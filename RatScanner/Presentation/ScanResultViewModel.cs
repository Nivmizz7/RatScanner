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
) { public int SlotCount => Math.Max(1, Width * Height); }

internal sealed record RecognitionViewModel(float? Confidence, string Label, bool IsManualSelection);

internal sealed record PricingViewModel(
    int? FleaPrice,
    int? FleaPricePerSlot,
    string SourceName,
    DateTimeOffset? UpdatedAt
);

internal sealed record TraderViewModel(string? Name, string? ImageUrl, int? TotalPrice, int? PricePerSlot);

internal enum RecommendationType { KeepForQuest, KeepForHideout, SellOnFlea, SellToTrader, PriceUnavailable }
internal sealed record RecommendationViewModel(RecommendationType Type, string Title, string Explanation, int? DifferenceAmount, int? DifferencePercent);

internal enum RequirementStatus { NotRequired, Required, Unavailable }

internal sealed record RequirementViewModel(RequirementStatus Status, int? RemainingRequired);
