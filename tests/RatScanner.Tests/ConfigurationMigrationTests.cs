using System;
using System.IO;
using Xunit;

namespace RatScanner.Tests;

public sealed class ConfigurationMigrationTests
{
    [Fact]
    public void Unsupported_config_is_preserved_before_migration()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            const string original =
                "[Other]\r\nconfigversion=1\r\n[Tracking.TarkovTracker]\r\ntoken=encrypted-value\r\n";
            File.WriteAllText(configPath, original);

            RatConfig.ConfigLoadPlan plan = RatConfig.PrepareConfigForLoad(configPath);

            Assert.True(plan.FileExists);
            Assert.False(plan.IsSupported);
            Assert.True(plan.ShouldSave);
            Assert.Equal(1, plan.ExistingVersion);
            Assert.NotNull(plan.BackupPath);
            Assert.Equal(original, File.ReadAllText(configPath));
            Assert.Equal(original, File.ReadAllText(plan.BackupPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Repeated_migration_never_overwrites_an_existing_backup()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            File.WriteAllText(configPath, "[Other]\r\nconfigversion=1\r\n");

            RatConfig.ConfigLoadPlan first = RatConfig.PrepareConfigForLoad(configPath);
            File.WriteAllText(configPath, "[Other]\r\nconfigversion=0\r\n");
            RatConfig.ConfigLoadPlan second = RatConfig.PrepareConfigForLoad(configPath);

            Assert.NotEqual(first.BackupPath, second.BackupPath);
            Assert.Equal("[Other]\r\nconfigversion=1\r\n", File.ReadAllText(first.BackupPath!));
            Assert.Equal("[Other]\r\nconfigversion=0\r\n", File.ReadAllText(second.BackupPath!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_config_requests_initial_save_without_creating_a_backup()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");

            RatConfig.ConfigLoadPlan plan = RatConfig.PrepareConfigForLoad(configPath);

            Assert.False(plan.FileExists);
            Assert.True(plan.IsSupported);
            Assert.True(plan.ShouldSave);
            Assert.Null(plan.BackupPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Migrating_the_same_unsupported_version_twice_creates_a_suffixed_backup()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            File.WriteAllText(configPath, "[Other]\r\nconfigversion=1\r\n");

            RatConfig.ConfigLoadPlan first = RatConfig.PrepareConfigForLoad(configPath);

            // Recreate the unsupported file so the same version is backed up again and the
            // collision suffix path (config.cfg.v1.bak.1) is exercised.
            File.WriteAllText(configPath, "[Other]\r\nconfigversion=1\r\n");
            RatConfig.ConfigLoadPlan second = RatConfig.PrepareConfigForLoad(configPath);

            Assert.NotNull(first.BackupPath);
            Assert.NotNull(second.BackupPath);
            Assert.NotEqual(first.BackupPath, second.BackupPath);
            Assert.True(File.Exists(first.BackupPath));
            Assert.True(File.Exists(second.BackupPath));
            Assert.EndsWith(".bak.1", second.BackupPath, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "RatScanner.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
