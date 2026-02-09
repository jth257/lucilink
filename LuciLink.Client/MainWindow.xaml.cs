using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using LuciLink.Client.ViewModels;

namespace LuciLink.Client;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;

    public MainWindow()
    {
        // 설정 로드
        _settings = AppSettings.Load();

        // 로컬라이제이션 초기화
        LocalizationManager.Initialize(_settings);

        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;

        InitializeComponent();

        FFmpegBinariesHelper.RegisterFFmpegBinaries();

        // VideoImage 컨트롤 참조 전달 (InputManager용)
        _vm.SetVideoImageControl(VideoImage, this);

        // 화면 회전 시 윈도우 크기 조정
        _vm.RotationDetected += AdjustWindowForRotation;

        // LogText 변경 시 자동 스크롤
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LogText))
            {
                LogTextBox.ScrollToEnd();
            }
        };

        // Enter 키로 로그인
        LoginPwBox.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) OnLogin(s, e); };
        LoginIdBox.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) LoginPwBox.Focus(); };

        // 설정에서 창 위치/크기 복원
        RestoreWindowPosition();

        // 앱 시작 시 세션 복원 시도
        Loaded += async (s, e) => await TryAutoLoginAsync();

        Closed += (s, e) =>
        {
            SaveWindowPosition();
            _vm.OnClosed();
        };
    }

    // ===== 앱 시작 시 저장된 세션으로 자동 로그인 =====
    private async Task TryAutoLoginAsync()
    {
        var restored = await _vm.TryRestoreSessionAsync();
        if (restored)
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            MainPanel.Visibility = Visibility.Visible;
        }
    }

    // ===== 로그인 (Supabase 비동기) =====
    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        await _vm.Login.TryLoginAsync(LoginIdBox.Text.Trim(), LoginPwBox.Password);
        if (_vm.Login.IsLoggedIn)
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            MainPanel.Visibility = Visibility.Visible;
        }
    }

    private async void OnLogout(object sender, RoutedEventArgs e)
    {
        await _vm.Login.LogoutAsync();
        _vm.Profile.IsPanelVisible = false;
        MainPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        LoginIdBox.Text = "";
        LoginPwBox.Password = "";
    }

    // ===== APK 드래그 앤 드롭 (DragEventArgs는 View에서만 접근) =====
    private void OnDragEnter(object sender, DragEventArgs e) => _vm.HandleDragEnter(e);
    private void OnDragLeave(object sender, DragEventArgs e) => _vm.HandleDragLeave();
    private async void OnDrop(object sender, DragEventArgs e) => await _vm.HandleDropAsync(e);

    // ===== 창 위치/크기 영속화 =====
    private void RestoreWindowPosition()
    {
        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.IsMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowPosition()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        _settings.IsMaximized = WindowState == WindowState.Maximized;
        _settings.Save();
    }

    // ===== 화면 회전 시 윈도우 크기 조정 =====
    private void AdjustWindowForRotation(int videoWidth, int videoHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0) return;

        double ratio = (double)videoWidth / videoHeight;
        var workArea = SystemParameters.WorkArea;
        double maxH = workArea.Height * 0.85;
        double maxW = workArea.Width * 0.85;

        double targetW, targetH;
        if (videoWidth > videoHeight)
        {
            targetW = Math.Min(maxW, 900);
            targetH = targetW / ratio + 120;
        }
        else
        {
            targetH = Math.Min(maxH, 900);
            targetW = (targetH - 120) * ratio;
        }

        targetW = Math.Max(MinWidth, Math.Min(targetW, maxW));
        targetH = Math.Max(MinHeight, Math.Min(targetH, maxH));

        Width = targetW;
        Height = targetH;
        Left = (workArea.Width - targetW) / 2 + workArea.Left;
        Top = (workArea.Height - targetH) / 2 + workArea.Top;
    }

    // ===== 하이퍼링크 클릭 → 브라우저 열기 =====
    private void OnHyperlinkNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
        catch { }
    }

    // ===== 커스텀 타이틀바 =====
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Border의 ActualWidth/ActualHeight를 Rect로 변환하는 컨버터 (둥근 모서리 클리핑용)
/// </summary>
public class RectConverter : IMultiValueConverter
{
    public static readonly RectConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is double w && values[1] is double h)
            return new Rect(0, 0, w, h);
        return new Rect();
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Boolean → Visibility 컨버터
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();
    public static readonly BoolToVisibilityConverter Inverse = new() { IsInverse = true };

    public bool IsInverse { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool boolVal && boolVal;
        if (IsInverse) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// TrialProgress (0~1) → Width 컨버터 (프로그레스바)
/// </summary>
public class ProgressToWidthConverter : IValueConverter
{
    public static readonly ProgressToWidthConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double progress)
            return progress * 260.0; // 카드 내부 폭 기준
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
