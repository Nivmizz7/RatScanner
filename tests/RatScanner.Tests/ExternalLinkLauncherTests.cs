using System;
using System.Collections.Generic;
using System.Diagnostics;
using Xunit;

namespace RatScanner.Tests;

public sealed class ExternalLinkLauncherTests
{
    [Theory]
    [InlineData("https://tarkov.dev/item/544fb45d4bdc2dee738b4568")]
    [InlineData("http://escapefromtarkov.fandom.com/wiki/Salewa")]
    [InlineData("HTTPS://TARKOVTRACKER.ORG")]
    [InlineData("https://github.com/tarkovtracker-org/RatScanner/issues/new?body=x&title=y")]
    public void Absolute_web_urls_are_accepted(string url)
    {
        Assert.True(ExternalLinkLauncher.IsSafeWebUrl(url));
    }

    [Theory]
    // Catalog links arrive as remote tarkov.dev data replayed from the on-disk cache. ShellExecute
    // resolves any of these to a program or protocol handler, so they must never reach the shell.
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\attacker-share\payload.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("javascript:alert(1)")]
    [InlineData("search-ms:query=x")]
    public void Non_web_schemes_are_rejected(string url)
    {
        Assert.False(ExternalLinkLauncher.IsSafeWebUrl(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // Relative and malformed values also reach the launcher, because wikiLink is nullable
    // free-form text on the tarkov.dev item payload.
    [InlineData("tarkov.dev/item/123")]
    [InlineData("/wiki/Salewa")]
    [InlineData("not a url")]
    public void Empty_relative_and_malformed_values_are_rejected(string url)
    {
        Assert.False(ExternalLinkLauncher.IsSafeWebUrl(url));
    }

    [Fact]
    public void Missing_link_is_rejected()
    {
        Assert.False(ExternalLinkLauncher.IsSafeWebUrl(null));
    }

    [Fact]
    public void Open_does_not_launch_rejected_targets()
    {
        bool launched = false;
        List<string> warnings = [];

        ExternalLinkLauncher.Open(
            @"C:\Windows\System32\cmd.exe",
            _ =>
            {
                launched = true;
                return null;
            },
            warnings.Add
        );

        Assert.False(launched);
        Assert.Single(warnings);
        Assert.DoesNotContain(@"C:\Windows", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Open_launches_accepted_targets_through_the_shell()
    {
        ProcessStartInfo captured = null;

        ExternalLinkLauncher.Open(
            "https://tarkov.dev/item/123?source=test#details",
            startInfo =>
            {
                captured = startInfo;
                return null;
            },
            _ => Assert.Fail("Accepted links should not log a warning.")
        );

        Assert.NotNull(captured);
        Assert.True(captured.UseShellExecute);
        Assert.Equal("https://tarkov.dev/item/123?source=test#details", captured.FileName);
    }

    [Fact]
    public void Open_logs_a_sanitized_target_when_launching_fails()
    {
        List<string> warnings = [];

        ExternalLinkLauncher.Open(
            "https://user:secret@example.com/item/123?token=sensitive#fragment",
            _ => throw new InvalidOperationException("failure contains sensitive details"),
            warnings.Add
        );

        string warning = Assert.Single(warnings);
        Assert.Contains("https://example.com/item/123", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("user", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("token", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("failure", warning, StringComparison.Ordinal);
    }
}
