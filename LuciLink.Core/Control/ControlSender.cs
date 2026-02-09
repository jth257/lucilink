using System.Buffers.Binary;
using System.Net.Sockets;

namespace LuciLink.Core.Control;

public class ControlSender : IDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    public bool IsConnected => _client?.Connected ?? false;

    public async Task ConnectAsync(int port)
    {
        _client = new TcpClient { NoDelay = true }; // Nagle 비활성화 → 지연 최소화
        await _client.ConnectAsync("127.0.0.1", port);
        _stream = _client.GetStream();
    }

    /// <summary>
    /// scrcpy v2.4 INJECT_TOUCH_EVENT (Type 2, 32 bytes, Big Endian)
    /// </summary>
    public async Task InjectTouchAsync(int action, long pointerId, 
        int x, int y, int screenWidth, int screenHeight, 
        float pressure, int actionButton, int buttonsState)
    {
        if (_stream == null) return;

        // 프로토콜 레이아웃 (32 bytes):
        // [0]    type = 2
        // [1]    action (0=DOWN, 1=UP, 2=MOVE)
        // [2-9]  pointerId (8 bytes)
        // [10-13] x (4 bytes)
        // [14-17] y (4 bytes)
        // [18-19] screenWidth (2 bytes)
        // [20-21] screenHeight (2 bytes)
        // [22-23] pressure (0~65535)
        // [24-27] actionButton (변경된 버튼)
        // [28-31] buttonsState (현재 눌린 버튼들)
        
        byte[] msg = new byte[32];
        msg[0] = 2; // TYPE_INJECT_TOUCH_EVENT
        msg[1] = (byte)action;
        BinaryPrimitives.WriteInt64BigEndian(msg.AsSpan(2), pointerId);
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(10), x);
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(14), y);
        BinaryPrimitives.WriteInt16BigEndian(msg.AsSpan(18), (short)screenWidth);
        BinaryPrimitives.WriteInt16BigEndian(msg.AsSpan(20), (short)screenHeight);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(22), (ushort)(pressure * 65535f));
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(24), actionButton);
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(28), buttonsState);

        await _stream.WriteAsync(msg).ConfigureAwait(false);
    }

    /// <summary>
    /// scrcpy v2.4 INJECT_KEY_EVENT (Type 0, 14 bytes, Big Endian)
    /// </summary>
    public async Task InjectKeyAsync(int action, int keyCode, int metaState)
    {
        if (_stream == null) return;

        byte[] msg = new byte[14];
        msg[0] = 0; // TYPE_INJECT_KEY_EVENT
        msg[1] = (byte)action;
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(2), keyCode);
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(6), 0); // repeat
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(10), metaState);

        await _stream.WriteAsync(msg).ConfigureAwait(false);
    }

    /// <summary>
    /// scrcpy v2.4 SET_CLIPBOARD (Type 9) + PASTE (Ctrl+V 시뮬레이션)
    /// 한글 등 IME 조합 문자를 직접 주입
    /// </summary>
    public async Task InjectTextAsync(string text)
    {
        if (_stream == null || string.IsNullOrEmpty(text)) return;

        byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(text);

        // SET_CLIPBOARD: type(1) + sequence(8) + paste(1) + length(4) + text
        byte[] msg = new byte[1 + 8 + 1 + 4 + textBytes.Length];
        msg[0] = 9; // TYPE_SET_CLIPBOARD
        BinaryPrimitives.WriteInt64BigEndian(msg.AsSpan(1), 0); // sequence = 0
        msg[9] = 1; // paste = true (자동으로 붙여넣기)
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(10), textBytes.Length);
        textBytes.CopyTo(msg, 14);

        await _stream.WriteAsync(msg).ConfigureAwait(false);
    }

    /// <summary>
    /// 화면 회전 제어 (scrcpy v2.4 ROTATE_DEVICE, Type 11)
    /// </summary>
    public async Task RotateDeviceAsync()
    {
        if (_stream == null) return;
        byte[] msg = new byte[] { 11 }; // TYPE_ROTATE_DEVICE
        await _stream.WriteAsync(msg).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
    }
}
