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
            Assert.Equal(legacyKey, new SimpleConfig(configPath, "TarkovTracker").ReadSecureString("Token", ""));

            RatConfig.LoadConfig(configPath);

            Assert.Equal(legacyKey, RatConfig.Tracking.TarkovTracker.PvpToken);
            Assert.Equal("", RatConfig.Tracking.TarkovTracker.PveToken);
            Assert.Equal("", RatConfig.Tracking.TarkovTracker.SeasonalToken);
            Assert.Equal("", RatConfig.Tracking.TarkovTracker.IoToken);
            Assert.True(File.Exists(configPath + ".v2.bak"));

            RatConfig.LoadConfig(configPath);

            Assert.Equal(legacyKey, RatConfig.Tracking.TarkovTracker.PvpToken);
            Assert.Equal("", RatConfig.Tracking.TarkovTracker.PveToken);
            Assert.False(File.Exists(configPath + ".v3.bak"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Legacy_io_key_migrates_to_the_pvp_only_io_slot()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            const string legacyKey = "tt_legacy";
            SimpleConfig legacy = new(configPath, "TarkovTracker");
            legacy.WriteSecureString("Token", legacyKey);
            legacy.WriteInt("Backend", 0);
            legacy.Section = "Other";
            legacy.WriteInt("ConfigVersion", 2);

            RatConfig.LoadConfig(configPath);

            Assert.Equal("", RatConfig.Tracking.TarkovTracker.PvpToken);
            Assert.Equal("", RatConfig.Tracking.TarkovTracker.PveToken);
            Assert.Equal(legacyKey, RatConfig.Tracking.TarkovTracker.IoToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Current_config_does_not_resurrect_a_removed_io_key_from_legacy_fields()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            SimpleConfig config = new(configPath, "TarkovTracker");
            config.WriteSecureString("Token", "tt_stale");
            config.WriteInt("Backend", 0);
            config.WriteSecureString("PvpToken", "PVP_current");
            config.WriteSecureString("PveToken", "PVE_current");
            config.WriteSecureString("SeasonalToken", "SZN_current");
            config.WriteSecureString("IoToken", "");
            config.Section = "Other";
            config.WriteInt("ConfigVersion", 3);

            RatConfig.LoadConfig(configPath);

            Assert.Equal("PVP_current", RatConfig.Tracking.TarkovTracker.PvpToken);
            Assert.Equal("PVE_current", RatConfig.Tracking.TarkovTracker.PveToken);
            Assert.Equal("SZN_current", RatConfig.Tracking.TarkovTracker.SeasonalToken);
            Assert.Equal("", RatConfig.Tracking.TarkovTracker.IoToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Malformed_legacy_key_is_preserved_in_the_pvp_slot_for_user_validation()
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
    public void Config_without_pvp_source_defaults_to_org_when_org_pvp_token_present()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            SimpleConfig config = new(configPath, "TarkovTracker");
            config.WriteSecureString("PvpToken", "PVP_org");
            config.WriteSecureString("PveToken", "PVE_org");
            config.WriteSecureString("IoToken", "IO_legacy");
            config.Section = "Other";
            config.WriteInt("ConfigVersion", 3);

            RatConfig.LoadConfig(configPath);

            Assert.Equal(PvpSource.Org, RatConfig.Tracking.TarkovTracker.PvpSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Config_without_pvp_source_defaults_to_io_when_only_io_token_present()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            SimpleConfig config = new(configPath, "TarkovTracker");
            config.WriteSecureString("PvpToken", "");
            config.WriteSecureString("PveToken", "");
            config.WriteSecureString("IoToken", "IO_legacy");
            config.Section = "Other";
            config.WriteInt("ConfigVersion", 3);

            RatConfig.LoadConfig(configPath);

            Assert.Equal(PvpSource.Io, RatConfig.Tracking.TarkovTracker.PvpSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Config_without_pvp_source_defaults_to_org_when_no_pvp_token_present()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            SimpleConfig config = new(configPath, "TarkovTracker");
            config.WriteSecureString("PvpToken", "");
            config.WriteSecureString("PveToken", "PVE_org");
            config.WriteSecureString("IoToken", "");
            config.Section = "Other";
            config.WriteInt("ConfigVersion", 3);

            RatConfig.LoadConfig(configPath);

            Assert.Equal(PvpSource.Org, RatConfig.Tracking.TarkovTracker.PvpSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Stored_pvp_source_is_preserved_on_load()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            SimpleConfig config = new(configPath, "TarkovTracker");
            config.WriteSecureString("PvpToken", "PVP_org");
            config.WriteSecureString("PveToken", "PVE_org");
            config.WriteSecureString("IoToken", "IO_legacy");
            config.WriteInt("PvpSource", 1); // Io
            config.Section = "Other";
            config.WriteInt("ConfigVersion", 3);

            RatConfig.LoadConfig(configPath);

            // Stored value wins over the org-token-present default.
            Assert.Equal(PvpSource.Io, RatConfig.Tracking.TarkovTracker.PvpSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Out_of_range_pvp_source_falls_back_to_derived_default()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            SimpleConfig config = new(configPath, "TarkovTracker");
            config.WriteSecureString("PvpToken", "PVP_org");
            config.WriteSecureString("PveToken", "");
            config.WriteSecureString("IoToken", "");
            config.WriteInt("PvpSource", 42); // invalid
            config.Section = "Other";
            config.WriteInt("ConfigVersion", 3);

            RatConfig.LoadConfig(configPath);

            Assert.Equal(PvpSource.Org, RatConfig.Tracking.TarkovTracker.PvpSource);
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
            config.WriteInt("ConfigVersion", 3);

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
    public void Save_persists_pvp_source_and_reload_preserves_it()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string configPath = Path.Combine(root, "config.cfg");
            SimpleConfig config = new(configPath, "TarkovTracker");
            config.WriteSecureString("PvpToken", "PVP_org");
            config.WriteSecureString("PveToken", "PVE_org");
            config.WriteSecureString("IoToken", "IO_legacy");
            config.Section = "Other";
            config.WriteInt("ConfigVersion", 3);

            RatConfig.LoadConfig(configPath);
            Assert.Equal(PvpSource.Org, RatConfig.Tracking.TarkovTracker.PvpSource);

            RatConfig.Tracking.TarkovTracker.PvpSource = PvpSource.Io;
            RatConfig.SaveConfig(configPath);

            // Reset statics to defaults so the reload is meaningful.
            RatConfig.Tracking.TarkovTracker.PvpSource = PvpSource.Org;
            RatConfig.LoadConfig(configPath);
            Assert.Equal(PvpSource.Io, RatConfig.Tracking.TarkovTracker.PvpSource);
        }
        finally
        {
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
            config.WriteInt("ConfigVersion", 3);

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
