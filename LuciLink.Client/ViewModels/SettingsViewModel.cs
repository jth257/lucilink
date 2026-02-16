using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LuciLink.Client.ViewModels;

/// <summary>
/// 설정 패널 ViewModel: 스트리밍 옵션 관리.
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private bool _isPanelVisible;
    private int _maxWidth;
    private int _maxHeight;
    private int _bitrate;
    private int _framerate;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        LoadFromSettings();

        TogglePanelCommand = new RelayCommand(TogglePanel);
        SaveCommand = new RelayCommand(Save);
        CloseCommand = new RelayCommand(() => IsPanelVisible = false);
    }

    public ICommand TogglePanelCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CloseCommand { get; }

    public bool IsPanelVisible
    {
        get => _isPanelVisible;
        set => SetProperty(ref _isPanelVisible, value);
    }

    /// <summary>최대 가로 해상도 (0 = 원본)</summary>
    public int MaxWidth
    {
        get => _maxWidth;
        set => SetProperty(ref _maxWidth, value);
    }

    /// <summary>최대 세로 해상도 (0 = 원본)</summary>
    public int MaxHeight
    {
        get => _maxHeight;
        set => SetProperty(ref _maxHeight, value);
    }

    /// <summary>비트레이트 (bps)</summary>
    public int Bitrate
    {
        get => _bitrate;
        set { SetProperty(ref _bitrate, value); OnPropertyChanged(nameof(BitrateMbps)); }
    }

    /// <summary>비트레이트 (Mbps 단위, UI 표시용)</summary>
    public double BitrateMbps
    {
        get => _bitrate / 1_000_000.0;
        set { Bitrate = (int)(value * 1_000_000); }
    }

    /// <summary>프레임레이트</summary>
    public int Framerate
    {
        get => _framerate;
        set => SetProperty(ref _framerate, value);
    }

    private string _videoEncoder = "";
    /// <summary>비디오 인코더 (빈 문자열 = 기본 인코더)</summary>
    public string VideoEncoder
    {
        get => _videoEncoder;
        set => SetProperty(ref _videoEncoder, value);
    }

    /// <summary>해상도 표시 텍스트</summary>
    public string ResolutionText => _maxWidth <= 0 || _maxHeight <= 0
        ? LocalizationManager.Get("Settings.ResOriginal")
        : $"{_maxWidth} × {_maxHeight}";

    private void TogglePanel()
    {
        if (!IsPanelVisible) LoadFromSettings();
        IsPanelVisible = !IsPanelVisible;
    }

    private void LoadFromSettings()
    {
        MaxWidth = _settings.MaxWidth;
        MaxHeight = _settings.MaxHeight;
        Bitrate = _settings.Bitrate;
        Framerate = _settings.Framerate;
        VideoEncoder = _settings.VideoEncoder;
    }

    private void Save()
    {
        _settings.MaxWidth = _maxWidth;
        _settings.MaxHeight = _maxHeight;
        _settings.Bitrate = _bitrate;
        _settings.Framerate = _framerate;
        _settings.VideoEncoder = _videoEncoder;
        _settings.Save();
        IsPanelVisible = false;
    }
}
