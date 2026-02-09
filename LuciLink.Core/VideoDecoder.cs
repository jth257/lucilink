using FFmpeg.AutoGen;

namespace LuciLink.Core.Decoding;

public unsafe class VideoDecoder : IDisposable
{
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private AVPacket* _packet;

    public VideoDecoder()
    {
    }

    public void Initialize()
    {
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null) throw new Exception("H.264 decoder not found.");

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (ffmpeg.avcodec_open2(_codecContext, codec, null) < 0)
        {
            throw new Exception("Could not open codec.");
        }

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    public AVFrame* Decode(byte[] data)
    {
        fixed (byte* pData = data)
        {
            _packet->data = pData;
            _packet->size = data.Length;

            int response = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            if (response < 0) return null; // Error or need more data

            response = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (response == 0)
            {
                return _frame;
            }
        }
        return null; // No frame available yet
    }
    
    public void Dispose()
    {
        fixed (AVCodecContext** ptr = &_codecContext) ffmpeg.avcodec_free_context(ptr);
        fixed (AVFrame** ptr = &_frame) ffmpeg.av_frame_free(ptr);
        fixed (AVPacket** ptr = &_packet) ffmpeg.av_packet_free(ptr);
    }
}
