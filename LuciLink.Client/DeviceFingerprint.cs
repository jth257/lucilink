using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace LuciLink.Client;

/// <summary>
/// 기기 핑거프린트 생성: 하드웨어 정보 조합 → SHA-256 해시.
/// 원본 정보는 저장하지 않으며, 해시만 서버로 전송.
/// </summary>
public static class DeviceFingerprint
{
    /// <summary>기기 고유 SHA-256 해시 생성</summary>
    public static string Generate()
    {
        var raw = new StringBuilder();

        // CPU ID
        raw.Append(GetWmiValue("Win32_Processor", "ProcessorId"));
        // 메인보드 시리얼
        raw.Append(GetWmiValue("Win32_BaseBoard", "SerialNumber"));
        // BIOS 시리얼
        raw.Append(GetWmiValue("Win32_BIOS", "SerialNumber"));
        // 디스크 시리얼 (첫 번째)
        raw.Append(GetWmiValue("Win32_DiskDrive", "SerialNumber"));

        if (raw.Length == 0)
        {
            // WMI 실패 시 머신 이름 + OS 설치 ID 사용 (덜 고유함)
            raw.Append(Environment.MachineName);
            raw.Append(GetRegistryMachineGuid());
        }

        // SHA-256 해시
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static string GetWmiValue(string className, string propertyName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {propertyName} FROM {className}");
            foreach (var obj in searcher.Get())
            {
                var val = obj[propertyName]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(val) && val != "To Be Filled By O.E.M.")
                    return val;
            }
        }
        catch { }
        return "";
    }

    private static string GetRegistryMachineGuid()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString() ?? "";
        }
        catch { return ""; }
    }
}
