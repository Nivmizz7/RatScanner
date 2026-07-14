using System;
using System.Globalization;

namespace RatScanner;

public static class Extensions
{
    public static string ToShortString(this int value)
    {
        string sign = value < 0 ? "-" : "";
        // Work from the magnitude (as long, to also cover int.MinValue) so the sign never
        // affects digit slicing or suffix selection.
        string str = Math.Abs((long)value).ToString(CultureInfo.InvariantCulture);
        if (str.Length < 4)
            return $"{sign}{str}";

        string[] suffixes = new string[] { "", "K", "M", "B", "T", "Q" };

        string digits = str[..3];

        int dotPos = str.Length % 3;
        if (dotPos != 0)
            digits = digits[..dotPos];

        string suffix = suffixes[(int)Math.Floor((str.Length - 1) / 3f)];
        return $"{sign}{digits}{suffix}";
    }

    public static string ToShortString(this int? value) => ToShortString(value ?? 0);

    public static string AsRubs(this int value)
    {
        string text = $"{value:n0}";
        string numberGroupSeparator = NumberFormatInfo.CurrentInfo.NumberGroupSeparator;
        text = text.Replace(numberGroupSeparator, RatConfig.ToolTip.DigitGroupingSymbol);
        return $"{text} ₽";
    }

    public static string AsRubs(this int? value) => AsRubs(value ?? 0);

    public static string AsRubs(this string value) => $"{value} ₽";

    /// <summary>
    /// Maps RatStash OCR language to json.tarkov.dev locale file suffix (items_en, tasks_ru, …).
    /// </summary>
    public static string ToTarkovDevLocale(this RatStash.Language lang) =>
        lang switch
        {
            RatStash.Language.Chinese => "zh",
            RatStash.Language.Czech => "cs",
            RatStash.Language.English => "en",
            RatStash.Language.Spanish => "es",
            RatStash.Language.SpanishMexican => "es",
            RatStash.Language.French => "fr",
            RatStash.Language.German => "de",
            RatStash.Language.Hungarian => "hu",
            RatStash.Language.Italian => "it",
            RatStash.Language.Japanese => "ja",
            RatStash.Language.Korean => "ko",
            RatStash.Language.Polish => "pl",
            RatStash.Language.Portuguese => "pt",
            RatStash.Language.Russian => "ru",
            RatStash.Language.Slovak => "sk",
            RatStash.Language.Turkish => "tr",
            _ => "en",
        };
}
