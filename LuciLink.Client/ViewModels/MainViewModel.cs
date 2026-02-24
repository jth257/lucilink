using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FFmpeg.AutoGen;
using LuciLink.Client.Rendering;
using LuciLink.Client.StoreMockup;
using LuciLink.Core.Adb;
using LuciLink.Core.Control;
using LuciLink.Core.Decoding;
using LuciLink.Core.Scrcpy;
using LuciLink.Core.Streaming;

namespace LuciLink.Client.ViewModels;

/// <summary>
/// 메인 화면 ViewModel: 연결/디코딩/캡처/APK관리/네비게이션/재연결
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly AdbClient _adb;
    private readonly ScrcpyServer _server;
    private readonly Dispatcher _dispatcher;
    private StreamReceiver? _receiver;
    private VideoDecoder? _decoder;
    private VideoRenderer? _renderer;
    private volatile bool _isRunning;
    private ControlSender? _controlSender;
    private InputManager? _inputManager;

    // 토큰/구독 자동 갱신 타이머
    private System.Threading.Timer? _tokenRefreshTimer;
    private System.Threading.Timer? _subscriptionCheckTimer;
    private string _currentSubStatus = "pending";

    // 연결 상태
    private Process? _serverProcess;
    private string? _deviceSerial;
    private int _localPort;

    // 화면 회전 감지
    private int _lastVideoWidth;
    private int _lastVideoHeight;

    // 재연결
    private const int MaxReconnectAttempts = 3;
    private const int ReconnectDelayMs = 5000;

    // 호환 모드 폴백 (인코더 에러 시 자동 전환)
    private bool _compatMode;
#pragma warning disable CS0414 // 향후 디버깅/진단에 활용
    private volatile bool _encoderErrorDetected;
#pragma warning restore CS0414

    // 바인딩 속성
    private bool _isConnected;
    private bool _canConnect = true;
    private string _connectButtonText = "";
    private string _statusText = "";
    private Brush _statusColor = Brushes.Gray;
    private string _deviceInfoText = "";
    private string _logText = "";
    private ImageSource? _videoSource;
    private bool _isPlaceholderVisible = true;
    private bool _isLogVisible;
    private bool _isDropOverlayVisible;
    private bool _isInstallOverlayVisible;
    private string _installStatusText = "";
    private string _windowTitle = "LuciLink (BETA)";

    // Supabase 인증
    private readonly SupabaseAuthService _authService;

    // 자식 ViewModel
    public LoginViewModel Login { get; }
    public ProfileViewModel Profile { get; }
    public SettingsViewModel Settings { get; }
    public ReportViewModel Report { get; }
    private AppSettings _appSettings;

    #region Binding Properties

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public bool CanConnect
    {
        get => _canConnect;
        set => SetProperty(ref _canConnect, value);
    }

    public string ConnectButtonText
    {
        get => _connectButtonText;
        set => SetProperty(ref _connectButtonText, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public Brush StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    public string DeviceInfoText
    {
        get => _deviceInfoText;
        set => SetProperty(ref _deviceInfoText, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public ImageSource? VideoSource
    {
        get => _videoSource;
        set => SetProperty(ref _videoSource, value);
    }

    public bool IsPlaceholderVisible
    {
        get => _isPlaceholderVisible;
        set => SetProperty(ref _isPlaceholderVisible, value);
    }

    public bool IsLogVisible
    {
        get => _isLogVisible;
        set => SetProperty(ref _isLogVisible, value);
    }

    public bool IsDropOverlayVisible
    {
        get => _isDropOverlayVisible;
        set => SetProperty(ref _isDropOverlayVisible, value);
    }

    public bool IsInstallOverlayVisible
    {
        get => _isInstallOverlayVisible;
        set => SetProperty(ref _isInstallOverlayVisible, value);
    }

    public string InstallStatusText
    {
        get => _installStatusText;
        set => SetProperty(ref _installStatusText, value);
    }

    public string WindowTitle
    {
        get => _windowTitle;
        set => SetProperty(ref _windowTitle, value);
    }

    #endregion

    #region Commands

    public ICommand ConnectToggleCommand { get; }
    public ICommand NavBackCommand { get; }
    public ICommand NavHomeCommand { get; }
    public ICommand NavRecentCommand { get; }
    public ICommand CaptureScreenCommand { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand ToggleLogCommand { get; }
    public ICommand ToggleLanguageCommand { get; }
    public ICommand OpenMockupCommand { get; }

    #endregion

    // VideoImage 컨트롤 참조 (InputManager 연결용)
    private System.Windows.Controls.Image? _videoImage;
    private Window? _window;

    // 화면 회전 시 윈도우 크기 조정 요청 이벤트
    public event Action<int, int>? RotationDetected;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        string adbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "adb.exe");
        _adb = new AdbClient(adbPath);
        _server = new ScrcpyServer(_adb);

        _authService = new SupabaseAuthService();
        _appSettings = AppSettings.Load();
        Login = new LoginViewModel(_authService);
        Profile = new ProfileViewModel();
        Settings = new SettingsViewModel(_appSettings);
        Report = new ReportViewModel(_adb);

        // 리포트 로그 연결
        Report.LogMessage += msg => Log(msg);

        // 로그인 성공 → 프로필 업데이트 + 구독 상태 동기화 + 베타 처리
        Login.LoginSucceeded += async (name, email, subStatus, trialDays, createdAt) =>
        {
            _currentSubStatus = subStatus ?? "pending";

            // 프로그램 로그인 기록 (첫 로그인만 UPSERT)
            await _authService.RecordProgramLoginAsync();

            // 베타 테스터 상태 확인
            var betaStatus = await _authService.CheckBetaTesterStatusAsync();

            Profile.SetUser(name, email, subStatus, trialDays, createdAt, betaStatus);
        };

        // 체험 활성화 확인 → activate_trial() RPC 호출
        Profile.TrialActivationRequested += OnTrialActivationAsync;

        // 피드백 제출
        Profile.FeedbackRequested += OnFeedbackAsync;

        // 로그아웃
        Profile.LogoutRequested += OnLogout;

        // 구독 클릭 → 폴링 간격 단축
        Profile.SubscribeClicked += OnSubscribeClicked;

        // Commands
        ConnectToggleCommand = new AsyncRelayCommand(OnConnectToggle, () => CanConnect);
        NavBackCommand = new AsyncRelayCommand(() => InjectKeyAsync(4));
        NavHomeCommand = new AsyncRelayCommand(() => InjectKeyAsync(3));
        NavRecentCommand = new AsyncRelayCommand(() => InjectKeyAsync(187));
        CaptureScreenCommand = new RelayCommand(OnCaptureScreen);
        CopyReportCommand = new AsyncRelayCommand(() => Report.EnterReportModeAsync());
        ToggleLogCommand = new RelayCommand(() => IsLogVisible = !IsLogVisible);
        ToggleLanguageCommand = new RelayCommand(OnToggleLanguage);
        OpenMockupCommand = new RelayCommand(OnOpenMockup);

        ConnectButtonText = LocalizationManager.Get("Button.Connect");
        StatusText = LocalizationManager.Get("Status.Disconnected");
        StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B6B"));

        // 백그라운드 자동 업데이트 확인
        _ = CheckForUpdatesAsync();

        // 토큰 자동 갱신 타이머 (50분마다, Supabase JWT 기본 만료: 1시간)
        _tokenRefreshTimer = new System.Threading.Timer(async _ =>
        {
            try { await _authService.RefreshSessionAsync(); }
            catch { /* 갱신 실패 무시 — 다음 주기에 재시도 */ }
        }, null, TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(50));

        // 구독 상태 주기 확인 (30분마다 — 결제 후 상태 자동 반영)
        _subscriptionCheckTimer = new System.Threading.Timer(async _ =>
        {
            await RefreshSubscriptionStatusAsync();
        }, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    /// <summary>구독 버튼 클릭 → 5초 간격 폴링으로 단축, 60초 후 원래 30분으로 복원</summary>
    private void OnSubscribeClicked()
    {
        Log("Subscribe clicked — polling interval shortened to 5s for 60s.");
        _subscriptionCheckTimer?.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        // 60초 후 원래 30분 간격으로 복원
        Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ =>
        {
            _subscriptionCheckTimer?.Change(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
            Log("Polling interval restored to 30min.");
        });
    }

    /// <summary>앱 시작 시 백그라운드 업데이트 확인</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            await Task.Delay(3000); // 앱 로딩 후 3초 대기
            var updateService = new UpdateService();
            var updateInfo = await updateService.CheckForUpdateAsync();

            if (updateInfo != null)
            {
                var result = MessageBox.Show(
                    LocalizationManager.Get("Msg.UpdateAvailable"),
                    LocalizationManager.Get("Msg.UpdateTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    Log("Downloading update...");
                    var downloaded = await updateService.DownloadAndApplyAsync(
                        updateInfo,
                        progress => _dispatcher.Invoke(() => Log($"Download: {progress}%")));

                    if (downloaded)
                    {
                        Log("Update downloaded. Restarting...");
                        updateService.ApplyAndRestart(updateInfo);
                    }
                    else
                    {
                        Log("Update download failed.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Update check skipped: {ex.Message}");
        }
    }

    /// <summary>VideoImage 컨트롤 참조 설정 (InputManager 연결용)</summary>
    public void SetVideoImageControl(System.Windows.Controls.Image image, Window window)
    {
        _videoImage = image;
        _window = window;
    }

    #region Connection

    private async Task OnConnectToggle()
    {
        if (_isRunning)
        {
            Disconnect();
        }
        else
        {
            await ConnectAsync();
        }
    }

    /// <summary>구독 상태 확인 — 기능 사용 가능 여부</summary>
    private bool CanUseApp =>
        _currentSubStatus == "trial" || _currentSubStatus == "active" || _currentSubStatus == "subscribed" || _currentSubStatus == "cancelled";

    private async Task ConnectAsync()
    {
        // 구독 상태 확인 — pending/expired는 연결 차단
        if (!CanUseApp)
        {
            var msg = _currentSubStatus == "pending"
                ? LocalizationManager.Get("Msg.TrialRequired")
                : LocalizationManager.Get("Msg.SubExpired");
            MessageBox.Show(msg, LocalizationManager.Get("Msg.FeatureRestricted"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CanConnect = false;

        try
        {
            // 1. 동적 포트 할당
            _localPort = FindAvailablePort();
            Log($"Using port: {_localPort}");

            // 2. 기기 감지
            var devices = await _adb.GetDevicesAsync().ConfigureAwait(false);
            if (devices.Count == 0)
            {
                _dispatcher.Invoke(() =>
                {
                    MessageBox.Show(LocalizationManager.Get("Msg.NoDevice"));
                    CanConnect = true;
                });
                return;
            }
            _deviceSerial = devices[0];
            Log($"Device found: {_deviceSerial}");

            // 3. 서버 푸시
            var serverPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "scrcpy-server.jar");
            await _server.PushServerAsync(_deviceSerial, serverPath).ConfigureAwait(false);

            // 4. 서버 시작 (설정값 또는 호환 모드 파라미터 적용)
            int maxSize = _compatMode ? 800 : Math.Max(_appSettings.MaxWidth, _appSettings.MaxHeight);
            int bitrate = _compatMode ? 2_000_000 : _appSettings.Bitrate;
            int maxFps = _compatMode ? 30 : _appSettings.Framerate;
            string? encoder = _compatMode ? "OMX.google.h264.encoder" 
                : (string.IsNullOrEmpty(_appSettings.VideoEncoder) ? null : _appSettings.VideoEncoder);

            Log($"Server params: maxSize={maxSize}, bitrate={bitrate/1_000_000.0}Mbps, fps={maxFps}, encoder={encoder ?? "(default)"}");
            _encoderErrorDetected = false;
            _serverProcess = await _server.StartServerAsync(
                _deviceSerial, maxSize, bitrate, maxFps, encoder).ConfigureAwait(false);
            Log($"Server started (socket: {_server.SocketName})");

            // 5. 포트 포워딩
            try { await _adb.RemoveForwardAsync(_deviceSerial, _localPort).ConfigureAwait(false); } catch { }
            await _adb.ForwardPortAsync(_deviceSerial, _localPort, _server.SocketName).ConfigureAwait(false);

            // 서버 로그 수집
            var serverLog = new System.Text.StringBuilder();
            StartServerLogReader(_serverProcess, serverLog);

            Log("Waiting for server...");
            await Task.Delay(2000).ConfigureAwait(false);

            // 6. 비디오 소켓
            _receiver = new StreamReceiver();
            await ConnectWithRetry(_receiver, _serverProcess, serverLog).ConfigureAwait(false);

            // 7. 컨트롤 소켓
            await Task.Delay(500).ConfigureAwait(false);
            CheckServerAlive(_serverProcess, serverLog);

            _controlSender = new ControlSender();
            try
            {
                Log("Connecting control socket...");
                await _controlSender.ConnectAsync(_localPort).ConfigureAwait(false);
                Log("Control connected.");
                _dispatcher.Invoke(() =>
                {
                    if (_videoImage != null && _window != null)
                        _inputManager = new InputManager(_controlSender, _videoImage, _window);
                });
            }
            catch (Exception ex) { Log($"Control failed: {ex.Message} (Video Only)"); }

            // 8. 기기명 읽기
            await Task.Delay(500).ConfigureAwait(false);
            CheckServerAlive(_serverProcess, serverLog);

            Log("Reading device name...");
            var deviceName = await _receiver.ReadDeviceNameAsync().ConfigureAwait(false);
            Log($"Device: {deviceName}");

            // 9. 디코딩 시작
            _isRunning = true;
            _decoder = new VideoDecoder();
            _renderer = new VideoRenderer(_dispatcher);
            _ = Task.Run(DecodeLoop);

            _dispatcher.Invoke(() =>
            {
                WindowTitle = $"LuciLink (BETA) - {deviceName}";
                ConnectButtonText = "Disconnect";
                CanConnect = true;
                IsPlaceholderVisible = false;
                IsConnected = true;
                UpdateStatus(true, deviceName);
                Report.SetDevice(_deviceSerial!, 0, 0); // 실제 크기는 DecodeLoop에서 갱신
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() =>
            {
                Log($"[Error] {ex.Message}");
                MessageBox.Show($"Connection failed:\n{ex.Message}");
                ConnectButtonText = LocalizationManager.Get("Button.Connect");
                CanConnect = true;
            });
            Cleanup();
        }
    }

    private void Disconnect()
    {
        Log("Disconnecting...");
        Cleanup();

        VideoSource = null;
        WindowTitle = "LuciLink (BETA)";
        ConnectButtonText = LocalizationManager.Get("Button.Connect");
        CanConnect = true;
        IsPlaceholderVisible = true;
        IsConnected = false;
        UpdateStatus(false, null);
        Log("Disconnected.");
    }

    private async void OnLogout()
    {
        if (_isRunning) Cleanup();

        VideoSource = null;
        WindowTitle = "LuciLink (BETA)";
        ConnectButtonText = LocalizationManager.Get("Button.Connect");
        CanConnect = true;
        IsPlaceholderVisible = true;
        IsConnected = false;
        UpdateStatus(false, null);

        await Login.LogoutAsync();
        Profile.IsPanelVisible = false;
    }

    /// <summary>앱 시작 시 저장된 세션으로 자동 로그인 시도</summary>
    public async Task<bool> TryRestoreSessionAsync()
    {
        var result = await Login.TryRestoreSessionAsync();
        if (result) await RefreshSubscriptionStatusAsync();
        return result;
    }

    /// <summary>구독 상태 주기 확인 (결제 후 자동 반영, 만료 감지)</summary>
    private async Task RefreshSubscriptionStatusAsync()
    {
        try
        {
            var sub = await _authService.GetSubscriptionAsync();
            if (sub == null) return;

            var oldStatus = _currentSubStatus;
            _currentSubStatus = sub.Status ?? "pending";

            // trial 만료 자동 감지
            if (_currentSubStatus == "trial" && sub.TrialEndDate != null)
            {
                if (DateTime.TryParse(sub.TrialEndDate, out var endDate) && endDate < DateTime.UtcNow)
                    _currentSubStatus = "expired";
            }

            // 상태 변경 시 UI 업데이트
            if (oldStatus != _currentSubStatus)
            {
                int daysLeft = 0;
                if (_currentSubStatus == "trial" && sub.TrialEndDate != null &&
                    DateTime.TryParse(sub.TrialEndDate, out var ed))
                    daysLeft = Math.Max(0, (int)Math.Ceiling((ed - DateTime.UtcNow).TotalDays));

                _dispatcher.Invoke(() =>
                {
                    Profile.SetUser(Profile.UserName, Profile.Email, _currentSubStatus, daysLeft);
                    Log($"Subscription status updated: {oldStatus} → {_currentSubStatus}");
                });
            }
        }
        catch { /* 네트워크 오류 무시 */ }
    }

    /// <summary>체험 활성화 (pending → trial) + 기기 어뷰징 검사</summary>
    private async Task OnTrialActivationAsync()
    {
        // 이메일 인증 확인
        if (!_authService.IsEmailVerified)
        {
            Log("Trial blocked: email not verified.");
            MessageBox.Show(
                LocalizationManager.Get("Msg.EmailVerifyRequired"),
                LocalizationManager.Get("Msg.EmailVerifyTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = await Login.ActivateTrialAsync();
        if (result.Success)
        {
            int daysLeft = 3;
            if (result.TrialEndDate != null && DateTime.TryParse(result.TrialEndDate, out var endDate))
            {
                daysLeft = Math.Max(0, (int)Math.Ceiling((endDate - DateTime.UtcNow).TotalDays));
            }
            _currentSubStatus = "trial";
            Profile.OnTrialActivated(daysLeft);
            Log($"Trial activated! {daysLeft} days remaining.");
        }
        else if (result.Status == "device_already_used")
        {
            Log("Trial blocked: device already used for trial by another account.");
            MessageBox.Show(
                LocalizationManager.Get("Msg.DeviceAlreadyUsed"),
                LocalizationManager.Get("Msg.DeviceAlreadyUsedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Profile.UpdateSubscriptionUI();
        }
        else
        {
            Log($"Trial activation failed: {result.Status}");
            MessageBox.Show(
                $"{LocalizationManager.Get("Msg.TrialActivationFailed")}\n{result.Status}",
                LocalizationManager.Get("Msg.Error"));
        }
    }

    /// <summary>피드백 제출 다이얼로그</summary>
    private async Task OnFeedbackAsync()
    {
        // 카테고리 선택
        var categoryResult = MessageBox.Show(
            "피드백 종류를 선택해주세요.\n\n[예] 버그 리포트\n[아니오] 기능 제안 / 일반 피드백",
            "🧪 베타 테스터 피드백",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (categoryResult == MessageBoxResult.Cancel) return;

        var category = categoryResult == MessageBoxResult.Yes ? "bug" : "general";
        var categoryLabel = category == "bug" ? "버그 리포트" : "기능 제안 / 일반";

        // 피드백 내용 입력
        var content = Microsoft.VisualBasic.Interaction.InputBox(
            $"[{categoryLabel}] 피드백 내용을 작성해주세요.\n\n(최소 20자 이상 작성해주세요)\n\n승인 시 정식 출시 후 평생 무료 혜택이 제공됩니다!",
            "🧪 베타 테스터 피드백",
            "");

        if (string.IsNullOrWhiteSpace(content)) return;

        if (content.Length < 20)
        {
            MessageBox.Show("피드백은 최소 20자 이상 작성해주세요.", "피드백 제출", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = await _authService.SubmitFeedbackAsync(content, category);

        if (result.Success)
        {
            MessageBox.Show(
                "피드백이 성공적으로 제출되었습니다! 🎉\n\n관리자 검토 후 평생 무료 혜택이 적용됩니다.",
                "피드백 제출 완료",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // 베타 상태 갱신
            var betaStatus = await _authService.CheckBetaTesterStatusAsync();
            Profile.SetUser(Profile.UserName, Profile.Email, _currentSubStatus, 0,
                _authService.UserCreatedAt, betaStatus);
        }
        else
        {
            MessageBox.Show(
                result.Error ?? "피드백 제출에 실패했습니다.",
                "오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    #endregion

    #region Decode Loop

    private unsafe void DecodeLoop()
    {
        try
        {
            Log("Starting decode...");

            try
            {
                uint ver = ffmpeg.avcodec_version();
                Log($"FFmpeg: avcodec v{ver >> 16}.{(ver >> 8) & 0xFF}.{ver & 0xFF}");
            }
            catch (Exception ex)
            {
                Log($"[FATAL] FFmpeg not found: {ex.Message}");
                return;
            }

            _decoder!.Initialize();
            var stream = _receiver!.GetRawStream();

            // 코덱 메타데이터 (12바이트)
            byte[] codecMeta = new byte[12];
            ReadExactly(stream, codecMeta, 0, 12);
            int codecId = BinaryPrimitives.ReadInt32BigEndian(codecMeta.AsSpan(0, 4));
            int initW = BinaryPrimitives.ReadInt32BigEndian(codecMeta.AsSpan(4, 4));
            int initH = BinaryPrimitives.ReadInt32BigEndian(codecMeta.AsSpan(8, 4));
            Log($"Codec: 0x{codecId:X8}, {initW}x{initH}");
            Log($"Codec meta hex: {BitConverter.ToString(codecMeta)}");

            byte[] headerBuf = new byte[12];
            byte[] packetBuf = new byte[1024 * 1024];
            int packetCount = 0;
            int decodeSuccess = 0;
            int decodeFail = 0;

            while (_isRunning)
            {
                ReadExactly(stream, headerBuf, 0, 12);
                long pts = BinaryPrimitives.ReadInt64BigEndian(headerBuf.AsSpan(0, 8));
                int size = BinaryPrimitives.ReadInt32BigEndian(headerBuf.AsSpan(8, 4));
                packetCount++;

                // 첫 10패킷 상세 로그
                if (packetCount <= 10)
                    Log($"[Pkt#{packetCount}] pts={pts}, size={size}, header={BitConverter.ToString(headerBuf)}");

                if (size <= 0 || size > 10 * 1024 * 1024)
                {
                    Log($"[Warn] Bad packet size: {size} at pkt#{packetCount}");
                    continue;
                }

                if (size > packetBuf.Length)
                    packetBuf = new byte[size + 1024 * 1024];

                ReadExactly(stream, packetBuf, 0, size);

                byte[] frameData = new byte[size];
                Array.Copy(packetBuf, frameData, size);

                // 첫 패킷 NAL 유닛 타입 로그
                if (packetCount <= 5 && size >= 5)
                    Log($"[Pkt#{packetCount}] NAL bytes: {BitConverter.ToString(frameData, 0, Math.Min(16, size))}");

                var frame = _decoder.Decode(frameData);
                if (frame != null)
                {
                    decodeSuccess++;
                    if (decodeSuccess <= 3)
                        Log($"[Decode OK #{decodeSuccess}] {frame->width}x{frame->height} fmt={(AVPixelFormat)frame->format}");

                    _renderer?.Render(frame);
                    _inputManager?.UpdateVideoSize(frame->width, frame->height);

                    int fw = frame->width;
                    int fh = frame->height;
                    bool rotated = (_lastVideoWidth != 0) &&
                                   ((_lastVideoWidth > _lastVideoHeight) != (fw > fh));
                    _lastVideoWidth = fw;
                    _lastVideoHeight = fh;

                    _dispatcher.InvokeAsync(() =>
                    {
                        if (VideoSource != _renderer?.ImageSource)
                        {
                            VideoSource = _renderer?.ImageSource;
                            IsPlaceholderVisible = false;
                            Log("First frame rendered!");
                        }

                        if (rotated)
                        {
                            RotationDetected?.Invoke(fw, fh);
                            Log($"Rotation detected: {fw}x{fh}");
                        }

                        DeviceInfoText = $"{fw} x {fh}";
                    }, DispatcherPriority.Background);
                }
                else
                {
                    decodeFail++;
                    if (decodeFail <= 5)
                        Log($"[Decode FAIL #{decodeFail}] pkt#{packetCount} size={size}");
                }

                // 50패킷마다 상태 요약
                if (packetCount % 50 == 0)
                    Log($"[Stats] pkts={packetCount}, decoded={decodeSuccess}, failed={decodeFail}");
            }
        }
        catch (Exception ex)
        {
            if (_isRunning)
            {
                _dispatcher.Invoke(() =>
                {
                    Log($"Connection lost: {ex.GetType().Name}");
                    TryReconnect();
                });
            }
        }
    }

    #endregion

    #region Auto Reconnect

    private async void TryReconnect()
    {
        for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            StatusText = $"{LocalizationManager.Get("Status.Reconnecting")} {attempt}/{MaxReconnectAttempts}";
            StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFA726"));
            Log($"Reconnect attempt {attempt}/{MaxReconnectAttempts}...");

            Cleanup();
            await Task.Delay(ReconnectDelayMs);

            try
            {
                await ConnectAsync();
                if (_isRunning)
                {
                    Log("Reconnected successfully!");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"Reconnect {attempt} failed: {ex.Message}");
            }
        }

        // 재연결 실패
        OnConnectionLost();
    }

    private void OnConnectionLost()
    {
        Cleanup();

        VideoSource = null;
        WindowTitle = "LuciLink (BETA)";
        ConnectButtonText = LocalizationManager.Get("Button.Connect");
        CanConnect = true;
        IsPlaceholderVisible = true;
        IsConnected = false;
        UpdateStatus(false, null);
        _lastVideoWidth = 0;
        _lastVideoHeight = 0;

        Log("Device disconnected.");
        MessageBox.Show(
            LocalizationManager.Get("Msg.Disconnected"),
            LocalizationManager.Get("Msg.DisconnectedTitle"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    #endregion

    #region Navigation

    private async Task InjectKeyAsync(int keyCode)
    {
        if (_controlSender == null || !_controlSender.IsConnected) return;
        await _controlSender.InjectKeyAsync(0, keyCode, 0);
        await _controlSender.InjectKeyAsync(1, keyCode, 0);
    }

    #endregion

    #region APK Install

    public void HandleDragEnter(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Any(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
            {
                e.Effects = DragDropEffects.Copy;
                IsDropOverlayVisible = true;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    public void HandleDragLeave()
    {
        IsDropOverlayVisible = false;
    }

    public async Task HandleDropAsync(DragEventArgs e)
    {
        IsDropOverlayVisible = false;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var apkFiles = files.Where(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (apkFiles.Length == 0)
        {
            MessageBox.Show(LocalizationManager.Get("Msg.ApkOnly"));
            return;
        }

        if (_deviceSerial == null)
        {
            MessageBox.Show(LocalizationManager.Get("Msg.ConnectFirst"));
            return;
        }

        foreach (var apkPath in apkFiles)
        {
            await InstallApkAsync(apkPath);
        }
    }

    private async Task InstallApkAsync(string apkPath)
    {
        var fileName = Path.GetFileName(apkPath);
        Log($"Installing: {fileName}");

        InstallStatusText = string.Format(LocalizationManager.Get("Msg.InstallProgress"), fileName);
        IsInstallOverlayVisible = true;

        try
        {
            var result = await Task.Run(async () =>
            {
                return await _adb.ExecuteCommandAsync(
                    $"-s {_deviceSerial} install -r \"{apkPath}\"").ConfigureAwait(false);
            });

            IsInstallOverlayVisible = false;

            if (result.Contains("Success"))
            {
                Log($"Installed: {fileName}");

                var packageName = await TryGetPackageNameAsync(apkPath);

                if (packageName != null)
                {
                    Log($"Auto-launching: {packageName}");
                    await LaunchAppAsync(packageName);
                }
                else
                {
                    Log($"Install complete (no package name found): {fileName}");
                }
            }
            else
            {
                Log($"Install failed: {result}");
                MessageBox.Show($"{LocalizationManager.Get("Msg.InstallFailed")}:\n{result}",
                    LocalizationManager.Get("Msg.Error"));
            }
        }
        catch (Exception ex)
        {
            IsInstallOverlayVisible = false;
            Log($"Install error: {ex.Message}");
            MessageBox.Show($"{LocalizationManager.Get("Msg.InstallError")}:\n{ex.Message}",
                LocalizationManager.Get("Msg.Error"));
        }
    }

    /// <summary>APK 설치 후 패키지명 조회 — adb shell pm list packages를 이용 (aapt 불필요)</summary>
    private async Task<string?> TryGetPackageNameAsync(string apkPath)
    {
        try
        {
            if (_deviceSerial == null) return null;

            // APK 파일명에서 패키지명 힌트 추출 (예: com.example.app-release.apk → com.example.app)
            var fileName = Path.GetFileNameWithoutExtension(apkPath)
                .Replace("-release", "").Replace("-debug", "").Replace("_release", "").Replace("_debug", "");

            // 설치된 패키지 목록에서 파일명과 매칭되는 패키지 검색
            var output = await Task.Run(async () =>
            {
                return await _adb.ExecuteCommandAsync(
                    $"-s {_deviceSerial} shell pm list packages -3").ConfigureAwait(false);
            });

            // 가장 최근 설치된 패키지 중 파일명과 유사한 것 찾기
            var packages = output.Split('\n')
                .Where(l => l.StartsWith("package:"))
                .Select(l => l.Replace("package:", "").Trim())
                .ToArray();

            // 정확한 패키지명 매칭 시도
            var match = packages.FirstOrDefault(p =>
                p.Contains(fileName, StringComparison.OrdinalIgnoreCase));

            return match;
        }
        catch { return null; }
    }

    private async Task LaunchAppAsync(string packageName)
    {
        try
        {
            Log($"Launching: {packageName}");
            await _adb.ExecuteCommandAsync(
                $"-s {_deviceSerial} shell monkey -p {packageName} -c android.intent.category.LAUNCHER 1");
            Log($"Launched: {packageName}");
        }
        catch (Exception ex) { Log($"Launch failed: {ex.Message}"); }
    }

    #endregion

    #region Capture & Report

    private void OnCaptureScreen()
    {
        if (VideoSource is not BitmapSource bitmap)
        {
            MessageBox.Show(LocalizationManager.Get("Msg.NoCaptureSource"));
            return;
        }

        try
        {
            // WriteableBitmap은 실시간 업데이트되므로 스냅샷을 깊은 복사
            var snapshot = CopyBitmap(bitmap);

            // DataObject로 DIB + PNG 스트림 형식 모두 설정
            // (Clipboard.SetImage만 사용하면 BGRA32 alpha 채널 문제로 빈 이미지가 됨)
            var dataObj = new DataObject();
            dataObj.SetImage(snapshot);

            var pngStream = new MemoryStream();
            var pngEncoder = new PngBitmapEncoder();
            pngEncoder.Frames.Add(BitmapFrame.Create(snapshot));
            pngEncoder.Save(pngStream);
            pngStream.Position = 0;
            dataObj.SetData("PNG", pngStream);

            Clipboard.SetDataObject(dataObj, true);
            Log("Screenshot copied to clipboard!");

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LuciLink");
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            SaveBitmapToPng(snapshot, filePath);
            Log($"Saved: {filePath}");

            MessageBox.Show(
                LocalizationManager.Get("Msg.CaptureSuccess"),
                LocalizationManager.Get("Msg.CaptureTitle"));
        }
        catch (Exception ex)
        {
            Log($"Capture failed: {ex.Message}");
            MessageBox.Show($"Capture failed: {ex.Message}");
        }
    }

    private async Task OnCopyReport()
    {
        if (_deviceSerial == null)
        {
            MessageBox.Show(LocalizationManager.Get("Msg.ConnectFirst"));
            return;
        }

        Log("Collecting error report...");

        string screenshotInfo = "(no screenshot)";
        BitmapSource? bitmapForClipboard = null;
        if (VideoSource is BitmapSource bitmap)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LuciLink");
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, $"report_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            SaveBitmapToPng(bitmap, filePath);
            screenshotInfo = filePath;
            bitmapForClipboard = bitmap;
        }

        string logcatOutput;
        try
        {
            logcatOutput = await Task.Run(async () =>
            {
                return await _adb.ExecuteCommandAsync(
                    $"-s {_deviceSerial} shell logcat -d -t 100 *:E").ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            logcatOutput = $"(logcat failed: {ex.Message})";
        }

        // 내부 앱 로그 (최근 50줄)
        var appLog = LogText ?? "";
        var appLogLines = appLog.Split('\n');
        if (appLogLines.Length > 50)
            appLog = string.Join('\n', appLogLines.Skip(appLogLines.Length - 50));

        // 시스템 정보
        var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var subStatus = Profile?.SubStatusText ?? "unknown";

        var report = $"""
            # LuciLink Error Report
            
            ## System Info
            - **Date**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
            - **App Version**: {appVersion}
            - **OS**: {Environment.OSVersion}
            - **Runtime**: .NET {Environment.Version}
            - **Subscription**: {subStatus}
            
            ## Device
            - **Serial**: {_deviceSerial}
            - **Screenshot**: {screenshotInfo}
            
            ## App Log (Recent)
            ```
            {appLog}
            ```
            
            ## Android Error Logs (logcat *:E)
            ```
            {logcatOutput}
            ```
            
            ---
            *위 정보를 분석하여 오류 원인을 파악하고 해결 방법을 제안해주세요.*
            """;

        var dataObj = new DataObject();
        if (bitmapForClipboard != null) dataObj.SetImage(bitmapForClipboard);
        dataObj.SetText(report);
        Clipboard.SetDataObject(dataObj, true);

        Log($"Error report copied to clipboard! ({logcatOutput.Split('\n').Length} log lines)");
        Log($"Screenshot: {screenshotInfo}");
        MessageBox.Show(
            LocalizationManager.Get("Msg.ReportCopied"),
            LocalizationManager.Get("Msg.ReportCopiedTitle"));
    }

    private static BitmapSource CopyBitmap(BitmapSource source)
    {
        // WriteableBitmap(BGRA32)에서 alpha가 0일 수 있으므로
        // 픽셀을 직접 복사하면서 alpha를 255(불투명)로 강제 설정
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4; // BGRA = 4 bytes per pixel
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        // Alpha 채널 강제 설정 (B=0, G=1, R=2, A=3)
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        var result = BitmapSource.Create(
            width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null,
            pixels, stride);
        result.Freeze();
        return result;
    }

    private static void SaveBitmapToPng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = new FileStream(path, FileMode.Create);
        encoder.Save(fs);
    }

    #endregion

    #region Store Mockup

    private void OnOpenMockup()
    {
        if (VideoSource is not BitmapSource bitmap)
        {
            MessageBox.Show(LocalizationManager.Get("Msg.NoCaptureSource"));
            return;
        }

        bool isFreeUser = _currentSubStatus != "active" && _currentSubStatus != "subscribed";
        var window = new MockupWindow(bitmap, isFreeUser);
        window.Show();
    }

    #endregion

    #region Language

    private void OnToggleLanguage()
    {
        LocalizationManager.Toggle();
        UpdateStatus(_isRunning, _isRunning ? _deviceSerial : null);
        if (!_isRunning)
        {
            ConnectButtonText = LocalizationManager.Get("Button.Connect");
        }
        Profile.UpdateSubscriptionUI();
    }

    #endregion

    #region Status & Logging

    private void UpdateStatus(bool connected, string? deviceName)
    {
        if (connected)
        {
            StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00D68F"));
            StatusText = LocalizationManager.Get("Status.Connected");
            DeviceInfoText = deviceName ?? _deviceSerial ?? "";
        }
        else
        {
            StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B6B"));
            StatusText = LocalizationManager.Get("Status.Disconnected");
            DeviceInfoText = "";
        }
    }

    public void Log(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        _dispatcher.Invoke(() => LogText += entry);
    }

    #endregion

    #region Helpers

    private static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void StartServerLogReader(Process proc, System.Text.StringBuilder log)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!proc.HasExited)
                {
                    var line = await proc.StandardError.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line))
                    {
                        log.AppendLine(line); Log($"[Scrcpy] {line}");
                        // 인코더 에러 감지 → 호환 모드 폴백 트리거
                        if (!_compatMode && (line.Contains("Video encoding error") || 
                            line.Contains("Could not open codec") ||
                            line.Contains("encoding error", StringComparison.OrdinalIgnoreCase)))
                        {
                            _encoderErrorDetected = true;
                            Log("[!] Encoder error detected — will retry in compat mode.");
                            _dispatcher.InvokeAsync(() => TryCompatModeFallback());
                        }
                    }
                }
            }
            catch { }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                while (!proc.HasExited)
                {
                    var line = await proc.StandardOutput.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line)) { log.AppendLine(line); Log($"[Scrcpy] {line}"); }
                }
            }
            catch { }
        });
    }

    /// <summary>인코더 에러 감지 시 호환 모드로 자동 재연결</summary>
    private async void TryCompatModeFallback()
    {
        if (_compatMode) return; // 이미 호환 모드였으면 중복 방지

        Log("Switching to compatibility mode (software encoder + 800p + 2Mbps)...");
        StatusText = LocalizationManager.Get("Status.CompatMode") ?? "호환 모드로 재연결 중...";
        StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFA726"));

        Cleanup();
        await Task.Delay(2000); // 인코더 릴리스 대기

        _compatMode = true;
        try
        {
            await ConnectAsync();
            if (_isRunning)
                Log("Compat mode connection successful!");
        }
        catch (Exception ex)
        {
            Log($"Compat mode failed: {ex.Message}");
            _compatMode = false;
            OnConnectionLost();
        }
    }

    private async Task ConnectWithRetry(StreamReceiver receiver, Process serverProc, System.Text.StringBuilder serverLog)
    {
        Log("Connecting video stream...");
        for (int retry = 0; retry < 10; retry++)
        {
            CheckServerAlive(serverProc, serverLog);
            try
            {
                await Task.Delay(1000).ConfigureAwait(false);
                await receiver.ConnectAsync(_localPort).ConfigureAwait(false);
                Log("Video stream connected!");
                return;
            }
            catch (Exception ex)
            {
                Log($"Retry {retry + 1}/10: {ex.Message}");
                if (retry >= 9)
                    throw new Exception($"Failed after 10 retries.\nServer alive: {!serverProc.HasExited}\nLog:\n{serverLog}");
            }
        }
    }

    private static void CheckServerAlive(Process proc, System.Text.StringBuilder log)
    {
        if (proc.HasExited)
            throw new Exception($"Server crashed (exit: {proc.ExitCode})\nLog:\n{log}");
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int n = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (n == 0) throw new EndOfStreamException($"Stream ended (got {totalRead}/{count} bytes)");
            totalRead += n;
        }
    }

    private void Cleanup()
    {
        _isRunning = false;

        try { _decoder?.Dispose(); } catch { }
        try { _renderer?.Dispose(); } catch { }
        _decoder = null;
        _renderer = null;

        try { _controlSender?.Dispose(); } catch { }
        try { _receiver?.Disconnect(); } catch { }
        _controlSender = null;
        _receiver = null;
        _inputManager = null;

        try
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                _serverProcess.Kill();
                _serverProcess.Dispose();
            }
        }
        catch { }
        _serverProcess = null;

        if (_deviceSerial != null && _localPort > 0)
        {
            try { _adb.RemoveForwardAsync(_deviceSerial, _localPort).Wait(2000); } catch { }
        }
    }

    public void OnClosed()
    {
        _tokenRefreshTimer?.Dispose();
        _subscriptionCheckTimer?.Dispose();
        Cleanup();
    }

    #endregion
}
