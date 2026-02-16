using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LuciLink.Client.Rendering;
using LuciLink.Core;
using LuciLink.Core.Adb;

namespace LuciLink.Client.ViewModels;

/// <summary>
/// 고도화된 AI 리포트 모드를 관리하는 ViewModel.
/// UI XML 덤프 → 좌표 매핑 → 비주얼 어노테이션 → 마크다운 리포트 생성
/// </summary>
public class ReportViewModel : ViewModelBase
{
    private readonly AdbClient _adb;
    private readonly UiDumpService _dumpService = new();
    private readonly UiXmlParser _parser = new();

    private bool _isReportMode;
    private BitmapSource? _capturedScreenshot;
    private List<UiElementInfo> _uiElements = new();
    private UiElementInfo? _selectedElement;
    private string _selectedElementInfo = "";
    private string _rawUiXml = "";
    private string _statusMessage = "";
    private bool _isPanelVisible;
    private string? _deviceSerial;
    private int _deviceWidth;
    private int _deviceHeight;

    // 캡처된 이미지 저장 경로
    private string? _capturedImagePath;

    // 어노테이션 목록
    public ObservableCollection<ReportAnnotation> Annotations { get; } = new();

    // UI 접근용 콜백 (Canvas 참조)
    public event Action<ReportAnnotation>? AnnotationAdded;
    public event Action? AnnotationsCleared;

    // 로그 콜백
    public event Action<string>? LogMessage;

    #region Binding Properties

    public bool IsReportMode
    {
        get => _isReportMode;
        set
        {
            if (SetProperty(ref _isReportMode, value))
                OnPropertyChanged(nameof(IsReportModeVisible));
        }
    }

    public Visibility IsReportModeVisible =>
        _isReportMode ? Visibility.Visible : Visibility.Collapsed;

    public BitmapSource? CapturedScreenshot
    {
        get => _capturedScreenshot;
        set => SetProperty(ref _capturedScreenshot, value);
    }

    public UiElementInfo? SelectedElement
    {
        get => _selectedElement;
        set
        {
            if (SetProperty(ref _selectedElement, value))
                UpdateSelectedElementInfo();
        }
    }

    public string SelectedElementInfo
    {
        get => _selectedElementInfo;
        set => SetProperty(ref _selectedElementInfo, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsPanelVisible
    {
        get => _isPanelVisible;
        set => SetProperty(ref _isPanelVisible, value);
    }

    #endregion

    #region Commands

    public ICommand EnterReportModeCommand { get; }
    public ICommand GenerateReportCommand { get; }
    public ICommand ExitReportModeCommand { get; }

    #endregion

    public ReportViewModel(AdbClient adb)
    {
        _adb = adb;
        EnterReportModeCommand = new AsyncRelayCommand(EnterReportModeAsync);
        GenerateReportCommand = new AsyncRelayCommand(GenerateReportAsync);
        ExitReportModeCommand = new RelayCommand(ExitReportMode);
    }

    /// <summary>디바이스 정보 설정 (연결 시 호출)</summary>
    public void SetDevice(string deviceSerial, int width, int height)
    {
        _deviceSerial = deviceSerial;
        _deviceWidth = width;
        _deviceHeight = height;
    }

    #region Report Mode

    /// <summary>리포트 모드 진입: 스크린샷 캡처 + UI dump</summary>
    public async Task EnterReportModeAsync()
    {
        if (_deviceSerial == null)
        {
            MessageBox.Show(LocalizationManager.Get("Msg.ConnectFirst"));
            return;
        }

        StatusMessage = LocalizationManager.Get("Report.DumpProgress");
        IsReportMode = true;
        IsPanelVisible = false;

        try
        {
            // 1. 스크린샷 캡처
            await CaptureScreenshotAsync();

            // 2. UI XML 덤프
            LogMessage?.Invoke("Dumping UI hierarchy...");
            _rawUiXml = await Task.Run(() =>
                _dumpService.DumpUiXmlAsync(_adb, _deviceSerial));

            // 3. XML 파싱
            _uiElements = _parser.Parse(_rawUiXml);
            LogMessage?.Invoke($"UI dump complete: {_uiElements.Count} elements found.");

            StatusMessage = string.Format(
                LocalizationManager.Get("Report.ElementCount"), _uiElements.Count);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"UI dump failed: {ex.Message}");
            StatusMessage = $"Error: {ex.Message}";
            // 에러가 나도 리포트 모드는 유지 (스크린샷이라도 사용)
        }
    }

    /// <summary>스크린샷 캡처 (현재 VideoSource에서)</summary>
    private async Task CaptureScreenshotAsync()
    {
        // adb exec-out screencap -p 로 바이트 배열 캡처
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LuciLink");
            Directory.CreateDirectory(dir);
            _capturedImagePath = Path.Combine(dir, $"report_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            // adb screencap을 사용하여 직접 캡처
            await _adb.ExecuteCommandAsync(
                $"-s {_deviceSerial} shell screencap -p /sdcard/lucilink_capture.png");

            // Pull the file
            var localTemp = Path.GetTempFileName();
            await _adb.ExecuteCommandAsync(
                $"-s {_deviceSerial} pull /sdcard/lucilink_capture.png \"{localTemp}\"");

            // Cleanup remote file
            try { await _adb.ExecuteCommandAsync($"-s {_deviceSerial} shell rm /sdcard/lucilink_capture.png"); }
            catch { }

            // Load as BitmapImage
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(localTemp, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            CapturedScreenshot = bitmap;

            // Save a copy
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var fs = new FileStream(_capturedImagePath, FileMode.Create);
            encoder.Save(fs);

            // Get device dimensions from the captured image
            _deviceWidth = bitmap.PixelWidth;
            _deviceHeight = bitmap.PixelHeight;

            // Cleanup temp
            try { File.Delete(localTemp); } catch { }

            LogMessage?.Invoke($"Screenshot captured: {_capturedImagePath}");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Screenshot capture failed: {ex.Message}");
        }
    }

    // 어노테이션 제거 콜백
    public event Action<ReportAnnotation>? AnnotationRemoved;

    /// <summary>
    /// 안드로이드 좌표로 UI 요소 검색.
    /// ReportOverlayCanvas에서 변환된 좌표로 호출됩니다.
    /// </summary>
    public void SelectElementAt(int androidX, int androidY)
    {
        if (_uiElements.Count == 0) return;

        var element = _parser.FindElementAt(_uiElements, androidX, androidY);
        if (element == null) return;

        // 이미 어노테이션이 있는지 확인
        var existingAnno = Annotations.FirstOrDefault(a =>
            a.Element.ResourceId == element.ResourceId &&
            a.Element.BoundsString == element.BoundsString);

        if (existingAnno != null)
        {
            // 토글: 이미 있으면 제거
            Annotations.Remove(existingAnno);
            AnnotationRemoved?.Invoke(existingAnno);
            
            if (SelectedElement == element)
            {
                SelectedElement = null;
                IsPanelVisible = false;
            }
        }
        else
        {
            // 새 어노테이션 추가
            SelectedElement = element;
            IsPanelVisible = true;
            var anno = ReportAnnotation.FromElement(element);
            Annotations.Add(anno);
            AnnotationAdded?.Invoke(anno);
        }
    }

    /// <summary>어노테이션에 메모 설정</summary>
    public void SetAnnotationMemo(ReportAnnotation annotation, string memo)
    {
        annotation.Memo = memo;
    }

    /// <summary>리포트 모드 종료</summary>
    private void ExitReportMode()
    {
        IsReportMode = false;
        IsPanelVisible = false;
        CapturedScreenshot = null;
        _uiElements.Clear();
        Annotations.Clear();
        AnnotationsCleared?.Invoke();
        SelectedElement = null;
        StatusMessage = "";
    }

    #endregion

    #region Report Generation

    /// <summary>마크다운 리포트 생성 → 클립보드 복사 + 파일 저장</summary>
    private async Task GenerateReportAsync()
    {
        StatusMessage = LocalizationManager.Get("Report.Generating");

        try
        {
            // 1. Logcat 수집
            string logcatOutput = "(logcat not available)";
            if (_deviceSerial != null)
            {
                try
                {
                    logcatOutput = await Task.Run(() =>
                        _adb.ExecuteCommandAsync(
                            $"-s {_deviceSerial} shell logcat -d -t 100 *:E"));
                }
                catch (Exception ex) { logcatOutput = $"(logcat failed: {ex.Message})"; }
            }

            // 2. 시스템 정보
            var appVersion = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? "unknown";

            // 3. 어노테이션 정보 수집
            var annotationSection = BuildAnnotationSection();

            // 4. 수정 가이드 이미지 생성 및 저장
            string modifiedImagePath = "(no modified image)";
            BitmapSource? modifiedImage = null;
            if (CapturedScreenshot != null && Annotations.Count > 0)
            {
                modifiedImagePath = await SaveAnnotatedImageAsync();
                // 이미지 로드
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(modifiedImagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    modifiedImage = bitmap;
                }
                catch { }
            }

            // 5. 마크다운 리포트 조립
            var report = new StringBuilder();
            report.AppendLine("# LuciLink AI Enhanced Report");
            report.AppendLine();
            report.AppendLine("## System Info");
            report.AppendLine($"- **Date**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"- **App Version**: {appVersion}");
            report.AppendLine($"- **OS**: {Environment.OSVersion}");
            report.AppendLine($"- **Runtime**: .NET {Environment.Version}");
            report.AppendLine();

            report.AppendLine("## Device");
            report.AppendLine($"- **Serial**: {_deviceSerial}");
            report.AppendLine($"- **Screen Size**: {_deviceWidth}x{_deviceHeight}");
            report.AppendLine($"- **Original Screenshot**: {_capturedImagePath}");
            if (modifiedImagePath != "(no modified image)")
                report.AppendLine($"- **Annotated Screenshot**: {modifiedImagePath}");
            report.AppendLine();

            // 어노테이션 섹션
            if (annotationSection.Length > 0)
            {
                report.AppendLine("## UI Elements & Modifications");
                report.AppendLine();
                report.Append(annotationSection);
                report.AppendLine();
            }

            // 선택된 요소들의 XML 상세 정보
            if (Annotations.Count > 0)
            {
                report.AppendLine("## Selected UI Elements (Raw XML Attributes)");
                report.AppendLine();
                foreach (var anno in Annotations)
                {
                    report.AppendLine($"### {anno.Element}");
                    report.AppendLine("```");
                    report.AppendLine(anno.Element.RawAttributes);
                    report.AppendLine("```");
                    report.AppendLine();
                }
            }

            // Logcat
            report.AppendLine("## Android Error Logs (logcat *:E)");
            report.AppendLine("```");
            report.AppendLine(logcatOutput);
            report.AppendLine("```");
            report.AppendLine();
            report.AppendLine("---");
            report.AppendLine("*위 정보를 분석하여 UI 수정 방법을 구체적으로 제안해주세요. " +
                              "각 요소의 Bounds와 메모를 참고하세요.*");

            // 6. 클립보드에 복사 (PNG 포맷 포함 — 웹 앱 호환)
            var clipImage = modifiedImage ?? CapturedScreenshot;
            var dataObj = new DataObject();
            if (clipImage != null)
            {
                // DIB 형식 (기본 Windows 앱 호환)
                dataObj.SetImage(clipImage);
                // PNG 스트림 형식 (웹 앱 — Gemini, ChatGPT 등 호환)
                var pngStream = new MemoryStream();
                var pngEncoder = new PngBitmapEncoder();
                pngEncoder.Frames.Add(BitmapFrame.Create(clipImage));
                pngEncoder.Save(pngStream);
                pngStream.Position = 0;
                dataObj.SetData("PNG", pngStream);
            }
            dataObj.SetText(report.ToString());
            Clipboard.SetDataObject(dataObj, true);

            // 7. 파일로도 저장
            var reportDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LuciLink");
            Directory.CreateDirectory(reportDir);
            var reportPath = Path.Combine(reportDir, $"ai_report_{DateTime.Now:yyyyMMdd_HHmmss}.md");
            await File.WriteAllTextAsync(reportPath, report.ToString());

            LogMessage?.Invoke($"AI report saved: {reportPath}");
            LogMessage?.Invoke($"AI report copied to clipboard! ({Annotations.Count} annotations)");

            MessageBox.Show(
                LocalizationManager.Get("Report.Copied"),
                LocalizationManager.Get("Msg.ReportCopiedTitle"));

            StatusMessage = "";
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Report generation failed: {ex.Message}");
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    /// <summary>어노테이션 정보를 마크다운 테이블로 생성</summary>
    private string BuildAnnotationSection()
    {
        if (Annotations.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("| # | Element | Class | Original Bounds | Modified Bounds | Memo |");
        sb.AppendLine("|---|---------|-------|-----------------|-----------------|------|");

        int index = 1;
        foreach (var anno in Annotations)
        {
            var id = string.IsNullOrEmpty(anno.Element.ResourceId) ? "(none)" : anno.Element.ResourceId;
            var className = anno.Element.ClassName.Split('.').LastOrDefault() ?? anno.Element.ClassName;
            var originalBounds = $"[{anno.OriginalLeft},{anno.OriginalTop}][{anno.OriginalRight},{anno.OriginalBottom}]";
            var modifiedBounds = anno.HasModifiedPosition
                ? $"[{anno.ModifiedLeft},{anno.ModifiedTop}][{anno.ModifiedRight},{anno.ModifiedBottom}]"
                : "(unchanged)";
            var memo = string.IsNullOrEmpty(anno.Memo) ? "" : anno.Memo;

            sb.AppendLine($"| {index} | `{id}` | `{className}` | `{originalBounds}` | `{modifiedBounds}` | {memo} |");
            index++;
        }

        return sb.ToString();
    }

    /// <summary>어노테이션이 반영된 스크린샷을 저장</summary>
    private Task<string> SaveAnnotatedImageAsync()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LuciLink");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"report_annotated_{DateTime.Now:yyyyMMdd_HHmmss}.png");

        if (CapturedScreenshot == null)
            return Task.FromResult("(no screenshot)");

        // DrawingVisual을 사용하여 스크린샷 위에 어노테이션 그리기
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // 원본 스크린샷 그리기
            dc.DrawImage(CapturedScreenshot,
                new Rect(0, 0, CapturedScreenshot.PixelWidth, CapturedScreenshot.PixelHeight));

            foreach (var anno in Annotations)
            {
                // 원본 바운딩 박스 (빨간 점선)
                var origPen = new Pen(Brushes.Red, 4) { DashStyle = DashStyles.Dash };
                var origRect = new Rect(
                    anno.OriginalLeft, anno.OriginalTop,
                    anno.OriginalRight - anno.OriginalLeft,
                    anno.OriginalBottom - anno.OriginalTop);
                dc.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(40, 255, 0, 0)),
                    origPen, origRect);

                // 수정된 위치 (녹색)
                if (anno.HasModifiedPosition)
                {
                    var modPen = new Pen(new SolidColorBrush(Color.FromRgb(0, 200, 83)), 4);
                    var modRect = new Rect(
                        anno.ModifiedLeft, anno.ModifiedTop,
                        anno.ModifiedRight - anno.ModifiedLeft,
                        anno.ModifiedBottom - anno.ModifiedTop);
                    dc.DrawRectangle(
                        new SolidColorBrush(Color.FromArgb(40, 0, 200, 83)),
                        modPen, modRect);

                    // 화살표
                    var arrowPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 165, 0)), 3)
                    {
                        DashStyle = DashStyles.DashDot
                    };
                    dc.DrawLine(arrowPen,
                        new Point(origRect.X + origRect.Width / 2, origRect.Y + origRect.Height / 2),
                        new Point(modRect.X + modRect.Width / 2, modRect.Y + modRect.Height / 2));
                }

                // 메모 텍스트
                if (!string.IsNullOrEmpty(anno.Memo))
                {
                    var text = new FormattedText(
                        anno.Memo,
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"),
                        24, Brushes.White, 96);

                    var textBg = new Rect(
                        anno.OriginalLeft,
                        anno.OriginalTop - 36,
                        text.Width + 16,
                        text.Height + 8);
                    dc.DrawRoundedRectangle(
                        new SolidColorBrush(Color.FromArgb(220, 30, 30, 30)),
                        null, textBg, 4, 4);
                    dc.DrawText(text,
                        new Point(anno.OriginalLeft + 8, anno.OriginalTop - 32));
                }

                // 요소 ID 라벨
                var idText = string.IsNullOrEmpty(anno.Element.ResourceId)
                    ? anno.Element.ClassName.Split('.').LastOrDefault() ?? "?"
                    : anno.Element.ResourceId.Split('/').LastOrDefault() ?? "?";

                var idFormatted = new FormattedText(
                    idText,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                        FontWeights.Bold, FontStretches.Normal),
                    20, Brushes.White, 96);

                var labelBg = new Rect(
                    anno.OriginalLeft,
                    anno.OriginalBottom + 4,
                    idFormatted.Width + 12,
                    idFormatted.Height + 6);
                dc.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromArgb(200, 255, 59, 48)),
                    null, labelBg, 4, 4);
                dc.DrawText(idFormatted,
                    new Point(anno.OriginalLeft + 6, anno.OriginalBottom + 7));
            }
        }

        // RenderTargetBitmap으로 렌더링
        var rtb = new RenderTargetBitmap(
            CapturedScreenshot.PixelWidth, CapturedScreenshot.PixelHeight,
            96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = new FileStream(path, FileMode.Create);
        encoder.Save(fs);

        return Task.FromResult(path);
    }

    private void UpdateSelectedElementInfo()
    {
        if (_selectedElement == null)
        {
            SelectedElementInfo = "";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Class: {_selectedElement.ClassName}");
        sb.AppendLine($"ID: {_selectedElement.ResourceId}");
        sb.AppendLine($"Text: {_selectedElement.Text}");
        sb.AppendLine($"Desc: {_selectedElement.ContentDesc}");
        sb.AppendLine($"Bounds: {_selectedElement.BoundsString}");
        sb.AppendLine($"Package: {_selectedElement.PackageName}");
        SelectedElementInfo = sb.ToString();
    }

    #endregion
}
