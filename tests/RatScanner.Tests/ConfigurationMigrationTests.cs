using System;
using System.IO;
using RatScanner.TarkovDev;
using Xunit;

namespace RatScanner.Tests;

[Collection(RatConfigCollection.Name)]
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

    [Fact]
    public void Legacy_org_key_migrates_to_pvp_only_and_is_idempotent()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            const string legacyKey = "PVP_legacy";
            SimpleConfig legacy = new(configPath, "TarkovTracker");
            legacy.WriteSecureString("Token", legacyKey);
            legacy.WriteInt("Backend", 1);
            legacy.Section = "Other";
            legacy.WriteInt("ConfigVersion", 2);

            RatConfig.LoadConfig(configPath);

            Assert.Equal(legacyKey, RatConfig.Tracking.TarkovTracker.PvpToken);
            Assert.Equal("", RatConfig.Tracking.TarkovTracker.PveToken);
            Assert.Equal("", RatConfig.Tracking.TarkovTracker.SeasonalToken);
            Assert.True(File.Exists(configPath + ".v2.bak"));

            RatConfig.LoadConfig(configPath);

            Assert.Equal(legacyKey, RatConfig.Tracking.TarkovTracker.PvpToken);
            Assert.False(File.Exists(configPath + ".v4.bak"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Legacy_io_key_is_not_migrated_and_retired_fields_are_removed()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            SimpleConfig legacy = new(configPath, "TarkovTracker");
            legacy.WriteSecureString("Token", "tt_legacy");
            legacy.WriteInt("Backend", 0);
            legacy.Section = "Other";
            legacy.WriteInt("ConfigVersion", 2);

            RatConfig.LoadConfig(configPath);

            Assert.Equal("", RatConfig.Tracking.TarkovTracker.PvpToken);
            Assert.Equal("missing", new SimpleConfig(configPath, "TarkovTracker").ReadSecureString("Token", "missing"));
            Assert.Equal(42, new SimpleConfig(configPath, "TarkovTracker").ReadInt("Backend", 42));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Version_three_config_preserves_org_keys_and_scrubs_io_configuration()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            SimpleConfig config = new(configPath, "TarkovTracker");
            config.WriteSecureString("PvpToken", "PVP_current");
            config.WriteSecureString("PveToken", "PVE_current");
            config.WriteSecureString("SeasonalToken", "SZN_current");
            config.WriteSecureString("IoToken", "tt_retired");
            config.WriteInt("PvpSource", 1);
            config.Section = "Other";
            config.WriteInt("ConfigVersion", 3);

            RatConfig.LoadConfig(configPath);

            Assert.Equal("PVP_current", RatConfig.Tracking.TarkovTracker.PvpToken);
            Assert.Equal("PVE_current", RatConfig.Tracking.TarkovTracker.PveToken);
            Assert.Equal("SZN_current", RatConfig.Tracking.TarkovTracker.SeasonalToken);
            SimpleConfig saved = new(configPath, "TarkovTracker");
            Assert.Equal("missing", saved.ReadSecureString("IoToken", "missing"));
            Assert.Equal(42, saved.ReadInt("PvpSource", 42));
            Assert.True(File.Exists(configPath + ".v3.bak"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Malformed_legacy_org_key_is_preserved_for_user_validation()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            const string legacyKey = "not-a-valid-prefix";
            SimpleConfig legacy = new(configPath, "TarkovTracker");
            legacy.WriteSecureString("Token", legacyKey);
            legacy.WriteInt("Backend", 1);
            legacy.Section = "Other";
            legacy.WriteInt("ConfigVersion", 2);

            RatConfig.LoadConfig(configPath);

            Assert.Equal(legacyKey, RatConfig.Tracking.TarkovTracker.PvpToken);
            Assert.Equal("", RatConfig.Tracking.TarkovTracker.PveToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Negative_scan_cooldown_is_clamped_on_load()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            SimpleConfig config = new(configPath, "NameScan");
            config.WriteInt("CooldownMs", -1);
            config.Section = "Other";
            config.WriteInt("ConfigVersion", 4);

            RatConfig.LoadConfig(configPath);

            Assert.Equal(0, RatConfig.NameScan.CooldownMs);
        }
        finally
        {
            RatConfig.NameScan.CooldownMs = 300;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_and_reload_preserve_seasonal_mode_and_token()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            SimpleConfig config = new(configPath, "Other");
            config.WriteInt("ConfigVersion", 4);

            RatConfig.LoadConfig(configPath);
            RatConfig.GameMode = GameMode.Seasonal;
            RatConfig.Tracking.TarkovTracker.SeasonalToken = "SZN_current";
            RatConfig.SaveConfig(configPath);

            RatConfig.GameMode = GameMode.Regular;
            RatConfig.Tracking.TarkovTracker.SeasonalToken = "";
            RatConfig.LoadConfig(configPath);

            Assert.Equal(GameMode.Seasonal, RatConfig.GameMode);
            Assert.Equal("SZN_current", RatConfig.Tracking.TarkovTracker.SeasonalToken);
        }
        finally
        {
            RatConfig.GameMode = GameMode.Regular;
            RatConfig.Tracking.TarkovTracker.SeasonalToken = "";
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
