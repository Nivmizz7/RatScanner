#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using RatScanner.Display;
using Xunit;

namespace RatScanner.Tests;

public sealed class GameDisplayConfigurationTests
{
    [Fact]
    public void Single_display_automatic_detection_uses_safe_non_blocking_fallback()
    {
        GameDisplayConfiguration configuration = Build([Display("primary", 0, 0, 2560, 1440, primary: true)]);

        Assert.Equal("primary", configuration.ActiveDisplay?.StableId);
        Assert.Equal(GameDisplaySelectionSource.PrimaryFallback, configuration.SelectionSource);
        Assert.Equal(GameDisplayStatusCode.PrimaryFallback, configuration.StatusCode);
        Assert.False(configuration.RequiresAttention);
        Assert.Equal(new Size(2560, 1440), configuration.GameViewport);
    }

    [Fact]
    public void Game_window_on_non_primary_display_wins_and_supplies_physical_capture_bounds()
    {
        GameDisplayInfo primary = Display("primary", 0, 0, 1920, 1080, primary: true);
        GameDisplayInfo second = Display("second", 1920, 0, 2560, 1440, scale: 1.5);
        Rectangle gameClient = new(2000, 100, 2200, 1200);

        GameDisplayConfiguration configuration = Build([primary, second], gameClient);

        Assert.Equal("second", configuration.ActiveDisplay?.StableId);
        Assert.Equal(GameDisplaySelectionSource.GameWindow, configuration.SelectionSource);
        Assert.Equal(new Size(2200, 1200), configuration.GameViewport);
        Assert.Equal(gameClient, configuration.CaptureBounds);
        Assert.Equal(1.5, configuration.DisplayScale);
    }

    [Fact]
    public void Saved_display_is_retained_when_game_is_not_running()
    {
        GameDisplayInfo primary = Display("primary", 0, 0, 1920, 1080, primary: true);
        GameDisplayInfo saved = Display("saved", -2560, 0, 2560, 1440, scale: 1.25);
        GameDisplayPreferences preferences = Preferences(saved);

        GameDisplayConfiguration configuration = Build([primary, saved], preferences: preferences);

        Assert.Equal("saved", configuration.ActiveDisplay?.StableId);
        Assert.Equal(GameDisplaySelectionSource.SavedStableId, configuration.SelectionSource);
        Assert.Equal(GameDisplayStatusCode.SavedDisplay, configuration.StatusCode);
        Assert.False(configuration.RequiresAttention);
    }

    [Fact]
    public void Missing_saved_display_uses_primary_and_requires_non_blocking_attention()
    {
        GameDisplayInfo primary = Display("primary", 0, 0, 1920, 1080, primary: true);
        GameDisplayPreferences missing = new(
            "disconnected",
            @"\\.\DISPLAY9",
            new Rectangle(1920, 0, 2560, 1440),
            false,
            1920,
            1080,
            false,
            1
        );

        GameDisplayConfiguration configuration = Build([primary], preferences: missing);

        Assert.Equal("primary", configuration.ActiveDisplay?.StableId);
        Assert.Equal(GameDisplayStatusCode.SavedDisplayUnavailable, configuration.StatusCode);
        Assert.True(configuration.RequiresAttention);
    }

    [Fact]
    public void Stable_identifier_survives_display_order_changes()
    {
        GameDisplayInfo first = Display("hardware-a", 0, 0, 2560, 1440, primary: true, number: 1);
        GameDisplayInfo second = Display("hardware-b", 2560, 0, 2560, 1440, number: 2);
        GameDisplayPreferences preferences = Preferences(second);

        GameDisplayConfiguration before = Build([first, second], preferences: preferences);
        GameDisplayConfiguration after = Build([second, first], preferences: preferences);

        Assert.Equal("hardware-b", before.ActiveDisplay?.StableId);
        Assert.Equal("hardware-b", after.ActiveDisplay?.StableId);
    }

    [Fact]
    public void Identical_resolutions_do_not_make_saved_display_ambiguous()
    {
        GameDisplayInfo left = Display("left-monitor", -2560, 0, 2560, 1440, number: 2);
        GameDisplayInfo right = Display("right-monitor", 0, 0, 2560, 1440, primary: true, number: 1);

        GameDisplayConfiguration configuration = Build([right, left], preferences: Preferences(left));

        Assert.Equal("left-monitor", configuration.ActiveDisplay?.StableId);
        Assert.Equal(left.PhysicalBounds, configuration.CaptureBounds);
    }

    [Fact]
    public void Different_per_monitor_dpi_values_follow_the_selected_display()
    {
        GameDisplayInfo primary = Display("primary", 0, 0, 3840, 2160, primary: true, scale: 1.5);
        GameDisplayInfo second = Display("second", 3840, 0, 1920, 1080, scale: 1);

        GameDisplayConfiguration configuration = Build([primary, second], preferences: Preferences(second));

        Assert.Equal(1, configuration.DisplayScale);
        Assert.Equal(new Size(1920, 1080), configuration.ActiveDisplay?.LogicalResolution);
    }

    [Fact]
    public void Automatic_viewport_tracks_a_changed_detected_resolution()
    {
        GameDisplayInfo display = Display("primary", 0, 0, 3840, 2160, primary: true);

        GameDisplayConfiguration first = Build([display], graphicsViewport: new Size(1920, 1080));
        GameDisplayConfiguration changed = Build([display], graphicsViewport: new Size(2560, 1440));

        Assert.Equal(new Size(1920, 1080), first.GameViewport);
        Assert.Equal(new Size(2560, 1440), changed.GameViewport);
    }

    [Fact]
    public void Disabled_custom_mode_ignores_stored_custom_values()
    {
        GameDisplayInfo display = Display("primary", 0, 0, 2560, 1440, primary: true);
        GameDisplayPreferences preferences = Preferences(display) with
        {
            UseCustomGameResolution = false,
            CustomGameWidth = 1280,
            CustomGameHeight = 720,
        };

        GameDisplayConfiguration configuration = Build(
            [display],
            graphicsViewport: new Size(2560, 1440),
            preferences: preferences
        );

        Assert.False(configuration.UsesCustomGameResolution);
        Assert.Equal(new Size(2560, 1440), configuration.GameViewport);
    }

    [Fact]
    public void Enabled_custom_mode_applies_valid_game_client_resolution_and_scale()
    {
        GameDisplayInfo display = Display("primary", 0, 0, 2560, 1440, primary: true, scale: 1.25);
        GameDisplayPreferences preferences = Preferences(display) with
        {
            UseCustomGameResolution = true,
            CustomGameWidth = 1600,
            CustomGameHeight = 900,
            UseCustomDisplayScale = true,
            CustomDisplayScale = 1.5,
        };

        GameDisplayConfiguration configuration = Build([display], preferences: preferences);

        Assert.True(configuration.UsesCustomGameResolution);
        Assert.True(configuration.UsesCustomDisplayScale);
        Assert.Equal(new Size(1600, 900), configuration.GameViewport);
        Assert.Equal(1.5, configuration.DisplayScale);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(-1920, 1080)]
    [InlineData(639, 360)]
    [InlineData(1920, 359)]
    [InlineData(20000, 1080)]
    [InlineData(4000, 500)]
    public void Invalid_custom_values_fall_back_to_automatic_and_require_correction(int width, int height)
    {
        GameDisplayInfo display = Display("primary", 0, 0, 1920, 1080, primary: true);
        GameDisplayPreferences preferences = Preferences(display) with
        {
            UseCustomGameResolution = true,
            CustomGameWidth = width,
            CustomGameHeight = height,
        };

        GameDisplayConfiguration configuration = Build([display], preferences: preferences);

        Assert.False(configuration.UsesCustomGameResolution);
        Assert.Equal(new Size(1920, 1080), configuration.GameViewport);
        Assert.Equal(GameDisplayStatusCode.InvalidCustomConfiguration, configuration.StatusCode);
        Assert.True(configuration.RequiresAttention);
    }

    [Fact]
    public void Returning_from_custom_to_automatic_restores_detected_values()
    {
        GameDisplayInfo display = Display("primary", 0, 0, 2560, 1440, primary: true);
        GameDisplayPreferences custom = Preferences(display) with
        {
            UseCustomGameResolution = true,
            CustomGameWidth = 1920,
            CustomGameHeight = 1080,
        };

        GameDisplayConfiguration customConfiguration = Build([display], preferences: custom);
        GameDisplayConfiguration automaticConfiguration = Build(
            [display],
            preferences: custom with
            {
                UseCustomGameResolution = false,
            }
        );

        Assert.Equal(new Size(1920, 1080), customConfiguration.GameViewport);
        Assert.Equal(new Size(2560, 1440), automaticConfiguration.GameViewport);
    }

    [Fact]
    public void Legacy_migration_preserves_only_values_that_differ_from_automatic_detection()
    {
        GameDisplayInfo display = Display("primary", 0, 0, 2560, 1440, primary: true, scale: 1.25);
        GameDisplayConfiguration automatic = Build([display]);

        GameDisplayPreferences unchanged = GameDisplayMigration.FromLegacy(2560, 1440, 1.25, automatic);
        GameDisplayPreferences customized = GameDisplayMigration.FromLegacy(1920, 1080, 1.5, automatic);

        Assert.False(unchanged.UseCustomGameResolution);
        Assert.False(unchanged.UseCustomDisplayScale);
        Assert.True(customized.UseCustomGameResolution);
        Assert.True(customized.UseCustomDisplayScale);
        Assert.Equal("primary", customized.PreferredStableId);
    }

    [Fact]
    public void Game_window_spanning_displays_uses_largest_overlap_and_warns()
    {
        GameDisplayInfo left = Display("left", 0, 0, 1920, 1080, primary: true);
        GameDisplayInfo right = Display("right", 1920, 0, 1920, 1080);
        Rectangle spanningWindow = new(1500, 100, 1800, 900);

        GameDisplayConfiguration configuration = Build([left, right], spanningWindow);

        Assert.Equal("right", configuration.ActiveDisplay?.StableId);
        Assert.Equal(GameDisplayStatusCode.GameWindowSpansDisplays, configuration.StatusCode);
        Assert.Equal(spanningWindow, configuration.CaptureBounds);
    }

    [Fact]
    public void Live_game_display_mismatch_uses_game_window_without_overwriting_saved_identity()
    {
        GameDisplayInfo saved = Display("saved", 0, 0, 1920, 1080, primary: true);
        GameDisplayInfo game = Display("game", 1920, 0, 2560, 1440, number: 2);
        GameDisplayPreferences preferences = Preferences(saved);

        GameDisplayConfiguration configuration = Build(
            [saved, game],
            new Rectangle(2000, 100, 2000, 1100),
            preferences: preferences
        );

        Assert.Equal("game", configuration.ActiveDisplay?.StableId);
        Assert.Equal(GameDisplayStatusCode.GameWindowOnDifferentDisplay, configuration.StatusCode);
        Assert.Equal("saved", preferences.PreferredStableId);
    }

    [Fact]
    public void Logical_and_physical_pixel_conversion_round_trips_at_per_monitor_scale()
    {
        Size logical = DisplayCoordinateConverter.PhysicalToLogical(new Size(3840, 2160), 1.5);
        Size physical = DisplayCoordinateConverter.LogicalToPhysical(logical, 1.5);

        Assert.Equal(new Size(2560, 1440), logical);
        Assert.Equal(new Size(3840, 2160), physical);
    }

    [Fact]
    public void Missing_dpi_is_a_required_warning_but_preserves_physical_coordinates()
    {
        GameDisplayInfo display = Display("primary", -1920, 200, 1920, 1080, primary: true, dpiReliable: false);

        GameDisplayConfiguration configuration = Build([display]);

        Assert.Equal(GameDisplayStatusCode.DpiUnavailable, configuration.StatusCode);
        Assert.True(configuration.RequiresAttention);
        Assert.Equal(display.PhysicalBounds, configuration.CaptureBounds);
    }

    [Fact]
    public void No_displays_is_actionable_without_inventing_capture_coordinates()
    {
        GameDisplayConfiguration configuration = Build([]);

        Assert.Null(configuration.ActiveDisplay);
        Assert.Equal(GameDisplayStatusCode.NoDisplays, configuration.StatusCode);
        Assert.True(configuration.RequiresAttention);
        Assert.Equal(Rectangle.Empty, configuration.CaptureBounds);
    }

    [Fact]
    public void Graphics_parser_reads_active_resolution_and_matches_stored_entries_by_index()
    {
        const string activeResolution = """
            {"DisplaySettings":{"Display":0,"Resolution":{"Width":2560,"Height":1440}},"Stored":[]}
            """;
        const string storedFallback = """
            {
              "DisplaySettings":{"Display":1},
              "Stored":[
                {"Index":2,"WindowResolution":{"Width":1280,"Height":720}},
                {"Index":1,"WindowResolution":{"Width":1600,"Height":900}}
              ]
            }
            """;

        Assert.True(GameGraphicsSettingsReader.TryParseViewport(activeResolution, out Size active));
        Assert.True(GameGraphicsSettingsReader.TryParseViewport(storedFallback, out Size stored));
        Assert.Equal(new Size(2560, 1440), active);
        Assert.Equal(new Size(1600, 900), stored);
    }

    [Fact]
    public void Modern_display_preferences_round_trip_through_config_persistence()
    {
        string path = Path.Combine(Path.GetTempPath(), $"RatScanner-display-{Guid.NewGuid():N}.cfg");
        GameDisplayPreferences expected = new(
            @"MONITOR\ACME123\INSTANCE",
            @"\\.\DISPLAY7",
            new Rectangle(-2560, 120, 2560, 1440),
            true,
            1920,
            1080,
            true,
            1.5
        );

        try
        {
            SimpleConfig config = new(path);
            GameDisplayPreferencesStore.Write(config, expected);

            Assert.True(GameDisplayPreferencesStore.TryRead(config, 800, 600, 1, out var actual));
            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static GameDisplayConfiguration Build(
        IReadOnlyList<GameDisplayInfo> displays,
        Rectangle? gameClientBounds = null,
        Size? graphicsViewport = null,
        GameDisplayPreferences? preferences = null
    ) =>
        GameDisplayConfigurationBuilder.Build(
            displays,
            gameClientBounds,
            graphicsViewport,
            preferences ?? GameDisplayPreferences.Automatic
        );

    private static GameDisplayInfo Display(
        string id,
        int x,
        int y,
        int width,
        int height,
        bool primary = false,
        double scale = 1,
        bool dpiReliable = true,
        int number = 1
    ) => new(id, $@"\\.\DISPLAY{number}", "", new Rectangle(x, y, width, height), primary, scale, dpiReliable, number);

    private static GameDisplayPreferences Preferences(GameDisplayInfo display) =>
        new(display.StableId, display.DeviceName, display.PhysicalBounds, false, 1920, 1080, false, 1);
}
