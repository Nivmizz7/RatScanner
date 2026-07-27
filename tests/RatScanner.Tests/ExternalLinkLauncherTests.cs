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
    public void Open_ignores_rejected_targets_instead_of_throwing()
    {
        // The Blazor click handlers call this synchronously; a throw here would surface as an
        // unhandled exception in the WebView render loop rather than a logged no-op.
        ExternalLinkLauncher.Open(null);
        ExternalLinkLauncher.Open(@"C:\Windows\System32\cmd.exe");
        ExternalLinkLauncher.Open("not a url");
    }
}
