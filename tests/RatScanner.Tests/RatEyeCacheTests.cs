using System;
using System.Drawing;
using System.IO;
using OpenCvSharp;
using RatEye;
using RatStash;
using Xunit;

namespace RatScanner.Tests;

public class RatEyeCacheTests
{
    [Fact]
    public void Corrupt_cached_icon_is_regenerated_atomically()
    {
        string root = Path.Combine(Path.GetTempPath(), "RatEye-cache-test-" + Guid.NewGuid().ToString("N"));
        string iconsDirectory = Path.Combine(root, "icons");
        string cacheDirectory = Path.Combine(root, "cache");
        Directory.CreateDirectory(iconsDirectory);
        Directory.CreateDirectory(cacheDirectory);

        Config config = CreateConfig(iconsDirectory);
        try
        {
            WriteIcon(Path.Combine(iconsDirectory, "one.png"));

            using (IconManager manager = new(config, cacheDirectory))
            {
                manager.EnsureStaticIconsLoaded(new Vector2(1, 1));
                Assert.Single(manager.StaticIcons[new Vector2(1, 1)]);
            }

            string cachePath = Assert.Single(Directory.GetFiles(cacheDirectory, "*.bmp"));
            File.WriteAllText(cachePath, "not a bitmap");

            using (IconManager manager = new(config, cacheDirectory))
            {
                manager.EnsureStaticIconsLoaded(new Vector2(1, 1));
                Assert.Single(manager.StaticIcons[new Vector2(1, 1)]);
            }

            using Mat cachedIcon = Cv2.ImRead(cachePath, ImreadModes.Unchanged);
            Assert.False(cachedIcon.Empty());
            Assert.Equal(MatType.CV_8UC3, cachedIcon.Type());
            Assert.Empty(Directory.GetFiles(cacheDirectory, "*.tmp.bmp"));
        }
        finally
        {
            config.ProcessingConfig.InspectionConfig.Marker.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cache_pruning_removes_stale_and_oldest_oversize_entries()
    {
        string root = Path.Combine(Path.GetTempPath(), "RatEye-cache-prune-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            DateTime now = new(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);
            string stale = WriteCacheFile(root, "stale.bmp", 20, now - TimeSpan.FromDays(31));
            string abandonedTemporary = WriteCacheFile(root, "abandoned.tmp.bmp", 20, now - TimeSpan.FromDays(2));
            string oldest = WriteCacheFile(root, "oldest.bmp", 40, now - TimeSpan.FromHours(2));
            string newest = WriteCacheFile(root, "newest.bmp", 40, now - TimeSpan.FromHours(1));
            string unrelated = WriteCacheFile(root, "keep.txt", 20, now - TimeSpan.FromDays(90));

            IconManager.PruneCache(root, now, TimeSpan.FromDays(30), maxBytes: 60, maxFiles: 10);

            Assert.False(File.Exists(stale));
            Assert.False(File.Exists(abandonedTemporary));
            Assert.False(File.Exists(oldest));
            Assert.True(File.Exists(newest));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Config CreateConfig(string iconsDirectory)
    {
        Config config = new()
        {
            PathConfig = new Config.Path { StaticIcons = iconsDirectory },
            ProcessingConfig = new Config.Processing
            {
                UseCache = true,
                IconConfig = new Config.Processing.Icon { UseStaticIcons = true },
            },
        };
        config.RatStashDB = Database.FromItems(
            new Item[]
            {
                new()
                {
                    Id = "one",
                    Name = "One",
                    ShortName = "One",
                    Width = 1,
                    Height = 1,
                },
            }
        );
        return config;
    }

    private static void WriteIcon(string path)
    {
        using Bitmap bitmap = new(64, 64);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static string WriteCacheFile(string root, string name, int length, DateTime lastWriteTimeUtc)
    {
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[length]);
        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
        return path;
    }
}
