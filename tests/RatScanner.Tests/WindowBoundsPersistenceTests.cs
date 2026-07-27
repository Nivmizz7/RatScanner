using System.IO;
using System.Windows;
using Xunit;

namespace RatScanner.Tests;

[Collection(RatConfigCollection.Name)]
public sealed class WindowBoundsPersistenceTests
{
    [Fact]
    public void Window_bounds_round_trip_through_config()
    {
        string root = CreateTemporaryDirectory();
        string configPath = Path.Combine(root, "config.cfg");
        try
        {
            RatConfig.LastWindowPositionX = 120;
            RatConfig.LastWindowPositionY = 80;
            RatConfig.LastWindowWidth = 900;
            RatConfig.LastWindowHeight = 640;
            RatConfig.SaveConfig(configPath);

            RatConfig.LastWindowPositionX = int.MinValue;
            RatConfig.LastWindowPositionY = int.MinValue;
            RatConfig.LastWindowWidth = 0;
            RatConfig.LastWindowHeight = 0;
            RatConfig.LoadConfig(configPath);

            Assert.Equal(120, RatConfig.LastWindowPositionX);
            Assert.Equal(80, RatConfig.LastWindowPositionY);
            Assert.Equal(900, RatConfig.LastWindowWidth);
            Assert.Equal(640, RatConfig.LastWindowHeight);
        }
        finally
        {
            RatConfig.LastWindowPositionX = int.MinValue;
            RatConfig.LastWindowPositionY = int.MinValue;
            RatConfig.LastWindowWidth = 0;
            RatConfig.LastWindowHeight = 0;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_size_keys_default_to_unset()
    {
        // Config reads pass the current static as the default, so a config
        // without size keys must leave the unset (0) defaults untouched.
        string root = CreateTemporaryDirectory();
        string configPath = Path.Combine(root, "config.cfg");
        try
        {
            File.WriteAllText(configPath, "[Other]\r\nconfigversion=3\r\n");

            RatConfig.LastWindowWidth = 0;
            RatConfig.LastWindowHeight = 0;
            RatConfig.LoadConfig(configPath);

            Assert.Equal(0, RatConfig.LastWindowWidth);
            Assert.Equal(0, RatConfig.LastWindowHeight);
        }
        finally
        {
            RatConfig.LastWindowWidth = 0;
            RatConfig.LastWindowHeight = 0;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Off_screen_saved_position_is_rejected()
    {
        PageSwitcher.LogicalWorkingArea[] workingAreas = [new(0, 0, 1920, 1080)];

        Assert.False(PageSwitcher.IsVisibleOnAnyScreen(-100000, -100000, 1080, 720, workingAreas));
    }

    [Fact]
    public void On_screen_position_is_accepted()
    {
        PageSwitcher.LogicalWorkingArea[] workingAreas = [new(-1280, 0, 1920, 1080)];

        Assert.True(PageSwitcher.IsVisibleOnAnyScreen(-1100, 120, 900, 640, workingAreas));
    }

    [Fact]
    public void Physical_working_area_is_converted_to_wpf_logical_units()
    {
        PageSwitcher.LogicalWorkingArea area = PageSwitcher.PhysicalToLogicalWorkingArea(
            new System.Drawing.Rectangle(3840, 0, 2560, 1440),
            1.5
        );

        Assert.Equal(2560, area.Left);
        Assert.Equal(0, area.Top);
        Assert.Equal(4266.666666666667, area.Right, 10);
        Assert.Equal(960, area.Bottom);
    }

    [Fact]
    public void High_dpi_physical_extent_does_not_accept_off_screen_logical_position()
    {
        PageSwitcher.LogicalWorkingArea[] workingAreas =
        [
            PageSwitcher.PhysicalToLogicalWorkingArea(new System.Drawing.Rectangle(0, 0, 3840, 2160), 2),
        ];

        Assert.False(PageSwitcher.IsVisibleOnAnyScreen(1900, 100, 1080, 720, workingAreas));
    }

    [Fact]
    public void Off_screen_saved_position_rejects_saved_size_as_one_placement()
    {
        PageSwitcher.LogicalWorkingArea[] workingAreas = [new(0, 0, 1920, 1080)];

        bool restored = PageSwitcher.TryGetRestorableBounds(
            5000,
            100,
            3200,
            1800,
            PageSwitcher.DefaultWidth,
            PageSwitcher.DefaultHeight,
            workingAreas,
            out Rect bounds
        );

        Assert.False(restored);
        Assert.True(bounds.IsEmpty);
    }

    [Fact]
    public void Missing_saved_size_uses_default_size_with_valid_saved_position()
    {
        PageSwitcher.LogicalWorkingArea[] workingAreas = [new(0, 0, 1920, 1080)];

        bool restored = PageSwitcher.TryGetRestorableBounds(
            120,
            80,
            0,
            0,
            PageSwitcher.DefaultWidth,
            PageSwitcher.DefaultHeight,
            workingAreas,
            out Rect bounds
        );

        Assert.True(restored);
        Assert.Equal(new Rect(120, 80, PageSwitcher.DefaultWidth, PageSwitcher.DefaultHeight), bounds);
    }

    [Fact]
    public void Minimized_window_persists_restore_bounds()
    {
        Rect restoreBounds = new(120, 80, 900, 640);
        Rect iconicBounds = new(-32000, -32000, 160, 28);

        bool persisted = PageSwitcher.TryGetPersistableBounds(
            isMinimalUi: false,
            WindowState.Minimized,
            Rect.Empty,
            restoreBounds,
            iconicBounds,
            out Rect bounds
        );

        Assert.True(persisted);
        Assert.Equal(restoreBounds, bounds);
    }

    [Fact]
    public void Minimal_ui_without_restore_bounds_preserves_previous_normal_bounds()
    {
        bool persisted = PageSwitcher.TryGetPersistableBounds(
            isMinimalUi: true,
            WindowState.Normal,
            Rect.Empty,
            new Rect(120, 80, 900, 640),
            new Rect(1700, 20, 260, 90),
            out Rect bounds
        );

        Assert.False(persisted);
        Assert.True(bounds.IsEmpty);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "RatScannerTests", Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }
}
