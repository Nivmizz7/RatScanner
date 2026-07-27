#nullable enable

using System;
using System.IO;
using Xunit;

namespace RatScanner.Tests;

public sealed class TrackingSourceCardContractTests
{
    [Fact]
    public void Source_cards_are_clickable_without_bubbling_radio_clicks()
    {
        string root = FindRepositoryRoot();
        string settings = ReadSource(root, "src", "App", "Pages", "App", "Settings", "SettingsTracking.razor");
        string dialog = ReadSource(root, "src", "App", "Components", "ChangeConnectionDialog.razor");

        AssertSourceCards(settings, "OnPvpDraftSourceChanged", "Disabled=\"@IsPvpSourceTesting\"");
        AssertSourceCards(dialog, "OnDraftSourceChanged", "Disabled=\"_testing\"");
    }

    [Fact]
    public void Replacement_submission_uses_one_provider_snapshot()
    {
        string root = FindRepositoryRoot();
        string dialog = ReadSource(root, "src", "App", "Components", "ChangeConnectionDialog.razor");
        string submit = Slice(dialog, "private async Task SubmitAsync()", "private string ValidationMessage");
        string sourceHandler = Slice(
            dialog,
            "private void OnDraftSourceChanged",
            "private async Task HandleKeyDownAsync"
        );

        Assert.Contains("bool submitToIo = IsIoDraft;", submit, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(submit, "IsIoDraft"));
        Assert.Equal(3, CountOccurrences(submit, "submitToIo"));
        Assert.Contains("if (_testing)", sourceHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_source_selection_is_frozen_during_connection_tests()
    {
        string root = FindRepositoryRoot();
        string settings = ReadSource(root, "src", "App", "Pages", "App", "Settings", "SettingsTracking.razor");
        string sourceHandler = Slice(
            settings,
            "private void OnPvpDraftSourceChanged",
            "private async Task HandleOrgKeyDownAsync"
        );

        Assert.Contains("if (IsPvpSourceTesting)", sourceHandler, StringComparison.Ordinal);
        Assert.Contains(
            "private bool IsPvpSourceTesting => _ioTesting || _orgTesting[GameMode.Regular];",
            settings,
            StringComparison.Ordinal
        );
    }

    private static void AssertSourceCards(string source, string handler, string disabledAttribute)
    {
        string cards = Slice(source, "<MudRadioGroup", "</MudRadioGroup>");

        Assert.Contains($"@onclick=\"() => {handler}(PvpSource.Org)\"", cards, StringComparison.Ordinal);
        Assert.Contains($"@onclick=\"() => {handler}(PvpSource.Io)\"", cards, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(cards, "@onclick:stopPropagation=\"true\""));
        Assert.Equal(2, CountOccurrences(cards, disabledAttribute));
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static string ReadSource(string root, params string[] segments) =>
        File.ReadAllText(Path.Combine([root, .. segments]));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RatScanner.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
