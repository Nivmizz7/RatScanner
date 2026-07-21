using System.Globalization;

namespace RatScanner.Presentation;

/// <summary>
/// Shared access to UI strings for presentation helpers that run outside Razor.
/// When <see cref="Localizer"/> is unset (unit tests, early init), English fallbacks are used.
/// </summary>
internal static class PresentationText
{
    internal static LocalizationService? Localizer { get; set; }

    internal static string T(string key, string englishFallback)
    {
        if (Localizer is null)
            return englishFallback;
        string value = Localizer.Translate(key);
        return value == key ? englishFallback : value;
    }

    internal static string F(string key, string englishFallback, params object[] args)
    {
        string format = T(key, englishFallback);
        return args is null || args.Length == 0 ? format : string.Format(CultureInfo.CurrentCulture, format, args);
    }
}
