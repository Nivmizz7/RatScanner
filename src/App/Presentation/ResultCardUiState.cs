namespace RatScanner.Presentation;

/// <summary>
/// Open/closed state of the scan result card's Details section together with the
/// copied-id label flag that lives on the same toggle. Centralized so the
/// user-choice rule is explicit and unit-testable: scan-driven result refreshes
/// must preserve the user's Details choice; only explicit user actions (the
/// toggle, recent-scan click, or search pick) may close it.
/// </summary>
internal sealed class ResultCardUiState
{
    public bool DetailsOpen { get; private set; }

    public bool CopiedId { get; private set; }

    /// <summary>
    /// A new scan result arrived without an explicit user selection (auto scan,
    /// game-mode refresh). The Details section must not collapse — the user may be
    /// watching scan details while auto-scanning. The copied-id label is per-item,
    /// so it resets.
    /// </summary>
    public void OnResultRefreshed() => CopiedId = false;

    /// <summary>The user deliberately switched to a different item (recent scan click or search pick).</summary>
    public void OnItemSelected()
    {
        DetailsOpen = false;
        CopiedId = false;
    }

    public void ToggleDetails()
    {
        DetailsOpen = !DetailsOpen;
        if (!DetailsOpen)
            CopiedId = false;
    }

    public void MarkCopied() => CopiedId = true;

    public void MarkCopyFailed() => CopiedId = false;
}
