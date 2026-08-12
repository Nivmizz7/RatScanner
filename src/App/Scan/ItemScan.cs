using RatEye;

namespace RatScanner.Scan;

public abstract class ItemScan
// Base Scan Data
{
    public TarkovDev.Item Item { get; set; } = new TarkovDev.Item();

    public float Confidence { get; set; } = 0;

    public string IconPath { get; set; } = null!;

    public long DissapearAt { get; set; } = 0;

    public bool IsSeed { get; init; }

    /// <summary>
    /// Sequence of the <see cref="Diagnostics.PerfTrace"/> that produced this scan,
    /// so the overlay and main window can attribute their render cost back to the
    /// correct scan instead of to whichever scan is currently open. Zero when the
    /// scan did not come from a traced entry point (seed or manual selection).
    /// </summary>
    internal long PerfSequence { get; init; }

    // Scan tooltip location
    public abstract Vector2 GetToolTipPosition();
}
