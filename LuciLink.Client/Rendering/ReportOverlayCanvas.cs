using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LuciLink.Core;

namespace LuciLink.Client.Rendering;

/// <summary>
/// 리포트 모드에서 캡처된 스크린샷 위에 어노테이션(바운딩 박스 + 메모)을 표시하는 투명 캔버스.
/// 안드로이드 좌표 → WPF 캔버스 좌표 변환을 수행합니다.
/// </summary>
public class ReportOverlayCanvas : Canvas
{
    // 안드로이드 화면 크기 (비디오 해상도)
    private int _deviceWidth;
    private int _deviceHeight;

    // 현재 어노테이션 목록
    private readonly List<ReportAnnotation> _annotations = new();

    // 드래그 상태
    private ReportAnnotation? _draggingAnnotation;
    private Point _dragStart;
    private Rect _dragOriginal;

    // 선택된 어노테이션 콜백
    public event Action<ReportAnnotation?>? AnnotationSelected;
    public event Action<ReportAnnotation>? AnnotationMemoRequested;

    public ReportOverlayCanvas()
    {
        Background = Brushes.Transparent; // 투명하지만 히트 테스트 가능
        ClipToBounds = true;
        PreviewMouseLeftButtonDown += OnCanvasPreviewMouseDown;
    }

    /// <summary>안드로이드 화면 크기 설정</summary>
    public void SetDeviceSize(int width, int height)
    {
        _deviceWidth = width;
        _deviceHeight = height;
    }

    /// <summary>어노테이션 추가 후 리렌더링</summary>
    public void AddAnnotation(ReportAnnotation annotation)
    {
        _annotations.Add(annotation);
        Redraw();
    }

    /// <summary>어노테이션 제거 후 리렌더링</summary>
    public void RemoveAnnotation(ReportAnnotation annotation)
    {
        _annotations.Remove(annotation);
        Redraw();
    }

    /// <summary>모든 어노테이션 제거</summary>
    public void ClearAnnotations()
    {
        _annotations.Clear();
        Children.Clear();
    }

    /// <summary>현재 어노테이션 리스트 반환</summary>
    public IReadOnlyList<ReportAnnotation> GetAnnotations() => _annotations.AsReadOnly();

    /// <summary>캔버스를 다시 그림</summary>
    public void Redraw()
    {
        Children.Clear();
        foreach (var anno in _annotations)
        {
            DrawAnnotation(anno);
        }
    }

    private void DrawAnnotation(ReportAnnotation anno)
    {
        // 원본 바운딩 박스 (빨간 점선)
        var origRect = ToCanvasRect(anno.OriginalLeft, anno.OriginalTop,
            anno.OriginalRight, anno.OriginalBottom);

        var origBorder = new Rectangle
        {
            Width = origRect.Width,
            Height = origRect.Height,
            Stroke = Brushes.Red,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Fill = new SolidColorBrush(Color.FromArgb(30, 255, 0, 0)),
            IsHitTestVisible = false,
            Tag = anno
        };
        SetLeft(origBorder, origRect.X);
        SetTop(origBorder, origRect.Y);
        Children.Add(origBorder);

        // 수정된 위치 (빨간 실선, 드래그 가능)
        if (anno.HasModifiedPosition)
        {
            var modRect = ToCanvasRect(anno.ModifiedLeft, anno.ModifiedTop,
                anno.ModifiedRight, anno.ModifiedBottom);

            var modBorder = new Rectangle
            {
                Width = modRect.Width,
                Height = modRect.Height,
                Stroke = new SolidColorBrush(Color.FromRgb(0, 200, 83)),
                StrokeThickness = 2.5,
                Fill = new SolidColorBrush(Color.FromArgb(30, 0, 200, 83)),
                Cursor = Cursors.SizeAll,
                Tag = anno
            };
            SetLeft(modBorder, modRect.X);
            SetTop(modBorder, modRect.Y);

            modBorder.MouseLeftButtonDown += OnAnnotationMouseDown;
            modBorder.MouseMove += OnAnnotationMouseMove;
            modBorder.MouseLeftButtonUp += OnAnnotationMouseUp;

            Children.Add(modBorder);

            // 원본 → 수정 위치 화살표 (중심점 연결)
            var line = new Line
            {
                X1 = origRect.X + origRect.Width / 2,
                Y1 = origRect.Y + origRect.Height / 2,
                X2 = modRect.X + modRect.Width / 2,
                Y2 = modRect.Y + modRect.Height / 2,
                Stroke = new SolidColorBrush(Color.FromArgb(180, 255, 165, 0)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 6, 3 },
                IsHitTestVisible = false
            };
            Children.Add(line);
        }

        // 메모 라벨
        if (!string.IsNullOrEmpty(anno.Memo))
        {
            var displayRect = anno.HasModifiedPosition
                ? ToCanvasRect(anno.ModifiedLeft, anno.ModifiedTop, anno.ModifiedRight, anno.ModifiedBottom)
                : origRect;

            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 30, 30, 30)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3, 6, 3),
                Child = new TextBlock
                {
                    Text = anno.Memo,
                    Foreground = Brushes.White,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 200
                },
                IsHitTestVisible = false
            };
            SetLeft(label, displayRect.X);
            SetTop(label, displayRect.Y - 28);
            Children.Add(label);
        }

        // 요소 ID 라벨 (간략)
        var idText = string.IsNullOrEmpty(anno.Element.ResourceId)
            ? anno.Element.ClassName.Split('.').LastOrDefault() ?? "?"
            : anno.Element.ResourceId.Split('/').LastOrDefault() ?? "?";

        var idLabel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 255, 59, 48)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2, 4, 2),
            Child = new TextBlock
            {
                Text = idText,
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold
            },
            IsHitTestVisible = false
        };
        SetLeft(idLabel, origRect.X);
        SetTop(idLabel, origRect.Y + origRect.Height + 2);
        Children.Add(idLabel);
    }

    #region Mouse Interaction

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // 캔버스 빈 영역 클릭 → 해당 좌표의 UI 요소 검색 이벤트 발생
        if (e.OriginalSource == this)
        {
            // 부모에서 처리 (ViewModel 연결)
            AnnotationSelected?.Invoke(null);
        }
    }

    private void OnCanvasPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 더블클릭 감지: 어노테이션 위에서 더블클릭 → 메모 입력 요청
        if (e.ClickCount == 2 && e.OriginalSource is Rectangle rect && rect.Tag is ReportAnnotation anno)
        {
            AnnotationMemoRequested?.Invoke(anno);
            e.Handled = true;
        }
    }

    private void OnAnnotationMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Rectangle rect && rect.Tag is ReportAnnotation anno)
        {
            _draggingAnnotation = anno;
            _dragStart = e.GetPosition(this);
            _dragOriginal = new Rect(
                anno.ModifiedLeft, anno.ModifiedTop,
                anno.ModifiedRight - anno.ModifiedLeft,
                anno.ModifiedBottom - anno.ModifiedTop);
            rect.CaptureMouse();
            AnnotationSelected?.Invoke(anno);
            e.Handled = true;
        }
    }

    private void OnAnnotationMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingAnnotation == null || e.LeftButton != MouseButtonState.Pressed) return;

        var current = e.GetPosition(this);
        var deltaX = current.X - _dragStart.X;
        var deltaY = current.Y - _dragStart.Y;

        // 캔버스 좌표 델타를 안드로이드 좌표 델타로 변환
        if (_deviceWidth <= 0 || _deviceHeight <= 0) return;
        double scaleX = _deviceWidth / ActualWidth;
        double scaleY = _deviceHeight / ActualHeight;

        // Uniform 스케일 (비디오와 동일한 비율)
        double scale = Math.Max(scaleX, scaleY);

        int androidDeltaX = (int)(deltaX * scale);
        int androidDeltaY = (int)(deltaY * scale);

        _draggingAnnotation.ModifiedLeft = (int)_dragOriginal.X + androidDeltaX;
        _draggingAnnotation.ModifiedTop = (int)_dragOriginal.Y + androidDeltaY;
        _draggingAnnotation.ModifiedRight = (int)(_dragOriginal.X + _dragOriginal.Width) + androidDeltaX;
        _draggingAnnotation.ModifiedBottom = (int)(_dragOriginal.Y + _dragOriginal.Height) + androidDeltaY;

        Redraw();
        e.Handled = true;
    }

    private void OnAnnotationMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Rectangle rect)
        {
            rect.ReleaseMouseCapture();
        }
        _draggingAnnotation = null;
        e.Handled = true;
    }

    #endregion

    #region Coordinate Conversion

    /// <summary>
    /// 안드로이드 좌표 → 캔버스 Rect 변환.
    /// 캔버스가 Image(Uniform Stretch)와 동일한 크기라고 가정합니다.
    /// </summary>
    private Rect ToCanvasRect(int left, int top, int right, int bottom)
    {
        if (_deviceWidth <= 0 || _deviceHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
            return new Rect(0, 0, 0, 0);

        double scaleX = ActualWidth / _deviceWidth;
        double scaleY = ActualHeight / _deviceHeight;
        double scale = Math.Min(scaleX, scaleY);

        // Uniform stretch 오프셋 계산
        double offsetX = (ActualWidth - _deviceWidth * scale) / 2;
        double offsetY = (ActualHeight - _deviceHeight * scale) / 2;

        return new Rect(
            left * scale + offsetX,
            top * scale + offsetY,
            (right - left) * scale,
            (bottom - top) * scale);
    }

    /// <summary>
    /// 캔버스 좌표 → 안드로이드 좌표 변환.
    /// </summary>
    public (int x, int y) ToAndroidCoords(double canvasX, double canvasY)
    {
        if (_deviceWidth <= 0 || _deviceHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
            return (0, 0);

        double scaleX = ActualWidth / _deviceWidth;
        double scaleY = ActualHeight / _deviceHeight;
        double scale = Math.Min(scaleX, scaleY);

        double offsetX = (ActualWidth - _deviceWidth * scale) / 2;
        double offsetY = (ActualHeight - _deviceHeight * scale) / 2;

        int ax = (int)((canvasX - offsetX) / scale);
        int ay = (int)((canvasY - offsetY) / scale);

        return (Math.Clamp(ax, 0, _deviceWidth - 1), Math.Clamp(ay, 0, _deviceHeight - 1));
    }

    #endregion
}

/// <summary>
/// 리포트 어노테이션 — 선택된 UI 요소 + 수정 위치 + 메모
/// </summary>
public class ReportAnnotation
{
    public UiElementInfo Element { get; set; } = null!;

    // 원본 위치 (UI dump에서 가져온 좌표)
    public int OriginalLeft { get; set; }
    public int OriginalTop { get; set; }
    public int OriginalRight { get; set; }
    public int OriginalBottom { get; set; }

    // 수정 위치 (드래그로 이동한 좌표)
    public int ModifiedLeft { get; set; }
    public int ModifiedTop { get; set; }
    public int ModifiedRight { get; set; }
    public int ModifiedBottom { get; set; }

    public bool HasModifiedPosition =>
        ModifiedLeft != OriginalLeft || ModifiedTop != OriginalTop ||
        ModifiedRight != OriginalRight || ModifiedBottom != OriginalBottom;

    public string Memo { get; set; } = "";

    public static ReportAnnotation FromElement(UiElementInfo element)
    {
        return new ReportAnnotation
        {
            Element = element,
            OriginalLeft = element.Left,
            OriginalTop = element.Top,
            OriginalRight = element.Right,
            OriginalBottom = element.Bottom,
            ModifiedLeft = element.Left,
            ModifiedTop = element.Top,
            ModifiedRight = element.Right,
            ModifiedBottom = element.Bottom
        };
    }
}
