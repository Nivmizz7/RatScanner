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
    public void Scanner_enable_toggles_re_register_hotkeys_after_immediate_save()
    {
        // ActiveHotkey copies Enable by value at construction. Immediate save must rebuild
        // hotkeys so name/icon enable flags take effect without a full settings page save.
        string root = FindRepositoryRoot();
        string settingsVm = File.ReadAllText(Path.Combine(root, "src", "App", "ViewModel", "SettingsVM.cs"));

        Assert.True(
            ContainsApplyRuntimeRegisterHotkeys(settingsVm, "SetEnableNameScanAsync"),
            "SetEnableNameScanAsync must call HotkeyManager.RegisterHotkeys via applyRuntime."
        );
        Assert.True(
            ContainsApplyRuntimeRegisterHotkeys(settingsVm, "SetEnableIconScanAsync"),
            "SetEnableIconScanAsync must call HotkeyManager.RegisterHotkeys via applyRuntime."
        );
        Assert.True(
            ContainsApplyRuntimeRegisterHotkeys(settingsVm, "SetOverlayEnabledAsync"),
            "SetOverlayEnabledAsync must keep calling HotkeyManager.RegisterHotkeys via applyRuntime."
        );
    }

    private static bool ContainsApplyRuntimeRegisterHotkeys(string source, string methodName)
    {
        int methodStart = source.IndexOf($"internal Task<SettingSaveResult> {methodName}", StringComparison.Ordinal);
        if (methodStart < 0)
            return false;

        int nextMethod = source.IndexOf("internal Task<SettingSaveResult>", methodStart + 1, StringComparison.Ordinal);
        string methodBody = nextMethod < 0 ? source[methodStart..] : source[methodStart..nextMethod];
        return methodBody.Contains("HotkeyManager.RegisterHotkeys()", StringComparison.Ordinal);
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
