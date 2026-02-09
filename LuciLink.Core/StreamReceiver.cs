using System.Net.Sockets;

namespace LuciLink.Core.Streaming;

public class StreamReceiver
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    public async Task ConnectAsync(int port)
    {
        _client = new TcpClient();
        await _client.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
        _stream = _client.GetStream();
        
        // 더미 바이트 수신 (send_dummy_byte=true)
        // ReadAsync는 ReadTimeout이 적용되지 않으므로 CancellationToken으로 타임아웃 처리
        byte[] dummyBuffer = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int read = await _stream.ReadAsync(dummyBuffer, 0, 1, cts.Token).ConfigureAwait(false);
        
        if (read != 1)
        {
            throw new IOException("Server did not send dummy byte - server may have crashed.");
        }
        
        System.Diagnostics.Debug.WriteLine($"[StreamReceiver] Dummy byte received: 0x{dummyBuffer[0]:X2}");
    }

    public async Task<string> ReadDeviceNameAsync()
    {
        if (_stream == null) throw new InvalidOperationException("Not connected");
        
        // Scrcpy protocol: 64 bytes for device name
        byte[] buffer = new byte[64];
        int bytesRead = 0;
        int attempts = 0;
        
        while (bytesRead < 64)
        {
            attempts++;
            int n = await _stream.ReadAsync(buffer, bytesRead, 64 - bytesRead).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[StreamReceiver] Read attempt {attempts}: got {n} bytes, total {bytesRead + n}/64");
            if (n == 0) 
            {
                System.Diagnostics.Debug.WriteLine($"[StreamReceiver] Stream ended! Only got {bytesRead} bytes. First 16 bytes: {BitConverter.ToString(buffer, 0, Math.Min(16, bytesRead))}");
                throw new EndOfStreamException($"Stream ended after {bytesRead} bytes (expected 64)");
            }
            bytesRead += n;
        }
        
        System.Diagnostics.Debug.WriteLine($"[StreamReceiver] Full 64 bytes received. Hex: {BitConverter.ToString(buffer, 0, 16)}...");
        return System.Text.Encoding.UTF8.GetString(buffer).Trim('\0');
    }

    public Stream GetRawStream()
    {
        return _stream ?? throw new InvalidOperationException("Not connected");
    }

    public void Disconnect()
    {
        _stream?.Dispose();
        _client?.Dispose();
    }
}
