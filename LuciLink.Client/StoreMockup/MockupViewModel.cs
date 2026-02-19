using System.Windows.Media;
using System.Windows.Media.Imaging;
using LuciLink.Client.ViewModels;

namespace LuciLink.Client.StoreMockup;

/// <summary>
/// 스토어 목업 생성기 ViewModel.
/// Google Play 1080×1920 홍보용 스크린샷 합성을 위한 상태 관리.
/// </summary>
public class MockupViewModel : ViewModelBase
{
    // Google Play Store 그래픽 규격
    public const int CanvasWidth = 1080;
    public const int CanvasHeight = 1920;

    // 폰 프레임 레이아웃 (1080×1920 캔버스 내 절대 좌표)
    public const int PhoneLeft = 230;
    public const int PhoneTop = 360;
    public const int PhoneWidth = 620;
    public const int PhoneHeight = 1300;
    public const int PhoneCornerRadius = 48;

    // 폰 내부 스크린 영역 (베젤 안쪽)
    public const int ScreenLeft = 244;
    public const int ScreenTop = 392;
    public const int ScreenWidth = 592;
    public const int ScreenHeight = 1236;
    public const int ScreenCornerRadius = 14;

    private BitmapSource _screenshot;
    private int _selectedBackgroundIndex;
    private string _marketingText = "";
    private bool _isFreeUser = true;
    private string _statusText = "";

    public BitmapSource Screenshot
    {
        get => _screenshot;
        set => SetProperty(ref _screenshot, value);
    }

    public int SelectedBackgroundIndex
    {
        get => _selectedBackgroundIndex;
        set
        {
            if (SetProperty(ref _selectedBackgroundIndex, value))
                OnPropertyChanged(nameof(SelectedBackground));
        }
    }

    public Brush SelectedBackground
    {
        get
        {
            var p = Presets[Math.Clamp(_selectedBackgroundIndex, 0, Presets.Length - 1)];
            var brush = new LinearGradientBrush(p.StartColor, p.EndColor, 45);
            brush.Freeze();
            return brush;
        }
    }

    public string MarketingText
    {
        get => _marketingText;
        set => SetProperty(ref _marketingText, value);
    }

    public bool IsFreeUser
    {
        get => _isFreeUser;
        set => SetProperty(ref _isFreeUser, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public static BackgroundPreset[] Presets { get; } =
    [
        new("Soft Lavender", Color.FromRgb(0xE8, 0xDE, 0xF8), Color.FromRgb(0xBB, 0xDE, 0xFB)),
        new("Mint Breeze",   Color.FromRgb(0xC8, 0xE6, 0xC9), Color.FromRgb(0xB2, 0xEB, 0xF2)),
        new("Warm Sunset",   Color.FromRgb(0xFF, 0xCC, 0xBC), Color.FromRgb(0xFF, 0xE0, 0xB2)),
        new("Ocean Blue",    Color.FromRgb(0xBB, 0xDE, 0xFB), Color.FromRgb(0xE1, 0xBE, 0xE7)),
    ];

    public MockupViewModel(BitmapSource screenshot, bool isFreeUser = true)
    {
        _screenshot = screenshot;
        _isFreeUser = isFreeUser;
    }
}

public record BackgroundPreset(string Name, Color StartColor, Color EndColor);
