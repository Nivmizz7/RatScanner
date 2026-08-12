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
        Assert.Contains("QueueOverlayInitialization();", blazorUi, StringComparison.Ordinal);
        Assert.DoesNotContain("startup.create_overlay", blazorUi, StringComparison.Ordinal);
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
