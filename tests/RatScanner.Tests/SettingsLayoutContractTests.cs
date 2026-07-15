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
