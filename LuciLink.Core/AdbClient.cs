using System.Diagnostics;

namespace LuciLink.Core.Adb;

public class AdbClient
{
    private readonly string _adbPath;

    public AdbClient(string adbPath = "adb")
    {
        _adbPath = adbPath;
    }

    public async Task<string> ExecuteCommandAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _adbPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Read stdout and stderr in parallel to avoid deadlocks
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new Exception($"ADB command failed: {errorTask.Result}");
        }

        return outputTask.Result.Trim();
    }

    public async Task PushFileAsync(string deviceSerial, string localPath, string remotePath)
    {
        await ExecuteCommandAsync($"-s {deviceSerial} push \"{localPath}\" \"{remotePath}\"");
    }

    public async Task<List<string>> GetDevicesAsync()
    {
        var output = await ExecuteCommandAsync("devices");
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        // Skip first line "List of devices attached" and parse the rest
        return lines.Skip(1)
            .Where(l => l.Contains("\tdevice"))
            .Select(l => l.Split('\t')[0])
            .ToList();
    }

    public async Task ForwardPortAsync(string deviceSerial, int localPort, string remoteSocketName)
    {
        await ExecuteCommandAsync($"-s {deviceSerial} forward tcp:{localPort} localabstract:{remoteSocketName}");
    }

    public async Task RemoveForwardAsync(string deviceSerial, int localPort)
    {
        await ExecuteCommandAsync($"-s {deviceSerial} forward --remove tcp:{localPort}");
    }

    public Process StartProcess(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _adbPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }
}
