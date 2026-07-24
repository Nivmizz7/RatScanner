using System.IO;
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
        // A position far outside any possible monitor arrangement must not be
        // considered visible (monitor unplugged since last run).
        Assert.False(PageSwitcher.IsVisibleOnAnyScreen(-100000, -100000, 1080, 720));
    }

    [Fact]
    public void On_screen_position_is_accepted()
    {
        // Center of the primary working area must be restorable on any machine
        // that can run the test suite (Windows desktop session).
        System.Drawing.Rectangle area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        double left = area.Left + (area.Width - 1080) / 2.0;
        double top = area.Top + (area.Height - 720) / 2.0;

        Assert.True(PageSwitcher.IsVisibleOnAnyScreen(left, top, 1080, 720));
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "RatScannerTests", Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }
}
