using System.Text.Json;

namespace Quince.Service.Services;

/// <summary>Serves UI text from flat key→string JSON dictionaries (<c>i18n/ru.json</c>,
/// <c>i18n/en.json</c>), switchable live without a page reload. The active language is persisted
/// through <see cref="AppSettingsService"/> (<c>app.yaml</c>, <c>ui_language</c>) — same live-reload
/// pattern as <see cref="AppConfig.AdKeywords"/> and friends, so every open browser tab picks up a
/// change immediately via <see cref="Changed"/>. Only user-facing UI chrome goes through here — log
/// messages, metadata, and channel config field names are unaffected.</summary>
public class LocalizationService
{
    private readonly AppSettingsService _appSettings;
    private readonly Dictionary<string, Dictionary<string, string>> _translations = new();

    public event Action? Changed;

    public LocalizationService(AppSettingsService appSettings, string i18nDir)
    {
        _appSettings = appSettings;
        foreach (var lang in new[] { "ru", "en" })
        {
            var path = Path.Combine(i18nDir, $"{lang}.json");
            var json = File.ReadAllText(path);
            _translations[lang] = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>();
        }
        _appSettings.Changed += () => Changed?.Invoke();
    }

    public string Language => _translations.ContainsKey(_appSettings.Current.UiLanguage)
        ? _appSettings.Current.UiLanguage
        : "ru";

    public void SetLanguage(string language)
    {
        if (language == Language) return;
        if (!_translations.ContainsKey(language)) return;
        var updated = _appSettings.Current;
        updated.UiLanguage = language;
        _appSettings.Save(updated);
    }

    /// <summary>Looks up <paramref name="key"/> in the active language, falling back to Russian and
    /// then the raw key itself so a missing translation renders as visible text instead of throwing.</summary>
    public string T(string key)
    {
        if (_translations[Language].TryGetValue(key, out var value)) return value;
        if (_translations["ru"].TryGetValue(key, out var fallback)) return fallback;
        return key;
    }

    public string T(string key, params object[] args) => string.Format(T(key), args);

    public string this[string key] => T(key);
}
