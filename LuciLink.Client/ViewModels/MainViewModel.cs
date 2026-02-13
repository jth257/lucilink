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
    private string _windowTitle = "LuciLink";

    // Supabase 인증
    private readonly SupabaseAuthService _authService;

    // 자식 ViewModel
    public LoginViewModel Login { get; }
    public ProfileViewModel Profile { get; }
    public SettingsViewModel Settings { get; }
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

        // 로그인 성공 → 프로필 업데이트
        Login.LoginSucceeded += (name, email, subStatus, trialDays) =>
        {
            Profile.SetUser(name, email, subStatus, trialDays);
        };

        // 체험 활성화 확인 → activate_trial() RPC 호출
        Profile.TrialActivationRequested += OnTrialActivationAsync;

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
        CopyReportCommand = new AsyncRelayCommand(OnCopyReport);
        ToggleLogCommand = new RelayCommand(() => IsLogVisible = !IsLogVisible);
        ToggleLanguageCommand = new RelayCommand(OnToggleLanguage);

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

            // 4. 서버 시작
            _serverProcess = await _server.StartServerAsync(_deviceSerial, 0, 0).ConfigureAwait(false);
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
                WindowTitle = $"LuciLink - {deviceName}";
                ConnectButtonText = "Disconnect";
                CanConnect = true;
                IsPlaceholderVisible = false;
                IsConnected = true;
                UpdateStatus(true, deviceName);
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
        WindowTitle = "LuciLink";
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
        WindowTitle = "LuciLink";
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

            byte[] headerBuf = new byte[12];
            byte[] packetBuf = new byte[1024 * 1024];

            while (_isRunning)
            {
                ReadExactly(stream, headerBuf, 0, 12);
                long pts = BinaryPrimitives.ReadInt64BigEndian(headerBuf.AsSpan(0, 8));
                int size = BinaryPrimitives.ReadInt32BigEndian(headerBuf.AsSpan(8, 4));

                if (size <= 0 || size > 10 * 1024 * 1024)
                {
                    Log($"[Warn] Bad packet size: {size}");
                    continue;
                }

                if (size > packetBuf.Length)
                    packetBuf = new byte[size + 1024 * 1024];

                ReadExactly(stream, packetBuf, 0, size);

                byte[] frameData = new byte[size];
                Array.Copy(packetBuf, frameData, size);

                var frame = _decoder.Decode(frameData);
                if (frame != null)
                {
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
        WindowTitle = "LuciLink";
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
                    var launch = MessageBox.Show(
                        string.Format(LocalizationManager.Get("Msg.InstallLaunchPrompt"), fileName),
                        LocalizationManager.Get("Msg.InstallSuccess"), MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (launch == MessageBoxResult.Yes)
                    {
                        await LaunchAppAsync(packageName);
                    }
                }
                else
                {
                    MessageBox.Show(
                        string.Format(LocalizationManager.Get("Msg.InstallComplete"), fileName),
                        LocalizationManager.Get("Msg.InstallSuccess"));
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

        Clipboard.SetImage(bitmap is BitmapSource bs ? bs : CopyBitmap(bitmap));
        Log("Screenshot copied to clipboard!");

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LuciLink");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png");

        SaveBitmapToPng(bitmap, filePath);
        Log($"Saved: {filePath}");
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
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    private static void SaveBitmapToPng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = new FileStream(path, FileMode.Create);
        encoder.Save(fs);
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
                    if (!string.IsNullOrEmpty(line)) { log.AppendLine(line); Log($"[Scrcpy] {line}"); }
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
