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
        RequirementBreakdown quests = scan.Item.GetTaskRequirementBreakdown();
        RequirementBreakdown hideout = scan.Item.GetHideoutRequirementBreakdown();
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
            new RequirementBreakdown(questRemaining, questRemaining, 0),
            new RequirementBreakdown(hideoutRemaining, 0, hideoutRemaining),
            scan.Item.GetAcquisitionInfo(),
            isHistoricalResult
        );

    internal static ScanResultViewModel Map(
        ItemScan scan,
        RequirementBreakdown quests,
        RequirementBreakdown hideout,
        AcquisitionInfo acquisition,
        bool isHistoricalResult
    )
    {
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
        int? fleaPrice = item.Avg24HPrice is > 0 ? item.Avg24HPrice : null;
        var traderOffer = item.GetBestTraderOffer();
        var trader = item.GetBestTraderOfferVendor();
        int? traderPrice = traderOffer?.PriceRub is > 0 ? traderOffer.PriceRub : null;
        string? itemType = item.Types?.FirstOrDefault();

        return new ScanResultViewModel(
            new ScanItemViewModel(
                item.Id,
                item.Name,
                item.ShortName,
                item.IconLink,
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
                updatedAt
            ),
            traderPrice is null
                ? null
                : new TraderViewModel(trader?.Name, trader?.Trader?.ImageLink, traderPrice, traderPrice / slots),
            RecommendationSelector.Select(fleaPrice, traderPrice, trader?.Name, quests, hideout, acquisition),
            MapRequirement(quests),
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
