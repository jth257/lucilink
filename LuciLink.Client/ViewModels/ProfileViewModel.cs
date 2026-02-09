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

    public RelayCommand TogglePanelCommand { get; }
    public RelayCommand SubscribeCommand { get; }
    public RelayCommand LogoutCommand { get; }

    public event Action? LogoutRequested;

    /// <summary>체험 활성화 요청 이벤트 (MainViewModel에서 처리)</summary>
    public event Func<Task>? TrialActivationRequested;

    public ProfileViewModel()
    {
        TogglePanelCommand = new RelayCommand(() => IsPanelVisible = !IsPanelVisible);
        SubscribeCommand = new RelayCommand(OnSubscribe);
        LogoutCommand = new RelayCommand(() => LogoutRequested?.Invoke());
    }

    /// <summary>로그인 성공 시 프로필 업데이트</summary>
    public void SetUser(string name, string email, string subStatus, int trialDays)
    {
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
            "지금부터 3일간의 무료 체험이 시작됩니다.\n\n" +
            "체험 기간 동안 LuciLink의 모든 기능을 이용할 수 있습니다.\n" +
            "체험 종료 후에는 구독이 필요합니다.\n\n" +
            "계속하시겠습니까?",
            "무료 체험 시작",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            // 체험 활성화 실행
            if (TrialActivationRequested != null)
            {
                await TrialActivationRequested.Invoke();
            }
        }
        else
        {
            // "나중에" 선택 — 앱은 사용 가능하지만 기능 제한 상태 유지
            MessageBox.Show(
                "무료 체험은 나중에 다시 시작할 수 있습니다.\n" +
                "현재 기기 연결 기능을 이용하시려면 체험을 시작해주세요.",
                "알림",
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
                SubStatusText = "체험 대기";
                IsTrialCardVisible = true;
                TrialDaysLeft = 3;
                PlanName = "Free Trial (대기)";
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
        }
    }

    private void OnSubscribe()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://lucitella.com/checkout?product=lucilink") { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show(LocalizationManager.Get("Msg.BrowserError") + "\nhttps://lucitella.com/checkout?product=lucilink");
        }
    }
}
