#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using RatEye;
using Xunit;
using TarkovItem = RatScanner.TarkovDev.Item;

namespace RatScanner.Tests;

/// <summary>
/// Optional local scan verification against real game screenshots in the repo root.
/// Mirrors RatScannerMain.NameScan / IconScan with the same config, crop math, and
/// offline tarkov.dev item database. The captures remain untracked local artifacts.
/// </summary>
public class ScanPipelineImageHarnessTests
{
    private readonly ITestOutputHelper _output;

    public ScanPipelineImageHarnessTests(ITestOutputHelper output) => _output = output;

    private const int ScreenWidth = 2560;
    private const int ScreenHeight = 1440;

    /// <summary>
    /// Loads a repo-root reference screenshot. Missing local captures are reported as
    /// skipped tests rather than silently passing without exercising any assertions.
    /// </summary>
    private static Bitmap LoadScreenshot(string fileName)
    {
        string path = Path.Combine(RepoRoot, fileName);
        Assert.SkipUnless(File.Exists(path), $"Local scan fixture not found: {fileName}");
        return new Bitmap(path);
    }

    private static string RepoRoot
    {
        get
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RatScanner.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }

    private static RatEyeEngine CreateEngine(bool highlighted = true)
    {
        string data = Path.Combine(AppContext.BaseDirectory, "Data");
        Config config = new()
        {
            PathConfig = new Config.Path
            {
                TrainedData = Path.Combine(data, "traineddata"),
                StaticIcons = Path.Combine(data, "icons"),
            },
            ProcessingConfig = new Config.Processing
            {
                UseCache = false,
                Scale = Config.Processing.Resolution2Scale(ScreenWidth, ScreenHeight),
                Language = RatStash.Language.English,
                IconConfig = new Config.Processing.Icon
                {
                    UseStaticIcons = true,
                    ScanMode = Config.Processing.Icon.ScanModes.TemplateMatching,
                    ScanRotatedIcons = true,
                },
                InventoryConfig = new Config.Processing.Inventory { OptimizeHighlighted = highlighted },
            },
        };
        return new RatEyeEngine(config, LoadRealItemDatabase());
    }

    private static RatStash.Database LoadRealItemDatabase()
    {
        Assert.SkipUnless(
            TarkovDevAPI.TryInitializeCacheFromOffline(),
            "Offline tarkov.dev cache not found; run the app once to populate it."
        );
        Assert.SkipUnless(TarkovDevAPI.TryGetCachedItems(out TarkovItem[] items), "Cached items are unavailable.");
        Assert.SkipUnless(items.Length > 0, "The offline tarkov.dev item cache is empty.");
        return RatStash.Database.FromItems(items.Select(RatScannerMain.ToRatStashItem).ToList());
    }

    /// <summary>Crop mirroring RatScannerMain.NameScan's screenshot geometry around a click.</summary>
    private static Bitmap NameScanCrop(Bitmap source, int clickX, int clickY, float scale)
    {
        int markerScanSize = (int)(50 * scale);
        int textWidth = (int)(600 * scale);
        int x = clickX - markerScanSize / 2;
        int y = clickY - markerScanSize / 2;
        Rectangle rect = new(x, y, markerScanSize + textWidth, markerScanSize);
        rect.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        return source.Clone(rect, source.PixelFormat);
    }

    [Fact]
    public void NameScan_reads_F1_grenade_title_from_inspect_window()
    {
        using RatEyeEngine engine = CreateEngine();
        float scale = engine.Config.ProcessingConfig.Scale;
        using Bitmap screenshot = LoadScreenshot("image copy.png");

        // Click position: the magnifier marker left of the inspect-window title.
        using Bitmap crop = NameScanCrop(screenshot, 768, 355, scale);
        RatEye.Processing.Inspection inspection = engine.NewInspection(crop);

        _output.WriteLine($"ContainsMarker={inspection.ContainsMarker}");
        _output.WriteLine($"MarkerConfidence={inspection.MarkerConfidence}");
        _output.WriteLine($"Item={inspection.Item?.Name ?? "<null>"}");
        _output.WriteLine($"ItemConfidence={inspection.ItemConfidence}");

        Assert.True(inspection.ContainsMarker, "Inspect-window marker not detected");
        Assert.NotNull(inspection.Item);
        Assert.Equal("F-1 hand grenade", inspection.Item.Name);
        // 0.85 is RatConfig.NameScan.ConfWarnThreshold — the app's own acceptance bar.
        Assert.True(inspection.ItemConfidence >= 0.85f, $"Low title confidence: {inspection.ItemConfidence}");
    }

    [Fact]
    public void NameScanScreen_finds_F1_grenade_on_full_screenshot()
    {
        using RatEyeEngine engine = CreateEngine();
        using Bitmap screenshot = LoadScreenshot("image.png");

        RatEye.Processing.MultiInspection multiInspection = engine.NewMultiInspection(screenshot);

        foreach (RatEye.Processing.Inspection inspection in multiInspection.Inspections)
            _output.WriteLine($"Found: {inspection.Item?.Name ?? "<null>"} conf={inspection.ItemConfidence}");

        Assert.Contains(multiInspection.Inspections, inspection => inspection.Item?.Name == "F-1 hand grenade");
    }

    [Fact]
    public void Hovered_item_highlight_is_located_after_eft_hover_color_change()
    {
        // EFT 1.0.6 brightened the hover highlight (value ~109-112); guards the widened
        // MaxHighlightingColor range. The gear-preview pane renders items larger than
        // standard slots, so only region location (not item identity) is asserted here.
        using RatEyeEngine engine = CreateEngine(highlighted: true);
        float scale = engine.Config.ProcessingConfig.Scale;
        using Bitmap screenshot = LoadScreenshot("image copy 2.png");

        // Cursor hovering the 6Sh118 raid backpack (tooltip anchor top-left).
        int cursorX = 2290,
            cursorY = 565;
        int scanWidth = (int)(scale * 896);
        int scanHeight = (int)(scale * 896);
        // Mirror GetScreenshot: the capture stays cursor-centered; off-screen area is blank.
        Rectangle rect = new(cursorX - scanWidth / 2, cursorY - scanHeight / 2, scanWidth, scanHeight);
        Rectangle visible = Rectangle.Intersect(rect, new Rectangle(0, 0, screenshot.Width, screenshot.Height));
        using Bitmap crop = new(scanWidth, scanHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(crop))
        {
            graphics.Clear(Color.Black);
            graphics.DrawImage(
                screenshot,
                new Rectangle(visible.X - rect.X, visible.Y - rect.Y, visible.Width, visible.Height),
                visible,
                GraphicsUnit.Pixel
            );
        }

        using RatEye.Processing.Inventory inventory = engine.NewInventory(crop);
        RatEye.Processing.Icon? icon = inventory.LocateIcon();

        Assert.NotNull(icon);
        _output.WriteLine($"Highlight located at {icon.Position} size {icon.Size}");
    }

    [Fact]
    public void IconScan_identifies_wd40_cell_in_junk_container()
    {
        using RatEyeEngine engine = CreateEngine(highlighted: true);
        using Bitmap screenshot = LoadScreenshot("image copy 3.png");

        // WD-40 (100ml) 1x1 cell in the top row of the left junk container, padded
        // the way LocateIconHighlighted pads a located highlight region.
        Rectangle cell = new(409, 119, 104, 104);
        using Bitmap crop = screenshot.Clone(cell, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using RatEye.Processing.Icon icon = new(
            crop,
            Vector2.Zero,
            new Vector2(cell.Width, cell.Height),
            engine.Config
        );

        _output.WriteLine($"Item={icon.Item?.Name ?? "<null>"} conf={icon.DetectionConfidence}");
        Assert.NotNull(icon.Item);
        Assert.Equal("WD-40 (100ml)", icon.Item.Name);
        Assert.True(
            icon.DetectionConfidence > RatConfig.IconScan.MinAcceptConfidence,
            $"Genuine grid match fell below the acceptance floor: {icon.DetectionConfidence}"
        );
    }

    [Fact]
    public void Gear_slot_garbage_matches_stay_below_the_acceptance_floor()
    {
        // Equipment-slot panels render items scaled to fit fixed boxes; template
        // matching then returns arbitrary same-size items (a T30 backpack matched
        // "6B23-2 ballistic plate" at ~0.49). MinAcceptConfidence must reject these.
        using RatEyeEngine engine = CreateEngine(highlighted: true);
        using Bitmap screenshot = LoadScreenshot("image copy 4.png");

        // T30 backpack equipment slot on the gear screen, padded like a located highlight.
        Rectangle cell = new(864, 858, 195, 213);
        using Bitmap crop = screenshot.Clone(cell, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using RatEye.Processing.Icon icon = new(
            crop,
            Vector2.Zero,
            new Vector2(cell.Width, cell.Height),
            engine.Config
        );

        _output.WriteLine($"Item={icon.Item?.Name ?? "<null>"} conf={icon.DetectionConfidence}");
        Assert.True(
            icon.Item == null || icon.DetectionConfidence < RatConfig.IconScan.MinAcceptConfidence,
            $"Gear-slot garbage match {icon.Item?.Name} at {icon.DetectionConfidence} "
                + "would be accepted; raise MinAcceptConfidence or fix candidate selection."
        );
    }
}
