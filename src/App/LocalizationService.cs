using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace RatScanner;

public enum UiLanguage
{
    English = 0,
    Spanish = 1,
    French = 2,
    Polish = 3,
    Portuguese = 4,
    Russian = 5,
    Chinese = 6,
}

public static class UiLanguageExtensions
{
    public static string ToCultureName(this UiLanguage language)
    {
        return language switch
        {
            UiLanguage.English => "English",
            UiLanguage.Spanish => "Español",
            UiLanguage.French => "Français",
            UiLanguage.Polish => "Polski",
            UiLanguage.Portuguese => "Português",
            UiLanguage.Russian => "Русский",
            UiLanguage.Chinese => "中文",
            _ => "Unknown",
        };
    }

    public static string ToCultureCode(this UiLanguage language)
    {
        return language switch
        {
            UiLanguage.English => "en",
            UiLanguage.Spanish => "es",
            UiLanguage.French => "fr",
            UiLanguage.Polish => "pl",
            UiLanguage.Portuguese => "pt",
            UiLanguage.Russian => "ru",
            UiLanguage.Chinese => "zh",
            _ => "en",
        };
    }

    public static string GetTranslationFileName(this UiLanguage language) => $"{language.ToCultureCode()}.json";
}

public class LocalizationService
{
    private readonly string _translationDirectory;
    private Dictionary<string, string> _translations = new(StringComparer.Ordinal);
    private Dictionary<string, string> _englishTranslations = new(StringComparer.Ordinal);

    public LocalizationService()
        : this(RatConfig.Paths.I18nDir, RatConfig.UserInterface.Language) { }

    internal LocalizationService(string translationDirectory, UiLanguage language = UiLanguage.English)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translationDirectory);
        _translationDirectory = translationDirectory;
        SetLanguage(language);
    }

    public void SetLanguage(UiLanguage language)
    {
        bool englishLoaded = TryLoadLanguage(UiLanguage.English, out Dictionary<string, string> english);
        _englishTranslations = englishLoaded
            ? english
            : new Dictionary<string, string>(StringComparer.Ordinal);

        if (language == UiLanguage.English)
        {
            _translations = _englishTranslations;
            return;
        }

        if (TryLoadLanguage(language, out Dictionary<string, string> translations))
        {
            _translations = translations;
            return;
        }

        if (englishLoaded)
        {
            Logger.LogWarning(
                $"Falling back to English UI translations after {language.ToCultureCode()} failed to load."
            );
            _translations = _englishTranslations;
            return;
        }

        // Never retain a previously selected language after a load failure. Returning
        // keys is deterministic and keeps the UI usable even when the packaged English
        // catalog is also unavailable or malformed.
        _translations = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private bool TryLoadLanguage(UiLanguage language, out Dictionary<string, string> translations)
    {
        string filePath = Path.Combine(_translationDirectory, language.GetTranslationFileName());
        try
        {
            if (!File.Exists(filePath))
            {
                Logger.LogWarning($"Translation file not found: {filePath}");
                translations = null!;
                return false;
            }

            string json = File.ReadAllText(filePath);
            Dictionary<string, string>? loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            if (loaded is null)
                throw new JsonSerializationException("The translation catalog contained no JSON object.");

            translations = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
            return true;
        }
        catch (Exception ex)
        {
            // A packaged translation failure must not prevent application startup.
            Logger.LogWarning($"Failed to load translation file for language {language.ToCultureCode()}", ex);
            translations = null!;
            return false;
        }
    }

    public string this[string key] => Translate(key);

    public string Translate(string key)
    {
        if (_translations.TryGetValue(key, out string? value))
            return value;
        return _englishTranslations.TryGetValue(key, out value) ? value : key;
    }

    public string Format(string key, params object[] args)
    {
        string format = Translate(key);
        return args == null || args.Length == 0 ? format : string.Format(CultureInfo.CurrentCulture, format, args);
    }
}
