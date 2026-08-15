#nullable enable

using RatScanner.Presentation;
using Xunit;

namespace RatScanner.Tests;

/// <summary>
/// The Details section of the scan result card must preserve the user's choice
/// across scan-driven refreshes (auto scan enqueues fire MenuVM.PropertyChanged,
/// which re-maps the result) and close only on explicit user actions. Regression
/// coverage for the issue where Details auto-collapsed on every scan.
/// </summary>
public sealed class ResultCardUiStateTests
{
    [Fact]
    public void Result_refresh_preserves_open_details()
    {
        var state = new ResultCardUiState();
        state.ToggleDetails();
        Assert.True(state.DetailsOpen);

        // The regression: RefreshResult() used to reset the flag on every scan.
        state.OnResultRefreshed();

        Assert.True(state.DetailsOpen);
    }

    [Fact]
    public void Result_refresh_resets_copied_label_but_not_details()
    {
        var state = new ResultCardUiState();
        state.ToggleDetails();
        state.MarkCopied();
        Assert.True(state.CopiedId);

        state.OnResultRefreshed();

        Assert.False(state.CopiedId);
        Assert.True(state.DetailsOpen);
    }

    [Fact]
    public void Item_selection_closes_details_even_when_user_opened_them()
    {
        var state = new ResultCardUiState();
        state.ToggleDetails();
        state.MarkCopied();

        state.OnItemSelected();

        Assert.False(state.DetailsOpen);
        Assert.False(state.CopiedId);
    }

    [Fact]
    public void Toggle_details_flips_open_and_closed()
    {
        var state = new ResultCardUiState();
        Assert.False(state.DetailsOpen);

        state.ToggleDetails();
        Assert.True(state.DetailsOpen);

        state.ToggleDetails();
        Assert.False(state.DetailsOpen);
    }

    [Fact]
    public void Closing_details_via_toggle_clears_copied_label()
    {
        var state = new ResultCardUiState();
        state.ToggleDetails();
        state.MarkCopied();
        Assert.True(state.CopiedId);

        state.ToggleDetails();

        Assert.False(state.CopiedId);
    }

    [Fact]
    public void Copy_result_flags_follow_success_and_failure()
    {
        var state = new ResultCardUiState();

        state.MarkCopied();
        Assert.True(state.CopiedId);

        state.MarkCopyFailed();
        Assert.False(state.CopiedId);
    }

    [Fact]
    public void Fresh_state_starts_collapsed()
    {
        var state = new ResultCardUiState();
        Assert.False(state.DetailsOpen);
        Assert.False(state.CopiedId);
    }
}
