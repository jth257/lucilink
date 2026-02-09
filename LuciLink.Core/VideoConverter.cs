using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace LuciLink.Core.Decoding;

public unsafe class VideoConverter : IDisposable
{
    private SwsContext* _swsContext;
    private int _sourceWidth, _sourceHeight;
    private int _destWidth, _destHeight;
    private AVPixelFormat _sourceFormat, _destFormat;

    public void Initialize(int sourceWidth, int sourceHeight, AVPixelFormat sourceFormat,
                           int destWidth, int destHeight, AVPixelFormat destFormat)
    {
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _sourceFormat = sourceFormat;
        _destWidth = destWidth;
        _destHeight = destHeight;
        _destFormat = destFormat;

        _swsContext = ffmpeg.sws_getContext(
            sourceWidth, sourceHeight, sourceFormat,
            destWidth, destHeight, destFormat,
            2 /* SWS_BILINEAR */, null, null, null);
            
        if (_swsContext == null)
        {
            throw new Exception("Could not initialize SwsContext.");
        }
    }

    public void Convert(AVFrame* sourceFrame, IntPtr destBuffer, int destStride)
    {
        if (_swsContext == null) return;

        // Destination pointers
        // sws_scale takes byte* const srcSlice[], int srcStride[], int srcSliceY, int srcSliceH, byte* const dst[], int dstStride[]
        // In C#: byte_ptrArray4 and int_array4 are fixed buffers in FFmpeg.AutoGen structs usually, but for arguments we can pass arrays or pointers.
        // Actually FFmpeg.AutoGen generated signatures often take byte** or similar.
        
        byte*[] destDataArr = new byte*[4];
        destDataArr[0] = (byte*)destBuffer;
        
        int[] destStrideArr = new int[4];
        destStrideArr[0] = destStride;
        
        ffmpeg.sws_scale(_swsContext,
            sourceFrame->data, sourceFrame->linesize,
            0, sourceFrame->height,
            destDataArr, destStrideArr);
    }

    public void Dispose()
    {
        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }
    }
}
