using System.Windows;

namespace LuciLink.Client;

/// <summary>
/// 앱 언어 전환을 관리하는 싱글턴.
/// ResourceDictionary를 교체하여 런타임에 즉시 언어 변경.
/// </summary>
public static class LocalizationManager
{
    private static ResourceDictionary? _currentDict;
    private static string _currentLang = "ko";
    private static AppSettings? _settings;

    public static string CurrentLanguage => _currentLang;

    /// <summary>초기 언어 로드 (설정 파일 → 시스템 언어 → 기본값)</summary>
    public static void Initialize(AppSettings settings)
    {
        _settings = settings;

        string lang;
        if (settings.Language != "auto")
        {
            lang = settings.Language;
        }
        else
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            lang = culture.TwoLetterISOLanguageName == "ko" ? "ko" : "en";
        }
        SetLanguage(lang);
    }

    /// <summary>언어 전환</summary>
    public static void SetLanguage(string langCode)
    {
        _currentLang = langCode;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{langCode}.xaml", UriKind.Relative)
        };

        if (_currentDict != null)
        {
            Application.Current.Resources.MergedDictionaries.Remove(_currentDict);
        }

        Application.Current.Resources.MergedDictionaries.Add(dict);
        _currentDict = dict;

        // 설정에 저장
        if (_settings != null)
        {
            _settings.Language = langCode;
            _settings.Save();
        }
    }

    /// <summary>현재 언어 ↔ 반대 언어 토글</summary>
    public static void Toggle()
    {
        SetLanguage(_currentLang == "ko" ? "en" : "ko");
    }

    /// <summary>코드에서 문자열 가져오기</summary>
    public static string Get(string key)
    {
        return Application.Current.TryFindResource(key) as string ?? $"[{key}]";
    }
}
