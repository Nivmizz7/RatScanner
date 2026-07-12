using System;
using System.Globalization;

namespace RatScanner.Presentation;

internal static class RecommendationSelector
{
    internal static RecommendationViewModel Select(
        int? flea,
        int? trader,
        string? traderName,
        int questRemaining,
        int hideoutRemaining
    )
    {
        if (questRemaining > 0)
            return new(
                RecommendationType.KeepForQuest,
                "Keep for quest",
                $"{questRemaining} still required for active quests.",
                null,
                null
            );
        if (hideoutRemaining > 0)
            return new(
                RecommendationType.KeepForHideout,
                "Keep for hideout",
                $"{hideoutRemaining} still required for hideout upgrades.",
                null,
                null
            );
        if (flea is null && trader is null)
            return new(
                RecommendationType.PriceUnavailable,
                "Price unavailable",
                "No current market or trader value is available.",
                null,
                null
            );
        if (flea is int fleaValue && (trader is null || fleaValue > trader))
        {
            int? difference = trader is int traderValue ? fleaValue - traderValue : null;
            int? percent = trader is > 0 ? (int)Math.Round((double)difference!.Value / trader.Value * 100) : null;
            string explanation = difference is null
                ? "No comparable trader offer is available."
                : $"Market value is {PriceFormatter.Format(difference)} above {traderName ?? "the best trader"}.";
            return new(RecommendationType.SellOnFlea, "Sell on Flea Market", explanation, difference, percent);
        }
        return new(
            RecommendationType.SellToTrader,
            $"Sell to {traderName ?? "best trader"}",
            "The trader offer meets or exceeds the current market value.",
            trader - flea,
            flea is > 0 ? (int)Math.Round((double)(trader!.Value - flea.Value) / flea.Value * 100) : null
        );
    }
}

internal static class PriceFormatter
{
    internal static string Format(int? value) =>
        value is > 0 ? string.Format(CultureInfo.CurrentCulture, "{0:N0} ₽", value) : "Unavailable";
}

internal static class FreshnessFormatter
{
    internal static string Format(DateTimeOffset? updatedAt, DateTimeOffset? now = null)
    {
        if (updatedAt is null)
            return string.Empty;
        TimeSpan age = (now ?? DateTimeOffset.UtcNow) - updatedAt.Value.ToUniversalTime();
        if (age.TotalMinutes < 1)
            return "Updated just now";
        if (age.TotalHours < 1)
            return $"Updated {(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1)
            return $"Updated {(int)age.TotalHours} hr ago";
        return $"Updated {Math.Max(1, (int)age.TotalDays)} days ago";
    }
}
