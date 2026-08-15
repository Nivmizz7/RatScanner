using System;
using System.Linq;
using RatScanner.Scan;
using RatScanner.TarkovDev;
using RatScanner.ViewModel;

namespace RatScanner.Presentation;

internal static class ScanResultAdapter
{
    internal static ScanResultViewModel Map(MenuVM menu) => Map(menu.LastItemScan, menu, false);

    internal static ScanResultViewModel Map(ItemScan scan, MenuVM menu, bool isHistoricalResult)
    {
        QuestNeedReport quests = scan.Item.GetQuestNeedReport(menu.CurrentUserProgress);
        RequirementBreakdown hideout = scan.Item.GetHideoutRequirementBreakdown(menu.CurrentUserProgress);
        AcquisitionInfo acquisition = scan.Item.GetAcquisitionInfo();
        return Map(scan, quests, hideout, acquisition, isHistoricalResult);
    }

    internal static ScanResultViewModel Map(
        ItemScan scan,
        int questRemaining,
        int hideoutRemaining,
        bool isHistoricalResult
    ) =>
        Map(
            scan,
            new QuestNeedReport { ActiveNow = questRemaining, ActiveNowFir = questRemaining },
            new RequirementBreakdown(hideoutRemaining, 0, hideoutRemaining),
            scan.Item.GetAcquisitionInfo(),
            isHistoricalResult
        );

    internal static ScanResultViewModel Map(
        ItemScan scan,
        QuestNeedReport quests,
        RequirementBreakdown hideout,
        AcquisitionInfo acquisition,
        bool isHistoricalResult
    )
    {
        // The advisory applies to any unverifiable assembly need (current OR future/conditional):
        // a bare receiver must never read as a guaranteed quest hand-in.
        bool isWeapon = scan.Item.Types?.Contains("gun", StringComparer.OrdinalIgnoreCase) == true;
        bool weaponUsableAdvisory = isWeapon && quests.WeaponHandInTotal > 0;
        var item = scan.Item;
        int slots = Math.Max(1, item.Width * item.Height);
        bool manual = scan is DefaultItemScan;
        float? confidence = manual ? null : Math.Clamp(scan.Confidence, 0, 1);
        string confidenceLabel = manual
            ? PresentationText.T("RecognitionManual", "Manual selection")
            : confidence switch
            {
                >= .9f => PresentationText.T("RecognitionHigh", "High"),
                >= .7f => PresentationText.T("RecognitionMedium", "Medium"),
                _ => PresentationText.T("RecognitionLow", "Low"),
            };

        DateTimeOffset? updatedAt = DateTimeOffset.TryParse(item.Updated, out DateTimeOffset updated) ? updated : null;
        bool bannedOnFlea = item.IsBannedOnFlea;
        int? fleaPrice =
            bannedOnFlea ? null
            : item.Avg24HPrice is > 0 ? item.Avg24HPrice
            : null;
        var traderOffer = item.GetBestTraderOffer();
        var trader = item.GetBestTraderOfferVendor();
        int? traderPrice = traderOffer?.PriceRub is > 0 ? traderOffer.PriceRub : null;
        string? itemType = item.Types is { Count: > 0 } types ? types[0] : null;

        return new ScanResultViewModel(
            new ScanItemViewModel(
                item.Id,
                item.Name,
                item.ShortName,
                // Prefer the locally installed icon. The remote catalog link costs a
                // network round trip, during which the reused <img> element still
                // shows the previous scan's icon next to this scan's name and price.
                ItemIconResolver.Resolve(scan.IconPath, item.Id, item.IconLink),
                item.Link,
                item.GetWikiLink(),
                itemType,
                1,
                item.Width,
                item.Height
            ),
            new RecognitionViewModel(confidence, confidenceLabel, manual),
            new PricingViewModel(
                fleaPrice,
                fleaPrice / slots,
                PresentationText.T("PriceSourceTarkovDev", "Tarkov.dev"),
                updatedAt,
                bannedOnFlea
            ),
            traderPrice is null
                ? null
                : new TraderViewModel(trader?.Name, trader?.Trader?.ImageLink, traderPrice, traderPrice / slots),
            RecommendationSelector.Select(fleaPrice, traderPrice, trader?.Name, quests, hideout, acquisition),
            new QuestNeedViewModel(quests, weaponUsableAdvisory),
            MapRequirement(hideout),
            new AcquisitionViewModel(
                acquisition.CanCraft,
                acquisition.CraftRecipeCount,
                acquisition.CanBarter,
                acquisition.BarterOfferCount
            ),
            null,
            isHistoricalResult
        );
    }

    private static RequirementViewModel MapRequirement(RequirementBreakdown breakdown) =>
        breakdown.Any
            ? new RequirementViewModel(
                RequirementStatus.Required,
                breakdown.Total,
                breakdown.FoundInRaid,
                breakdown.NonFoundInRaid
            )
            : new RequirementViewModel(RequirementStatus.NotRequired, 0);
}
