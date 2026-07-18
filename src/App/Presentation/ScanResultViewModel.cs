using System;

namespace RatScanner.Presentation;

internal sealed record ScanResultViewModel(
    ScanItemViewModel Item,
    RecognitionViewModel Recognition,
    PricingViewModel Pricing,
    TraderViewModel? Trader,
    RecommendationViewModel Recommendation,
    RequirementViewModel Quests,
    RequirementViewModel Hideout,
    AcquisitionViewModel Acquisition,
    DateTimeOffset? ScannedAt,
    bool IsHistoricalResult
);

internal sealed record ScanItemViewModel(
    string? Id,
    string? Name,
    string? ShortName,
    string? ImageUrl,
    string? MarketUrl,
    string? WikiUrl,
    string? ItemType,
    int Quantity,
    int Width,
    int Height
)
{
    public int SlotCount => Math.Max(1, Width * Height);
}

internal sealed record RecognitionViewModel(float? Confidence, string Label, bool IsManualSelection);

internal sealed record PricingViewModel(
    int? FleaPrice,
    int? FleaPricePerSlot,
    string SourceName,
    DateTimeOffset? UpdatedAt,
    bool BannedOnFlea
);

internal sealed record TraderViewModel(string? Name, string? ImageUrl, int? TotalPrice, int? PricePerSlot);

internal enum RecommendationType
{
    KeepForQuest,
    KeepForHideout,
    SellOnFlea,
    SellToTrader,
    PriceUnavailable,
}

internal sealed record RecommendationViewModel(
    RecommendationType Type,
    string Title,
    string Explanation,
    int? DifferenceAmount,
    int? DifferencePercent
);

internal enum RequirementStatus
{
    NotRequired,
    Required,
    Unavailable,
}

/// <param name="RequiresFoundInRaid">How many of the remaining needs mandate FIR.</param>
/// <param name="NonFoundInRaid">Remaining needs that accept non-FIR items.</param>
internal sealed record RequirementViewModel(
    RequirementStatus Status,
    int? RemainingRequired,
    int RequiresFoundInRaid = 0,
    int NonFoundInRaid = 0
)
{
    public bool HasFirNeed => RequiresFoundInRaid > 0;
    public bool HasNonFirNeed => NonFoundInRaid > 0;
}

/// <summary>
/// Alternate ways to get the item. Craft outputs are always FIR in-game;
/// barters are not. Visual FIR check on the scan is not implemented.
/// </summary>
internal sealed record AcquisitionViewModel(bool CanCraft, int CraftRecipeCount, bool CanBarter, int BarterOfferCount)
{
    public bool Any => CanCraft || CanBarter;
}
