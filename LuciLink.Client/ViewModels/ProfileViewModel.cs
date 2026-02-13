using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace LuciLink.Client.ViewModels;

/// <summary>
/// 프로필 패널 ViewModel: 사용자 정보, 구독 상태, 체험 활성화 확인
/// </summary>
public class ProfileViewModel : ViewModelBase
{
    private string _userName = "Unknown";
    private string _email = "";
    private string _subscriptionStatus = "pending";
    private int _trialDaysLeft = 0;
    private string _subStatusText = "";
    private Brush _subStatusColor = Brushes.Gray;
    private string _planName = "";
    private double _trialProgress = 0;
    private bool _isTrialCardVisible;
    private bool _isSubscribeVisible;
    private bool _isPanelVisible;
    private bool _isTrialActivationVisible; // 체험 시작 확인 다이얼로그
    private bool _isYearlyPlan;

    public string UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value);
    }

    public string Email
    {
        get => _email;
        set => SetProperty(ref _email, value);
    }

    public string SubStatusText
    {
        get => _subStatusText;
        set => SetProperty(ref _subStatusText, value);
    }

    public Brush SubStatusColor
    {
        get => _subStatusColor;
        set => SetProperty(ref _subStatusColor, value);
    }

    public string PlanName
    {
        get => _planName;
        set => SetProperty(ref _planName, value);
    }

    public int TrialDaysLeft
    {
        get => _trialDaysLeft;
        set => SetProperty(ref _trialDaysLeft, value);
    }

    public double TrialProgress
    {
        get => _trialProgress;
        set => SetProperty(ref _trialProgress, value);
    }

    public bool IsTrialCardVisible
    {
        get => _isTrialCardVisible;
        set => SetProperty(ref _isTrialCardVisible, value);
    }

    public bool IsSubscribeVisible
    {
        get => _isSubscribeVisible;
        set => SetProperty(ref _isSubscribeVisible, value);
    }

    public bool IsPanelVisible
    {
        get => _isPanelVisible;
        set => SetProperty(ref _isPanelVisible, value);
    }

    public bool IsTrialActivationVisible
    {
        get => _isTrialActivationVisible;
        set => SetProperty(ref _isTrialActivationVisible, value);
    }

    public bool IsYearlyPlan
    {
        get => _isYearlyPlan;
        set
        {
            SetProperty(ref _isYearlyPlan, value);
            OnPropertyChanged(nameof(SelectedPlanSlug));
        }
    }

    public string SelectedPlanSlug => _isYearlyPlan ? "lucilink-yearly" : "lucilink";

    public RelayCommand TogglePanelCommand { get; }
    public RelayCommand SubscribeCommand { get; }
    public RelayCommand LogoutCommand { get; }
    public RelayCommand SelectMonthlyCommand { get; }
    public RelayCommand SelectYearlyCommand { get; }

    public event Action? LogoutRequested;

    /// <summary>구독 버튼 클릭 이벤트 (MainViewModel에서 폴링 간격 단축)</summary>
    public event Action? SubscribeClicked;

    /// <summary>체험 활성화 요청 이벤트 (MainViewModel에서 처리)</summary>
    public event Func<Task>? TrialActivationRequested;

    public ProfileViewModel()
    {
        TogglePanelCommand = new RelayCommand(() => IsPanelVisible = !IsPanelVisible);
        SubscribeCommand = new RelayCommand(OnSubscribe);
        LogoutCommand = new RelayCommand(() => LogoutRequested?.Invoke());
        SelectMonthlyCommand = new RelayCommand(() => IsYearlyPlan = false);
        SelectYearlyCommand = new RelayCommand(() => IsYearlyPlan = true);
    }

    /// <summary>로그인 성공 시 프로필 업데이트</summary>
    public void SetUser(string name, string email, string subStatus, int trialDays)
    {
        System.Diagnostics.Debug.WriteLine($"[PROFILE] SetUser called: name={name}, email={email}, subStatus={subStatus}, trialDays={trialDays}");
        UserName = name;
        Email = email;
        _subscriptionStatus = subStatus;
        _trialDaysLeft = trialDays;
        UpdateSubscriptionUI();

        // pending 상태면 체험 시작 확인 다이얼로그 표시
        if (subStatus == "pending")
        {
            ShowTrialActivationDialog();
        }
    }

    /// <summary>체험 시작 확인 다이얼로그 표시</summary>
    private async void ShowTrialActivationDialog()
    {
        var result = MessageBox.Show(
            LocalizationManager.Get("Msg.TrialStartConfirm"),
            LocalizationManager.Get("Msg.TrialStartTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            if (TrialActivationRequested != null)
            {
                await TrialActivationRequested.Invoke();
            }
        }
        else
        {
            MessageBox.Show(
                LocalizationManager.Get("Msg.TrialLater"),
                LocalizationManager.Get("Msg.Notice"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    /// <summary>체험이 활성화된 후 UI 업데이트</summary>
    public void OnTrialActivated(int daysLeft)
    {
        _subscriptionStatus = "trial";
        _trialDaysLeft = daysLeft;
        UpdateSubscriptionUI();
    }

    public void UpdateSubscriptionUI()
    {
        switch (_subscriptionStatus)
        {
            case "subscribed":
            case "active":
                SubStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00D68F"));
                SubStatusText = LocalizationManager.Get("Profile.Subscribed");
                IsTrialCardVisible = false;
                PlanName = "Pro Plan";
                IsSubscribeVisible = false;
                break;

            case "cancelled":
                SubStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFA726"));
                SubStatusText = LocalizationManager.Get("Profile.Cancelled");
                IsTrialCardVisible = false;
                PlanName = "Pro Plan";
                IsSubscribeVisible = true;
                break;

            case "trial":
                SubStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6C5CE7"));
                SubStatusText = LocalizationManager.Get("Profile.Trial");
                IsTrialCardVisible = true;
                TrialDaysLeft = _trialDaysLeft;
                PlanName = "Free Trial";
                IsSubscribeVisible = true;
                TrialProgress = Math.Max(0, Math.Min(1, _trialDaysLeft / 3.0)); // 3일 기준
                break;

            case "pending":
                SubStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFA726"));
                SubStatusText = LocalizationManager.Get("Profile.TrialPending");
                IsTrialCardVisible = true;
                TrialDaysLeft = 3;
                PlanName = LocalizationManager.Get("Profile.PlanTrialPending");
                IsSubscribeVisible = false;
                TrialProgress = 1.0;
                break;

            case "expired":
                SubStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B6B"));
                SubStatusText = LocalizationManager.Get("Profile.Expired");
                IsTrialCardVisible = true;
                TrialDaysLeft = 0;
                PlanName = LocalizationManager.Get("Profile.PlanExpired");
                IsSubscribeVisible = true;
                TrialProgress = 0;
                break;

            default:
                SubStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFA726"));
                SubStatusText = LocalizationManager.Get("Profile.TrialPending");
                IsTrialCardVisible = false;
                PlanName = _subscriptionStatus;
                IsSubscribeVisible = true;
                break;
        }
    }

    private void OnSubscribe()
    {
        try
        {
            var slug = SelectedPlanSlug;
            var plan = IsYearlyPlan ? "yearly" : "monthly";
            var provider = LocalizationManager.CurrentLanguage == "ko" ? "toss" : "lemonsqueezy";
            var url = $"https://lucitella.com/checkout?product={slug}&plan={plan}&provider={provider}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            SubscribeClicked?.Invoke();
        }
        catch
        {
            MessageBox.Show(LocalizationManager.Get("Msg.BrowserError"));
        }
    }
}
