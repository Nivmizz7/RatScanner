#nullable enable

using System;
using System.IO;
using Xunit;

namespace RatScanner.Tests;

public sealed class SettingsLayoutContractTests
{
    [Fact]
    public void Settings_layout_has_no_global_save_bar_and_uses_responsive_navigation()
    {
        string root = FindRepositoryRoot();
        string layout = File.ReadAllText(Path.Combine(root, "src", "App", "Shared", "SettingsLayout.razor"));
        string css = File.ReadAllText(Path.Combine(root, "src", "App", "Shared", "SettingsLayout.razor.css"));
        string settingsDirectory = Path.Combine(root, "src", "App", "Pages", "App", "Settings");

        Assert.DoesNotContain("SettingsSave", layout, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(settingsDirectory, "SettingsSave.razor")));
        Assert.Contains("settings-tabs", layout, StringComparison.Ordinal);
        Assert.Contains("settings-select", layout, StringComparison.Ordinal);
        Assert.Contains("@container", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns", css, StringComparison.Ordinal);
        Assert.Contains("min-width: 0", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_credential_save_reports_untested_instead_of_connected()
    {
        string root = FindRepositoryRoot();
        string tracking = File.ReadAllText(
            Path.Combine(root, "src", "App", "Pages", "App", "Settings", "SettingsTracking.razor")
        );

        // The candidate never reaches storage on this path, so nothing verified the
        // credential that remains persisted. Claiming Connected would contradict the
        // Untested state used everywhere else IsModeConfigured gates the status.
        int failure = tracking.IndexOf("CredentialSaveFailed", StringComparison.Ordinal);
        Assert.True(failure >= 0, "Expected the credential save failure branch to exist.");
        int end = tracking.IndexOf("return;", failure, StringComparison.Ordinal);
        Assert.True(end > failure, "Expected the failure branch to return early.");

        string branch = tracking[failure..end];
        Assert.Contains("TrackerConnectionState.Untested", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("TrackerConnectionState.Connected", branch, StringComparison.Ordinal);
    }

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
