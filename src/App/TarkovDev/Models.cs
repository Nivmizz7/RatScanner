using System.Collections.Generic;
using System.Linq;

namespace RatScanner.TarkovDev;

/// <summary>Game mode segment used by json.tarkov.dev paths (regular / pve).</summary>
public enum GameMode
{
    Regular,
    Pve,
}

/// <summary>Which TarkovTracker backend supplies PvP progress.</summary>
public enum PvpSource
{
    Org = 0,
    Io = 1,
}

/// <summary>
/// App-facing item model populated from <c>json.tarkov.dev</c> (not GraphQL).
/// Field set is intentionally slim — only what the scanner UI / tracking needs today.
/// </summary>
public sealed class Item
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? ShortName { get; set; }
    public string? Updated { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? WikiLink { get; set; }
    public string? Link { get; set; }
    public string? IconLink { get; set; }
    public string? BaseImageLink { get; set; }
    public int? Avg24HPrice { get; set; }
    public string? BackgroundColor { get; set; }

    /// <summary>Raw type tags from JSON (e.g. gun, ammo, mods) — lowercase.</summary>
    public IReadOnlyList<string>? Types { get; set; }
    public ItemProperties? Properties { get; set; }
    public IReadOnlyList<ItemSellPrice>? SellFor { get; set; }

    /// <summary>True when the item has the <c>noFlea</c> type tag (cannot be sold on the flea market).</summary>
    public bool IsBannedOnFlea =>
        Types is not null && Types.Contains("noFlea", System.StringComparer.OrdinalIgnoreCase);

    // Future: craft/barter/FIR recommendation surfaces (not populated in this pass).
    // public bool CanBeCrafted { get; set; }
    // public bool CanBeBartered { get; set; }
}

public sealed class ItemProperties
{
    /// <summary>JSON propertiesType, e.g. ItemPropertiesAmmo.</summary>
    public string? PropertiesType { get; set; }
    public string? Caliber { get; set; }
    public int? Damage { get; set; }
    public int? PenetrationPower { get; set; }
    public float? FragmentationChance { get; set; }

    public bool IsAmmo =>
        string.Equals(PropertiesType, "ItemPropertiesAmmo", System.StringComparison.OrdinalIgnoreCase)
        || (
            !string.IsNullOrEmpty(Caliber)
            && PropertiesType?.Contains("Ammo", System.StringComparison.OrdinalIgnoreCase) == true
        );
}

public sealed class ItemSellPrice
{
    public int? PriceRub { get; set; }
    public TraderOffer? Vendor { get; set; }
}

public sealed class TraderOffer
{
    public string? Name { get; set; }
    public string? NormalizedName { get; set; }
    public Trader? Trader { get; set; }
}

public sealed class Trader
{
    public string? Id { get; set; }
    public string? ImageLink { get; set; }
}

public sealed class Task
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? WikiLink { get; set; }
    public string? TaskImageLink { get; set; }
    public bool? KappaRequired { get; set; }
    public string? TraderImageLink { get; set; }
    public IReadOnlyList<TaskObjective>? Objectives { get; set; }

    /// <summary>Minimum PMC level required to unlock the task (0/1 = no meaningful gate).</summary>
    public int MinPlayerLevel { get; set; }

    /// <summary>Faction restriction ("USEC" / "BEAR" / "Any" / null).</summary>
    public string? FactionName { get; set; }

    /// <summary>Prerequisite tasks with the states that satisfy them.</summary>
    public IReadOnlyList<TaskPrerequisite>? TaskRequirements { get; set; }

    /// <summary>Trader standing gates (rep / loyalty) — player data for these is NOT available from the tracker API.</summary>
    public IReadOnlyList<TaskTraderRequirement>? TraderRequirements { get; set; }

    /// <summary>
    /// True when the task has gates RatScanner cannot model (dialogue unlocks,
    /// timed availability, Lightkeeper) — must be treated as conditional.
    /// </summary>
    public bool HasUnmodeledRequirements { get; set; }
}

public sealed class TaskPrerequisite
{
    public string TaskId { get; set; } = string.Empty;
    public IReadOnlyList<string> Statuses { get; set; } = System.Array.Empty<string>();
}

public sealed class TaskTraderRequirement
{
    public string? RequirementType { get; set; }
    public string? CompareMethod { get; set; }
    public double Value { get; set; }
    public string? TraderId { get; set; }
}

/// <summary>
/// Flattened objective (JSON objectives are heterogeneous). Only fields required for tracking.
/// foundInRaid is retained for future FIR-aware recommendations (keep vs sell non-FIR).
/// </summary>
public sealed class TaskObjective
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? Description { get; set; }
    public int Count { get; set; }
    public bool FoundInRaid { get; set; }

    /// <summary>Optional objectives are not strictly required and never add to needed counts.</summary>
    public bool Optional { get; set; }

    public IReadOnlyList<string>? ItemIds { get; set; }
    public string? MarkerItemId { get; set; }
    public string? BuildItemId { get; set; }
}

public sealed class HideoutStation
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public IReadOnlyList<HideoutStationLevel>? Levels { get; set; }
}

public sealed class HideoutStationLevel
{
    public string Id { get; set; } = string.Empty;
    public IReadOnlyList<RequirementItem>? ItemRequirements { get; set; }
}

public sealed class RequirementItem
{
    public string? Id { get; set; }
    public int Count { get; set; }
    public string? ItemId { get; set; }

    /// <summary>Whether hideout upgrade requires FIR — used later for keep/sell advice.</summary>
    public bool FoundInRaid { get; set; }
}

public sealed class Map
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? NormalizedName { get; set; }
}

/// <summary>
/// Craft blueprint (json.tarkov.dev/regular/crafts). Fetched and indexed during
/// TarkovDevAPI.InitializeCache to drive the "craftable" scan hint; future product idea:
/// also surface FIR + hideout unlock state via TarkovTracker.
/// </summary>
public sealed class Craft
{
    public string Id { get; set; } = string.Empty;
    public string? StationId { get; set; }
    public int Level { get; set; }
    public int DurationSeconds { get; set; }
    public string? ProductItemId { get; set; }
    public int ProductCount { get; set; }
    public IReadOnlyList<CraftIngredient>? RequiredItems { get; set; }
}

public sealed class CraftIngredient
{
    public string? ItemId { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// Barter (json.tarkov.dev/regular/barters). Fetched and indexed during
/// TarkovDevAPI.InitializeCache to drive the "barterable" scan hint; future product idea:
/// contrast "can barter for this" with find-in-raid needs.
/// </summary>
public sealed class Barter
{
    public string Id { get; set; } = string.Empty;
    public string? TraderId { get; set; }
    public int MinTraderLevel { get; set; }
    public string? OfferedItemId { get; set; }
    public int OfferedCount { get; set; }
    public IReadOnlyList<CraftIngredient>? RequiredItems { get; set; }
}
