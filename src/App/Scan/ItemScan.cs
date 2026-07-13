using RatEye;

namespace RatScanner.Scan;

public abstract class ItemScan
// Base Scan Data
{
    public TarkovDev.GraphQL.Item Item { get; set; } = new TarkovDev.GraphQL.Item();

    public float Confidence { get; set; } = 0;

    public string IconPath { get; set; } = null!;

    public long DissapearAt { get; set; } = 0;

    public bool IsSeed { get; init; }

    // Scan tooltip location
    public abstract Vector2 GetToolTipPosition();
}
