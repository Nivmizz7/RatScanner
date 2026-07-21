namespace RatScanner.TarkovDev;

/// <summary>
/// Quest / hideout remaining counts broken down by found-in-raid requirement.
/// Visual FIR detection on the scanned icon is intentionally not implemented yet.
/// </summary>
public readonly record struct RequirementBreakdown(int Total, int FoundInRaid, int NonFoundInRaid)
{
    public bool Any => Total > 0;
    public bool HasFirNeed => FoundInRaid > 0;
    public bool HasNonFirNeed => NonFoundInRaid > 0;
}

/// <summary>How the player can obtain this item besides looting.</summary>
public readonly record struct AcquisitionInfo(bool CanCraft, int CraftRecipeCount, bool CanBarter, int BarterOfferCount)
{
    public bool Any => CanCraft || CanBarter;
}
