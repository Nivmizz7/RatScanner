using System;
using System.Linq;
using RatScanner.Scan;
using RatScanner.ViewModel;

namespace RatScanner.Presentation;

internal static class ScanResultAdapter
{
    internal static ScanResultViewModel Map(MenuVM menu) => Map(menu.LastItemScan, menu, false);

    internal static ScanResultViewModel Map(ItemScan scan, MenuVM menu, bool isHistoricalResult)
    {
        var item = scan.Item;
        int slots = Math.Max(1, item.Width * item.Height);
        bool manual = scan is DefaultItemScan;
        float? confidence = manual ? null : Math.Clamp(scan.Confidence, 0, 1);
        string confidenceLabel = manual
            ? "Manual selection"
            : confidence switch
            {
                >= .9f => "High",
                >= .7f => "Medium",
                _ => "Low",
            };

        DateTimeOffset? updatedAt = DateTimeOffset.TryParse(item.Updated, out DateTimeOffset updated) ? updated : null;
        int? fleaPrice = item.Avg24HPrice is > 0 ? item.Avg24HPrice : null;
        var traderOffer = item.GetBestTraderOffer();
        var trader = item.GetBestTraderOfferVendor();
        int? traderPrice = traderOffer?.PriceRub is > 0 ? traderOffer.PriceRub : null;
        string? itemType = item.Types?.FirstOrDefault()?.ToString();
        int questRemaining = menu.TaskRemaining;
        int hideoutRemaining = menu.HideoutRemaining;

        return new ScanResultViewModel(
            new ScanItemViewModel(
                item.Id,
                item.Name,
                item.ShortName,
                item.IconLink,
                item.Link,
                menu.WikiLink,
                itemType,
                1,
                item.Width,
                item.Height
            ),
            new RecognitionViewModel(confidence, confidenceLabel, manual),
            new PricingViewModel(fleaPrice, fleaPrice / slots, "Tarkov.dev", updatedAt),
            traderPrice is null
                ? null
                : new TraderViewModel(trader?.Name, trader?.Trader?.ImageLink, traderPrice, traderPrice / slots),
            RecommendationSelector.Select(fleaPrice, traderPrice, trader?.Name, questRemaining, hideoutRemaining),
            MapRequirement(questRemaining),
            MapRequirement(hideoutRemaining),
            null,
            isHistoricalResult
        );
    }

    private static RequirementViewModel MapRequirement(int remaining) =>
        remaining > 0
            ? new RequirementViewModel(RequirementStatus.Required, remaining)
            : new RequirementViewModel(RequirementStatus.NotRequired, 0);
}
