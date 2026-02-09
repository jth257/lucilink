using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FFmpeg.AutoGen;
using LuciLink.Core.Decoding;

namespace LuciLink.Client.Rendering;

public class VideoRenderer : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private WriteableBitmap? _bitmap;
    private VideoConverter? _converter;
    private int _width, _height;

    // Expose BitmapSource for UI binding
    public ImageSource? ImageSource => _bitmap;

    public VideoRenderer(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public unsafe void Render(AVFrame* frame)
    {
        if (frame == null) return;

        // Check if size changed or first frame
        if (_bitmap == null || _width != frame->width || _height != frame->height)
        {
            InitializeBitmap(frame->width, frame->height, (AVPixelFormat)frame->format);
        }

        if (_bitmap == null || _converter == null) return;

        // Render on UI thread to ensure safety with WriteableBitmap
        // Optimization: We could potentially lock on background if we adhere to strict synchronization,
        // but Dispatcher.Invoke is the safest bet for WPF w/o crashing.
        // To minimize blocking, we assume sws_scale is fast enough.
        // For 60fps, we have ~16ms. sws_scale YUV->BGRA 1080p is approx 2-5ms on modern CPU.
        
        _dispatcher.Invoke(() =>
        {
            _bitmap.Lock();
            try
            {
                // Write directly to BackBuffer
                _converter.Convert(frame, _bitmap.BackBuffer, _bitmap.BackBufferStride);
                
                // Mark area as dirty to trigger repaint
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            }
            finally
            {
                _bitmap.Unlock();
            }
        }, DispatcherPriority.Render); // High priority for rendering
    }

    private void InitializeBitmap(int width, int height, AVPixelFormat sourceFormat)
    {
        _width = width;
        _height = height;

        _dispatcher.Invoke(() =>
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            
            // Re-initialize converter matching new dimensions
            _converter?.Dispose();
            _converter = new VideoConverter();
            // Target format is BGRA32 (compatible with WPF default)
            _converter.Initialize(width, height, sourceFormat, width, height, AVPixelFormat.AV_PIX_FMT_BGRA);
        });
    }

    public void Dispose()
    {
        _converter?.Dispose();
        _converter = null;
        _bitmap = null;
    }
}
