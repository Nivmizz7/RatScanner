#nullable enable

using System;
using System.IO;
using System.Xml.Linq;
using RatScanner.View;
using Xunit;

namespace RatScanner.Tests;

public sealed class WebViewHostingContractTests
{
    [Fact]
    public void Main_window_retains_transparency_required_by_minimal_ui()
    {
        string root = FindRepositoryRoot();
        XDocument pageSwitcher = XDocument.Load(Path.Combine(root, "src", "App", "PageSwitcher.xaml"));
        XElement window = Assert.IsType<XElement>(pageSwitcher.Root);

        Assert.Equal("True", window.Attribute("AllowsTransparency")?.Value);
        Assert.Equal("None", window.Attribute("WindowStyle")?.Value);
    }

    [Fact]
    public void Passive_overlay_initialization_is_deferred_until_application_idle()
    {
        string root = FindRepositoryRoot();
        string blazorUi = File.ReadAllText(Path.Combine(root, "src", "App", "View", "BlazorUI.xaml.cs"));

        Assert.Contains("DispatcherPriority.ApplicationIdle", blazorUi, StringComparison.Ordinal);
        Assert.Contains("BlazorUI_Loaded", blazorUi, StringComparison.Ordinal);
        Assert.Contains("public void OnOpen()", blazorUi, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(blazorUi, "QueueOverlayInitialization();") >= 2,
            "Overlay initialization must be queued from Loaded and OnOpen so startup minimal mode is covered."
        );
        Assert.DoesNotContain("startup.create_overlay", blazorUi, StringComparison.Ordinal);
    }

    [Fact]
    public void Hotkey_rebuilds_are_serialized_and_tracker_failures_do_not_gate_scanning()
    {
        string root = FindRepositoryRoot();
        string hotkeyManager = StripComments(File.ReadAllText(Path.Combine(root, "src", "App", "HotkeyManager.cs")));
        string ratScannerMain = StripComments(File.ReadAllText(Path.Combine(root, "src", "App", "RatScannerMain.cs")));

        Assert.Contains("lock (_registrationLock)", hotkeyManager, StringComparison.Ordinal);
        Assert.Contains("RegisterHotkeysLocked();", hotkeyManager, StringComparison.Ordinal);
        Assert.Contains("UnregisterHotkeysLocked();", hotkeyManager, StringComparison.Ordinal);
        Assert.Contains("if (!_engineReady)", hotkeyManager, StringComparison.Ordinal);
        string runtimeInitialization = ExtractMethodDefinition(ratScannerMain, "InitializeRuntimeAsync");
        int timerSetup = runtimeInitialization.IndexOf("new Timer(", StringComparison.Ordinal);
        int trackerActivation = runtimeInitialization.IndexOf("ActivateTrackerModeAsync(", StringComparison.Ordinal);
        int finallyStart = runtimeInitialization.IndexOf("finally", StringComparison.Ordinal);
        int readinessPublication = runtimeInitialization.IndexOf("UpdateHotkeyReadiness();", StringComparison.Ordinal);
        Assert.True(timerSetup >= 0 && timerSetup < trackerActivation, "Tracker timer setup must precede activation.");
        Assert.True(
            finallyStart >= 0 && readinessPublication > finallyStart,
            "Runtime readiness must be published from the finally block."
        );
    }

    [Fact]
    public void Passive_overlay_styles_remain_click_through_and_nonactivating()
    {
        Assert.Equal((nint)0x080800A0, OverlayNativeMethods.PassiveClickThroughStyles);
    }

    [Fact]
    public void Passive_overlay_bootstraps_small_and_not_topmost()
    {
        string root = FindRepositoryRoot();
        XDocument overlay = XDocument.Load(Path.Combine(root, "src", "App", "View", "BlazorOverlay.xaml"));
        XElement window = Assert.IsType<XElement>(overlay.Root);

        Assert.Equal("False", window.Attribute("Topmost")?.Value);
        Assert.Equal("100", window.Attribute("Width")?.Value);
        Assert.Equal("100", window.Attribute("Height")?.Value);
    }

    [Fact]
    public void Frame_refresh_preserves_geometry_z_order_and_activation()
    {
        Assert.Equal((uint)0x0037, OverlayNativeMethods.SwpFrameChangedFlags);
    }

    [Fact]
    public void Full_screen_interactable_search_overlay_is_not_shipped()
    {
        string root = FindRepositoryRoot();
        string appRoot = Path.Combine(root, "src", "App");
        string interactablePages = Path.Combine(appRoot, "Pages", "InteractableOverlay");

        Assert.False(File.Exists(Path.Combine(appRoot, "View", "BlazorInteractableOverlay.xaml")));
        Assert.False(File.Exists(Path.Combine(appRoot, "wwwroot", "interactableOverlay.html")));
        if (Directory.Exists(interactablePages))
            Assert.Empty(Directory.GetFiles(interactablePages, "*", SearchOption.AllDirectories));
        Assert.DoesNotContain("/showOverlay", File.ReadAllText(Path.Combine(appRoot, "App.xaml.cs")));
        Assert.DoesNotContain(
            "BlazorInteractableOverlay",
            File.ReadAllText(Path.Combine(appRoot, "View", "BlazorUI.xaml.cs"))
        );
    }

    private static string StripComments(string source) =>
        System.Text.RegularExpressions.Regex.Replace(
            source,
            @"//[^\r\n]*|/\*.*?\*/",
            "",
            System.Text.RegularExpressions.RegexOptions.Singleline
        );

    private static string ExtractMethodDefinition(string source, string methodName)
    {
        int methodStart = source.IndexOf($"private async Task {methodName}(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Could not find the definition of {methodName}.");

        int bodyStart = source.IndexOf('{', methodStart);
        Assert.True(bodyStart >= 0, $"Could not find the body for {methodName}.");
        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"Could not find the end of {methodName}.");
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
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
