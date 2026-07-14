using System;
using System.Globalization;
using System.Text;
using RatScanner.TarkovDev;

namespace RatScanner.Presentation;

internal static class RecommendationSelector
{
    /// <summary>Legacy overloads used by unit tests.</summary>
    internal static RecommendationViewModel Select(
        int? flea,
        int? trader,
        string? traderName,
        int questRemaining,
        int hideoutRemaining
    ) =>
        Select(
            flea,
            trader,
            traderName,
            new RequirementBreakdown(questRemaining, questRemaining, 0),
            new RequirementBreakdown(hideoutRemaining, 0, hideoutRemaining),
            default
        );

    internal static RecommendationViewModel Select(
        int? flea,
        int? trader,
        string? traderName,
        RequirementBreakdown quests,
        RequirementBreakdown hideout,
        AcquisitionInfo acquisition
    )
    {
        if (quests.Any)
        {
            string firNote = FirNote(quests);
            string craftHint = CraftHint(acquisition, quests.HasFirNeed);
            return new(
                RecommendationType.KeepForQuest,
                "Keep for quest",
                $"{quests.Total} still required for active quests.{firNote}{craftHint}",
                null,
                null
            );
        }

        if (hideout.Any)
        {
            string firNote = FirNote(hideout);
            string craftHint = CraftHint(acquisition, hideout.HasFirNeed);
            return new(
                RecommendationType.KeepForHideout,
                "Keep for hideout",
                $"{hideout.Total} still required for hideout upgrades.{firNote}{craftHint}",
                null,
                null
            );
        }

        if (flea is null && trader is null)
        {
            string acquire = AcquireFallback(acquisition);
            return new(
                RecommendationType.PriceUnavailable,
                "Price unavailable",
                "No current market or trader value is available." + acquire,
                null,
                null
            );
        }

        if (flea is int fleaValue && (trader is null || fleaValue > trader))
        {
            int? difference = trader is int traderValue ? fleaValue - traderValue : null;
            int? percent = trader is > 0 ? (int)Math.Round((double)difference!.Value / trader.Value * 100) : null;
            string explanation = difference is null
                ? "No comparable trader offer is available."
                : $"{PriceFormatter.Format(difference)} more than {traderName ?? "the best trader"}.";
            explanation += AcquireFallback(acquisition);
            return new(RecommendationType.SellOnFlea, "Sell on Flea Market", explanation, difference, percent);
        }

        string sellTrader =
            "The trader offer meets or exceeds the current market value." + AcquireFallback(acquisition);
        return new(
            RecommendationType.SellToTrader,
            $"Sell to {traderName ?? "best trader"}",
            sellTrader,
            trader - flea,
            flea is > 0 ? (int)Math.Round((double)(trader!.Value - flea.Value) / flea.Value * 100) : null
        );
    }

    private static string FirNote(RequirementBreakdown breakdown)
    {
        if (breakdown.HasFirNeed && breakdown.HasNonFirNeed)
            return $" {breakdown.FoundInRaid} must be found in raid; {breakdown.NonFoundInRaid} can be non-FIR.";
        if (breakdown.HasFirNeed)
            return $" Requires found in raid (confirm FIR on the item — visual FIR detection is not available yet).";
        return " Non-FIR is OK for these needs.";
    }

    private static string CraftHint(AcquisitionInfo acquisition, bool needsFir)
    {
        if (!acquisition.CanCraft)
            return string.Empty;
        // Crafted items are always FIR in EFT — especially useful when quest/hideout needs FIR.
        return needsFir
            ? " Craftable in hideout (crafted items are always found in raid)."
            : " Also craftable in hideout.";
    }

    private static string AcquireFallback(AcquisitionInfo acquisition)
    {
        if (!acquisition.Any)
            return string.Empty;
        StringBuilder sb = new(" ");
        if (acquisition.CanCraft && acquisition.CanBarter)
            sb.Append(
                $"Can be crafted ({acquisition.CraftRecipeCount}) or bartered for ({acquisition.BarterOfferCount})."
            );
        else if (acquisition.CanCraft)
            sb.Append($"Can be crafted in hideout ({acquisition.CraftRecipeCount} recipe(s); output is always FIR).");
        else
            sb.Append($"Can be bartered for ({acquisition.BarterOfferCount} offer(s)).");
        return sb.ToString();
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
        int days = Math.Max(1, (int)age.TotalDays);
        return $"Updated {days} {(days == 1 ? "day" : "days")} ago";
    }
}

/// <summary>Maps json.tarkov.dev item type tags to user-facing labels.</summary>
internal static class ItemTypeLabel
{
    internal static string Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Item";

        // JSON types are lowercase/camelCase; tolerate accidental PascalCase.
        string key = raw.Trim();
        string folded = key.Length == 0 ? key : char.ToLowerInvariant(key[0]) + key.Substring(1);

        return folded switch
        {
            "ammo" => "Ammunition",
            "ammoBox" => "Ammunition container",
            "armor" => "Armor",
            "armorPlate" => "Armor plate",
            "backpack" => "Backpack",
            "barter" => "Barter item",
            "container" => "Container",
            "glasses" => "Eyewear",
            "grenade" => "Grenade",
            "gun" => "Weapon",
            "headphones" => "Headset",
            "helmet" => "Helmet",
            "injectors" => "Injector",
            "keys" => "Key",
            "markedOnly" => "Marked only",
            "meds" => "Medical item",
            "mods" => "Modification",
            "noFlea" => "Not flea-listed",
            "pistolGrip" => "Pistol grip",
            "preset" => "Weapon preset",
            "provisions" => "Provision",
            "rig" => "Tactical rig",
            "suppressor" => "Suppressor",
            "wearable" => "Wearable",
            "poster" => "Poster",
            "specialSlot" => "Special slot",
            _ => SplitCamelCase(key),
        };
    }

    private static string SplitCamelCase(string value)
    {
        if (value.Length == 0)
            return value;

        System.Text.StringBuilder builder = new(value.Length + 8);
        builder.Append(char.ToUpperInvariant(value[0]));
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c) && !char.IsUpper(value[i - 1]))
                builder.Append(' ');
            builder.Append(c);
        }
        return builder.ToString();
    }
}
