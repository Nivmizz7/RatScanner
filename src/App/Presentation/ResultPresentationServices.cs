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
                PresentationText.T("RecKeepForQuest", "Keep for quest"),
                PresentationText.F(
                    "RecQuestRequired",
                    "{0} still required for active quests.{1}{2}",
                    quests.Total,
                    firNote,
                    craftHint
                ),
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
                PresentationText.T("RecKeepForHideout", "Keep for hideout"),
                PresentationText.F(
                    "RecHideoutRequired",
                    "{0} still required for hideout upgrades.{1}{2}",
                    hideout.Total,
                    firNote,
                    craftHint
                ),
                null,
                null
            );
        }

        if (flea is null && trader is null)
        {
            string acquire = AcquireFallback(acquisition);
            return new(
                RecommendationType.PriceUnavailable,
                PresentationText.T("RecPriceUnavailable", "Price unavailable"),
                PresentationText.T("RecNoMarketValue", "No current market or trader value is available.") + acquire,
                null,
                null
            );
        }

        if (flea is int fleaValue && (trader is null || fleaValue > trader))
        {
            int? difference = trader is int traderValue ? fleaValue - traderValue : null;
            int? percent = trader is > 0 ? (int)Math.Round((double)difference!.Value / trader.Value * 100) : null;
            string bestTrader = traderName ?? PresentationText.T("RecBestTrader", "the best trader");
            string explanation = difference is null
                ? PresentationText.T("RecNoTraderOffer", "No comparable trader offer is available.")
                : PresentationText.F(
                    "RecMoreThanTrader",
                    "{0} more than {1}.",
                    PriceFormatter.Format(difference),
                    bestTrader
                );
            explanation += AcquireFallback(acquisition);
            return new(
                RecommendationType.SellOnFlea,
                PresentationText.T("RecSellOnFleaMarket", "Sell on Flea Market"),
                explanation,
                difference,
                percent
            );
        }

        string sellTraderName = traderName ?? PresentationText.T("RecBestTraderShort", "best trader");
        string sellTrader =
            PresentationText.T("RecTraderMeetsOrExceeds", "The trader offer meets or exceeds the current market value.")
            + AcquireFallback(acquisition);
        return new(
            RecommendationType.SellToTrader,
            PresentationText.F("RecSellToNamed", "Sell to {0}", sellTraderName),
            sellTrader,
            trader - flea,
            flea is > 0 ? (int)Math.Round((double)(trader!.Value - flea.Value) / flea.Value * 100) : null
        );
    }

    private static string FirNote(RequirementBreakdown breakdown)
    {
        if (breakdown.HasFirNeed && breakdown.HasNonFirNeed)
            return PresentationText.F(
                "RecFirMixed",
                " {0} must be found in raid; {1} can be non-FIR.",
                breakdown.FoundInRaid,
                breakdown.NonFoundInRaid
            );
        if (breakdown.HasFirNeed)
            return PresentationText.T(
                "RecFirRequired",
                " Requires found in raid (confirm FIR on the item — visual FIR detection is not available yet)."
            );
        return PresentationText.T("RecNonFirOk", " Non-FIR is OK for these needs.");
    }

    private static string CraftHint(AcquisitionInfo acquisition, bool needsFir)
    {
        if (!acquisition.CanCraft)
            return string.Empty;
        // Crafted items are always FIR in EFT — especially useful when quest/hideout needs FIR.
        return needsFir
            ? PresentationText.T("RecCraftableFir", " Craftable in hideout (crafted items are always found in raid).")
            : PresentationText.T("RecAlsoCraftable", " Also craftable in hideout.");
    }

    private static string AcquireFallback(AcquisitionInfo acquisition)
    {
        if (!acquisition.Any)
            return string.Empty;
        StringBuilder sb = new(" ");
        if (acquisition.CanCraft && acquisition.CanBarter)
            sb.Append(
                PresentationText.F(
                    "RecCraftOrBarter",
                    "Can be crafted ({0}) or bartered for ({1}).",
                    acquisition.CraftRecipeCount,
                    acquisition.BarterOfferCount
                )
            );
        else if (acquisition.CanCraft)
            sb.Append(
                PresentationText.F(
                    "RecCraftOnly",
                    "Can be crafted in hideout ({0} recipe(s); output is always FIR).",
                    acquisition.CraftRecipeCount
                )
            );
        else
            sb.Append(
                PresentationText.F("RecBarterOnly", "Can be bartered for ({0} offer(s)).", acquisition.BarterOfferCount)
            );
        return sb.ToString();
    }
}

internal static class PriceFormatter
{
    internal static string Format(int? value) =>
        value is > 0
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} ₽", value)
            : PresentationText.T("PriceUnavailable", "Unavailable");
}

internal static class FreshnessFormatter
{
    internal static string Format(DateTimeOffset? updatedAt, DateTimeOffset? now = null)
    {
        if (updatedAt is null)
            return string.Empty;
        TimeSpan age = (now ?? DateTimeOffset.UtcNow) - updatedAt.Value.ToUniversalTime();
        if (age.TotalMinutes < 1)
            return PresentationText.T("UpdatedJustNow", "Updated just now");
        if (age.TotalHours < 1)
            return PresentationText.F("UpdatedMinutesAgo", "Updated {0} min ago", (int)age.TotalMinutes);
        if (age.TotalDays < 1)
            return PresentationText.F("UpdatedHoursAgo", "Updated {0} h ago", (int)age.TotalHours);
        int days = Math.Max(1, (int)age.TotalDays);
        return PresentationText.F("UpdatedDaysAgo", "Updated {0} d ago", days);
    }
}

/// <summary>Maps json.tarkov.dev item type tags to user-facing labels.</summary>
internal static class ItemTypeLabel
{
    internal static string Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return PresentationText.T("ItemTypeGeneric", "Item");

        // JSON types are lowercase/camelCase; tolerate accidental PascalCase.
        string key = raw.Trim();
        string folded = key.Length == 0 ? key : char.ToLowerInvariant(key[0]) + key.Substring(1);

        return folded switch
        {
            "ammo" => PresentationText.T("ItemTypeAmmo", "Ammunition"),
            "ammoBox" => PresentationText.T("ItemTypeAmmoBox", "Ammunition container"),
            "armor" => PresentationText.T("ItemTypeArmor", "Armor"),
            "armorPlate" => PresentationText.T("ItemTypeArmorPlate", "Armor plate"),
            "backpack" => PresentationText.T("ItemTypeBackpack", "Backpack"),
            "barter" => PresentationText.T("ItemTypeBarter", "Barter item"),
            "container" => PresentationText.T("ItemTypeContainer", "Container"),
            "glasses" => PresentationText.T("ItemTypeGlasses", "Eyewear"),
            "grenade" => PresentationText.T("ItemTypeGrenade", "Grenade"),
            "gun" => PresentationText.T("ItemTypeGun", "Weapon"),
            "headphones" => PresentationText.T("ItemTypeHeadphones", "Headset"),
            "helmet" => PresentationText.T("ItemTypeHelmet", "Helmet"),
            "injectors" => PresentationText.T("ItemTypeInjectors", "Injector"),
            "keys" => PresentationText.T("ItemTypeKeys", "Key"),
            "markedOnly" => PresentationText.T("ItemTypeMarkedOnly", "Marked only"),
            "meds" => PresentationText.T("ItemTypeMeds", "Medical item"),
            "mods" => PresentationText.T("ItemTypeMods", "Modification"),
            "noFlea" => PresentationText.T("ItemTypeNoFlea", "Not flea-listed"),
            "pistolGrip" => PresentationText.T("ItemTypePistolGrip", "Pistol grip"),
            "preset" => PresentationText.T("ItemTypePreset", "Weapon preset"),
            "provisions" => PresentationText.T("ItemTypeProvisions", "Provision"),
            "rig" => PresentationText.T("ItemTypeRig", "Tactical rig"),
            "suppressor" => PresentationText.T("ItemTypeSuppressor", "Suppressor"),
            "wearable" => PresentationText.T("ItemTypeWearable", "Wearable"),
            "poster" => PresentationText.T("ItemTypePoster", "Poster"),
            "specialSlot" => PresentationText.T("ItemTypeSpecialSlot", "Special slot"),
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
