using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LuciLink.Client.StoreMockup;

public partial class MockupWindow : Window
{
    private readonly MockupViewModel _vm;

    public MockupWindow(BitmapSource screenshot, bool isFreeUser)
    {
        var copy = DeepCopyBitmap(screenshot);
        _vm = new MockupViewModel(copy, isFreeUser);
        DataContext = _vm;
        InitializeComponent();
    }

    private void OnBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && int.TryParse(tag, out int idx))
            _vm.SelectedBackgroundIndex = idx;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"LuciLink_Mockup_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            // 오프스크린 DrawingVisual로 DPI 독립적 렌더링 (96 DPI = 1:1 pixel)
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                RenderMockupLayers(dc, _vm);
            }

            var rtb = new RenderTargetBitmap(
                MockupViewModel.CanvasWidth,
                MockupViewModel.CanvasHeight,
                96, 96,
                PixelFormats.Pbgra32);
            rtb.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = new FileStream(path, FileMode.Create);
            encoder.Save(fs);

            _vm.StatusText = $"✅ 저장 완료: {Path.GetFileName(path)}";
            MessageBox.Show(
                $"목업이 바탕화면에 저장되었습니다.\n\n{path}\n\n크기: {MockupViewModel.CanvasWidth} × {MockupViewModel.CanvasHeight} px",
                "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"❌ 저장 실패: {ex.Message}";
            MessageBox.Show(
                $"저장 중 오류가 발생했습니다.\n{ex.Message}",
                "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 1080×1920 캔버스에 5개 레이어를 순서대로 렌더링.
    /// 미리보기(XAML)와 동일한 좌표 상수를 사용하여 WYSIWYG 보장.
    /// </summary>
    internal static void RenderMockupLayers(DrawingContext dc, MockupViewModel vm)
    {
        const int W = MockupViewModel.CanvasWidth;
        const int H = MockupViewModel.CanvasHeight;

        // ── Layer 1: 배경 그라데이션 ──
        var bgBrush = vm.SelectedBackground.Clone();
        bgBrush.Freeze();
        dc.DrawRectangle(bgBrush, null, new Rect(0, 0, W, H));

        // ── 폰 그림자 (다중 패스로 부드럽게) ──
        for (int i = 3; i >= 0; i--)
        {
            byte alpha = (byte)(15 - i * 3);
            var shadowBrush = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
            shadowBrush.Freeze();
            dc.DrawRoundedRectangle(
                shadowBrush, null,
                new Rect(
                    MockupViewModel.PhoneLeft + 4 + i * 4,
                    MockupViewModel.PhoneTop + 8 + i * 6,
                    MockupViewModel.PhoneWidth,
                    MockupViewModel.PhoneHeight),
                MockupViewModel.PhoneCornerRadius + i * 2,
                MockupViewModel.PhoneCornerRadius + i * 2);
        }

        // ── 폰 본체 ──
        var phoneRect = new Rect(
            MockupViewModel.PhoneLeft, MockupViewModel.PhoneTop,
            MockupViewModel.PhoneWidth, MockupViewModel.PhoneHeight);

        var phoneBodyBrush = new LinearGradientBrush(
            Color.FromRgb(0x2A, 0x2A, 0x2E),
            Color.FromRgb(0x1A, 0x1A, 0x1E),
            new Point(0, 0), new Point(1, 1));
        phoneBodyBrush.Freeze();

        dc.DrawRoundedRectangle(
            phoneBodyBrush, null, phoneRect,
            MockupViewModel.PhoneCornerRadius, MockupViewModel.PhoneCornerRadius);

        // 폰 하이라이트 테두리
        var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1.5);
        borderPen.Freeze();
        dc.DrawRoundedRectangle(
            null, borderPen, phoneRect,
            MockupViewModel.PhoneCornerRadius, MockupViewModel.PhoneCornerRadius);

        // ── 스크린 배경 (검정) ──
        var screenRect = new Rect(
            MockupViewModel.ScreenLeft, MockupViewModel.ScreenTop,
            MockupViewModel.ScreenWidth, MockupViewModel.ScreenHeight);
        var screenGeom = new RectangleGeometry(screenRect,
            MockupViewModel.ScreenCornerRadius, MockupViewModel.ScreenCornerRadius);
        dc.DrawGeometry(Brushes.Black, null, screenGeom);

        // ── Layer 2: 앱 스크린샷 (스크린 영역에 Uniform 스케일링 + 클리핑) ──
        if (vm.Screenshot != null)
        {
            dc.PushClip(screenGeom);

            double scaleX = (double)MockupViewModel.ScreenWidth / vm.Screenshot.PixelWidth;
            double scaleY = (double)MockupViewModel.ScreenHeight / vm.Screenshot.PixelHeight;
            double scale = Math.Min(scaleX, scaleY);

            double renderW = vm.Screenshot.PixelWidth * scale;
            double renderH = vm.Screenshot.PixelHeight * scale;
            double offsetX = MockupViewModel.ScreenLeft + (MockupViewModel.ScreenWidth - renderW) / 2;
            double offsetY = MockupViewModel.ScreenTop + (MockupViewModel.ScreenHeight - renderH) / 2;

            dc.DrawImage(vm.Screenshot, new Rect(offsetX, offsetY, renderW, renderH));
            dc.Pop();
        }

        // ── Layer 3: 폰 프레임 오버레이 (Assets/phone_frame.png 존재 시) ──
        var phoneFrameImage = TryLoadPhoneFrame();
        if (phoneFrameImage != null)
        {
            dc.DrawImage(phoneFrameImage, phoneRect);
        }

        // 카메라 필 (노치)
        double pillW = 56, pillH = 10;
        double pillX = MockupViewModel.PhoneLeft + (MockupViewModel.PhoneWidth - pillW) / 2;
        double pillY = MockupViewModel.PhoneTop + 14;
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)), null,
            new Rect(pillX, pillY, pillW, pillH), 5, 5);

        // ── Layer 4: 마케팅 문구 (폰 위쪽 여백에 중앙 정렬) ──
        if (!string.IsNullOrWhiteSpace(vm.MarketingText))
        {
            var typeface = new Typeface(
                new FontFamily("Segoe UI Variable, Segoe UI, sans-serif"),
                FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            var textBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            textBrush.Freeze();

            var text = new FormattedText(
                vm.MarketingText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface, 54, textBrush, 96)
            {
                MaxTextWidth = W - 120,
                TextAlignment = TextAlignment.Center
            };

            double textX = 60;
            double textY = Math.Max(30, (MockupViewModel.PhoneTop - text.Height) / 2);

            // 텍스트 그림자
            var shadowBrush = new SolidColorBrush(Color.FromArgb(25, 0, 0, 0));
            shadowBrush.Freeze();
            var shadow = new FormattedText(
                vm.MarketingText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface, 54, shadowBrush, 96)
            {
                MaxTextWidth = W - 120,
                TextAlignment = TextAlignment.Center
            };
            dc.DrawText(shadow, new Point(textX + 2, textY + 3));
            dc.DrawText(text, new Point(textX, textY));
        }

        // ── Layer 5: 워터마크 (IsFreeUser == true 일 때만 표시) ──
        if (vm.IsFreeUser)
        {
            var wmTypeface = new Typeface(
                new FontFamily("Segoe UI Variable, Segoe UI, sans-serif"),
                FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

            var wmBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255));
            wmBrush.Freeze();

            var wmText = new FormattedText(
                "⚡ Created by LuciLink",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                wmTypeface, 20, wmBrush, 96);

            // 폰 프레임 우하단 모서리에 교묘하게 겹치도록 배치
            double wmX = MockupViewModel.PhoneLeft + MockupViewModel.PhoneWidth - wmText.Width - 24;
            double wmY = MockupViewModel.PhoneTop + MockupViewModel.PhoneHeight - 36;

            var wmBgBrush = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0));
            wmBgBrush.Freeze();
            dc.DrawRoundedRectangle(
                wmBgBrush, null,
                new Rect(wmX - 10, wmY - 4, wmText.Width + 20, wmText.Height + 8),
                6, 6);

            dc.DrawText(wmText, new Point(wmX, wmY));
        }
    }

    /// <summary>Assets/phone_frame.png 로드 시도 (없으면 null 반환 → 프로그래밍 방식 프레임 사용)</summary>
    private static BitmapSource? TryLoadPhoneFrame()
    {
        try
        {
            var path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Assets", "phone_frame.png");
            if (!File.Exists(path)) return null;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    /// <summary>BitmapSource 깊은 복사 (PNG 인코딩/디코딩으로 완전 독립 복사본 생성)</summary>
    private static BitmapSource DeepCopyBitmap(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }
}
