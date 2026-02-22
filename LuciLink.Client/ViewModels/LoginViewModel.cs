using System.Windows;

namespace LuciLink.Client.ViewModels;

/// <summary>
/// 로그인 화면 ViewModel: Supabase 인증 + 구독 조회 + 체험 활성화
/// </summary>
public class LoginViewModel : ViewModelBase
{
    private readonly SupabaseAuthService _auth;

    private string _email = "";
    private string _errorText = "";
    private bool _isLoggedIn;
    private bool _isLoading;
    private bool _isSignupMode;

    public string Email
    {
        get => _email;
        set => SetProperty(ref _email, value);
    }

    public string ErrorText
    {
        get => _errorText;
        set => SetProperty(ref _errorText, value);
    }

    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        set => SetProperty(ref _isLoggedIn, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool IsSignupMode
    {
        get => _isSignupMode;
        set => SetProperty(ref _isSignupMode, value);
    }

    // 로그인 성공 시 호출: Name, Email, SubStatus, TrialDaysLeft, CreatedAt
    public event Action<string, string, string, int, DateTime?>? LoginSucceeded;

    public RelayCommand LoginCommand { get; }
    public RelayCommand SignupCommand { get; }
    public RelayCommand ForgotPasswordCommand { get; }

    public LoginViewModel(SupabaseAuthService auth)
    {
        _auth = auth;
        LoginCommand = new RelayCommand(_ => { }); // View에서 직접 TryLoginAsync 호출
        SignupCommand = new RelayCommand(OnSignup);
        ForgotPasswordCommand = new RelayCommand(_ => OnForgotPassword());
    }

    /// <summary>앱 시작 시 저장된 세션 복원 시도</summary>
    public async Task<bool> TryRestoreSessionAsync()
    {
        _auth.LoadSession();
        if (!_auth.IsLoggedIn) return false;

        // 토큰 갱신 시도
        var refreshed = await _auth.RefreshSessionAsync();
        if (!refreshed) return false;

        // 구독 조회
        var sub = await _auth.GetSubscriptionAsync();
        string status = "pending";
        int daysLeft = 0;
        if (sub != null)
        {
            (status, daysLeft) = ParseSubscription(sub);
        }

        IsLoggedIn = true;
        LoginSucceeded?.Invoke(
            _auth.UserEmail ?? "User",
            _auth.UserEmail ?? "",
            status,
            daysLeft,
            _auth.UserCreatedAt
        );
        return true;
    }

    /// <summary>이메일/비밀번호 로그인 (View에서 Password 직접 전달)</summary>
    public async Task TryLoginAsync(string email, string password)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            ErrorText = LocalizationManager.Get("Login.Error.Empty");
            return;
        }

        ErrorText = "";
        IsLoading = true;

        var result = await _auth.SignInAsync(email, password);

        if (!result.Success)
        {
            ErrorText = result.Error == "Invalid login credentials"
                ? LocalizationManager.Get("Login.Error.Invalid")
                : result.Error ?? LocalizationManager.Get("Login.Error.Invalid");
            IsLoading = false;
            return;
        }

        // 구독 조회
        var sub = await _auth.GetSubscriptionAsync();
        string status = "pending";
        int daysLeft = 0;

        if (sub != null)
        {
            (status, daysLeft) = ParseSubscription(sub);
        }

        IsLoading = false;
        IsLoggedIn = true;
        Email = email;

        LoginSucceeded?.Invoke(
            _auth.UserEmail ?? email,
            _auth.UserEmail ?? email,
            status,
            daysLeft,
            _auth.UserCreatedAt
        );
    }

    /// <summary>체험 활성화 — pending 상태에서 사용자 확인 후 호출 (기기 핑거프린트 포함)</summary>
    public async Task<TrialActivationResult> ActivateTrialAsync()
    {
        var deviceHash = DeviceFingerprint.Generate();
        return await _auth.ActivateTrialAsync(deviceHash);
    }

    public async Task LogoutAsync()
    {
        await _auth.SignOutAsync();
        IsLoggedIn = false;
        Email = "";
        ErrorText = "";
    }

    // Deprecated: 동기 로그인 (하위 호환성 유지, TryLoginAsync 사용 권장)
    public void TryLogin(string id, string password)
    {
        _ = TryLoginAsync(id, password);
    }

    public void Logout()
    {
        _ = LogoutAsync();
    }

    private static (string status, int daysLeft) ParseSubscription(SubscriptionInfo sub)
    {
        var status = sub.Status ?? "pending";
        int daysLeft = 0;

        if (status == "trial" && sub.TrialEndDate != null)
        {
            if (DateTime.TryParse(sub.TrialEndDate, out var endDate))
            {
                daysLeft = Math.Max(0, (int)Math.Ceiling((endDate - DateTime.UtcNow).TotalDays));
                if (daysLeft <= 0) status = "expired";
            }
        }
        else if ((status == "active" || status == "cancelled") && sub.CurrentPeriodEnd != null)
        {
            if (DateTime.TryParse(sub.CurrentPeriodEnd, out var endDate))
            {
                daysLeft = Math.Max(0, (int)Math.Ceiling((endDate - DateTime.UtcNow).TotalDays));
                // cancelled이면서 이미 만료됐으면 expired
                if (status == "cancelled" && daysLeft <= 0) status = "expired";
            }
        }

        return (status, daysLeft);
    }

    private void OnSignup(object? _)
    {
        IsSignupMode = !IsSignupMode;
        ErrorText = "";
    }

    /// <summary>앱 내 회원가입: 이메일/비밀번호/비밀번호확인</summary>
    public async Task TrySignupAsync(string email, string password, string confirmPassword)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            ErrorText = LocalizationManager.Get("Login.Error.Empty");
            return;
        }

        if (password != confirmPassword)
        {
            ErrorText = LocalizationManager.Get("Msg.PwMismatch");
            return;
        }

        ErrorText = "";
        IsLoading = true;

        var result = await _auth.SignUpAsync(email, password);

        IsLoading = false;

        if (result.Success)
        {
            MessageBox.Show(
                LocalizationManager.Get("Msg.SignupSuccess"),
                LocalizationManager.Get("Msg.SignupTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            IsSignupMode = false;
        }
        else
        {
            ErrorText = result.Error ?? LocalizationManager.Get("Msg.Error");
        }
    }

    /// <summary>비밀번호 재설정 이메일 발송</summary>
    private async void OnForgotPassword()
    {
        var email = Microsoft.VisualBasic.Interaction.InputBox(
            LocalizationManager.Get("Msg.ForgotPwPrompt"),
            LocalizationManager.Get("Msg.ForgotPwTitle"),
            _email);

        if (string.IsNullOrWhiteSpace(email)) return;

        var result = await _auth.ResetPasswordAsync(email);
        if (result.Success)
        {
            MessageBox.Show(
                LocalizationManager.Get("Msg.ForgotPwSent"),
                LocalizationManager.Get("Msg.ForgotPwTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                result.Error ?? LocalizationManager.Get("Msg.ForgotPwFailed"),
                LocalizationManager.Get("Msg.Error"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
