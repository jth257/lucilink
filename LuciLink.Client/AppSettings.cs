using System.IO;
using System.Text.Json;

namespace LuciLink.Client;

/// <summary>
/// JSON 기반 앱 설정 저장/로드.
/// 저장 경로: %APPDATA%/LuciLink/settings.json
/// </summary>
public class AppSettings
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuciLink");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    // 언어
    public string Language { get; set; } = "auto"; // "auto", "ko", "en"

    // 창 위치/크기
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 460;
    public double WindowHeight { get; set; } = 900;
    public bool IsMaximized { get; set; } = false;

    // 스트리밍 설정
    public int MaxWidth { get; set; } = 0;    // 0 = 원본 해상도
    public int MaxHeight { get; set; } = 0;   // 0 = 원본 해상도
    public int Bitrate { get; set; } = 8000000; // 8Mbps 기본값
    public int Framerate { get; set; } = 60;
    public string VideoEncoder { get; set; } = ""; // 빈 문자열 = 기본 인코더

    /// <summary>설정 파일에서 로드 (없으면 기본값)</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* 손상된 파일 무시 */ }
        return new AppSettings();
    }

    /// <summary>설정 파일로 저장</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* 저장 실패 무시 */ }
    }
}
