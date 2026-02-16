using LuciLink.Core.Adb;

namespace LuciLink.Core.Scrcpy;

public class ScrcpyServer
{
    private readonly AdbClient _adb;
    private const string DEVICE_SERVER_PATH = "/data/local/tmp/scrcpy-server.jar";

    /// <summary>
    /// 현재 세션의 scid와 소켓 이름. StartServerAsync 호출 후 사용 가능.
    /// </summary>
    public int Scid { get; private set; }
    public string SocketName { get; private set; } = "";

    public ScrcpyServer(AdbClient adb)
    {
        _adb = adb;
    }

    public async Task PushServerAsync(string deviceSerial, string localServerPath)
    {
        await _adb.PushFileAsync(deviceSerial, localServerPath, DEVICE_SERVER_PATH).ConfigureAwait(false);
    }

    public async Task<System.Diagnostics.Process> StartServerAsync(
        string deviceSerial, int maxSize = 0, int bitrate = 8000000,
        int maxFps = 60, string? videoEncoder = null)
    {
        // 이전 scrcpy 서버 정리 시도 (실패해도 무방 - 랜덤 scid 사용하므로)
        try 
        {
            await _adb.ExecuteCommandAsync(
                $"-s {deviceSerial} shell \"kill -9 $(ps | grep app_process | grep -v grep | awk '{{print $2}}')\"")
                .ConfigureAwait(false);
        }
        catch { /* 실패 무시 */ }
        
        await Task.Delay(1000).ConfigureAwait(false);

        // 매번 랜덤 scid 생성 → 소켓 이름 충돌 방지
        Scid = Random.Shared.Next(1, 0xFFFFFF);
        SocketName = $"scrcpy_{Scid:d8}";

        string cmd = $"-s {deviceSerial} shell CLASSPATH={DEVICE_SERVER_PATH} app_process / com.genymobile.scrcpy.Server " +
                     "2.4 " +
                     $"scid={Scid} " +
                     "tunnel_forward=true " +
                     "video=true " +
                     "audio=false " +
                     "control=true " +
                     "cleanup=false " +
                     $"max_size={maxSize} " +
                     $"video_bit_rate={bitrate} " +
                     $"max_fps={maxFps} " +
                     "send_dummy_byte=true " +
                     "send_codec_meta=true " +
                     "send_device_meta=true";

        // 인코더 지정 시 추가 (예: OMX.google.h264.encoder)
        if (!string.IsNullOrEmpty(videoEncoder))
            cmd += $" video_encoder={videoEncoder}";
        
        return _adb.StartProcess(cmd);
    }
}
