using LuciLink.Core.Adb;

namespace LuciLink.Core;

/// <summary>
/// adb shell uiautomator dump 명령으로 안드로이드 UI 구조(XML)를 가져오는 서비스.
/// </summary>
public class UiDumpService
{
    private const string RemoteDumpPath = "/sdcard/ui_dump.xml";

    /// <summary>
    /// 현재 화면의 UI 구조를 XML 문자열로 반환합니다.
    /// 1) uiautomator dump → 2) cat으로 읽기 → 3) rm으로 정리
    /// </summary>
    public async Task<string> DumpUiXmlAsync(AdbClient adb, string deviceSerial)
    {
        // 1. UI hierarchy dump
        await adb.ExecuteCommandAsync(
            $"-s {deviceSerial} shell uiautomator dump {RemoteDumpPath}");

        // 2. Read the dumped XML
        string xml;
        try
        {
            xml = await adb.ExecuteCommandAsync(
                $"-s {deviceSerial} shell cat {RemoteDumpPath}");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to read UI dump: {ex.Message}", ex);
        }

        // 3. Cleanup temp file (best-effort)
        try
        {
            await adb.ExecuteCommandAsync(
                $"-s {deviceSerial} shell rm {RemoteDumpPath}");
        }
        catch { /* ignore cleanup failures */ }

        if (string.IsNullOrWhiteSpace(xml))
            throw new Exception("UI dump returned empty XML");

        return xml;
    }
}
