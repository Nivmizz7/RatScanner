using System.IO;
using RatScanner.Presentation;
using Xunit;

namespace RatScanner.Tests;

/// <summary>
/// The scanned item's icon must come from disk whenever possible. A remote catalog
/// link costs a network round trip, and because Blazor patches the <c>src</c> of a
/// reused <c>&lt;img&gt;</c>, the previous item's image stays on screen for the
/// duration — which presents as the correct name and price beside the wrong icon.
/// </summary>
public sealed class ItemIconResolverTests
{
    [Fact]
    public void Engine_icon_path_is_mapped_to_the_local_host()
    {
        bool mapped = ItemIconResolver.TryMapEngineIconPath(
            @"C:\Games\RatScanner\Data\icons\5449016a4bdc2d6f028b456f.png",
            out string url
        );

        Assert.True(mapped);
        Assert.Equal("https://local.data/icons/5449016a4bdc2d6f028b456f.png", url);
    }

    [Fact]
    public void Forward_slash_paths_are_mapped_too()
    {
        Assert.True(ItemIconResolver.TryMapEngineIconPath("Data/icons/abc.png", out string url));
        Assert.Equal("https://local.data/icons/abc.png", url);
    }

    [Fact]
    public void Icon_directory_match_is_case_insensitive()
    {
        Assert.True(ItemIconResolver.TryMapEngineIconPath(@"c:\app\DATA\ICONS\abc.png", out string url));
        Assert.Equal("https://local.data/ICONS/abc.png", url);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\Battlestate Games\EscapeFromTarkov\Icon Cache\abc.png")]
    public void Paths_outside_the_installed_icon_directory_are_rejected(string path)
    {
        // The dynamic EFT icon cache is not mapped into the WebView, so pointing at
        // it would render a broken image.
        Assert.False(ItemIconResolver.TryMapEngineIconPath(path, out string url));
        Assert.Equal(string.Empty, url);
    }

    [Fact]
    public void Resolve_prefers_the_engine_icon_path_over_the_remote_link()
    {
        string url = ItemIconResolver.Resolve(@"Data\icons\abc.png", "abc", "https://assets.tarkov.dev/abc-image.webp");

        Assert.Equal("https://local.data/icons/abc.png", url);
    }

    [Fact]
    public void Resolve_falls_back_to_the_remote_link_when_no_local_icon_exists()
    {
        ItemIconResolver.ResetForTests();

        string url = ItemIconResolver.Resolve(null, "definitelymissing0123456", "https://assets.tarkov.dev/x.webp");

        Assert.Equal("https://assets.tarkov.dev/x.webp", url);
    }

    [Fact]
    public void Resolve_returns_empty_when_nothing_is_available()
    {
        ItemIconResolver.ResetForTests();

        Assert.Equal(string.Empty, ItemIconResolver.Resolve(null, null, null));
    }

    [Theory]
    [InlineData("../../windows/system32/config")]
    [InlineData("abc/def")]
    [InlineData(@"abc\def")]
    [InlineData("abc.png")]
    [InlineData("C:abc")]
    public void Non_alphanumeric_ids_never_reach_the_file_system(string itemId)
    {
        // Ids are alphanumeric in the catalog; anything else must not be turned into
        // a path probe or a URL.
        ItemIconResolver.ResetForTests();

        Assert.Equal("fallback", ItemIconResolver.Resolve(null, itemId, "fallback"));
    }

    [Fact]
    public void Installed_static_icon_is_found_by_item_id()
    {
        const string itemId = "5449016a4bdc2d6f028b456f";
        string probedPath = string.Empty;

        bool mapped = ItemIconResolver.TryMapItemId(
            itemId,
            path =>
            {
                probedPath = path;
                return true;
            },
            out string url
        );

        Assert.True(mapped);
        Assert.Equal(Path.Combine(RatConfig.Paths.StaticIcon, itemId + ".png"), probedPath);
        Assert.Equal("https://local.data/icons/" + itemId + ".png", url);
    }

    [Fact]
    public void Missing_static_icon_falls_back_without_environment_dependencies()
    {
        bool mapped = ItemIconResolver.TryMapItemId("abc123", _ => false, out string url);

        Assert.False(mapped);
        Assert.Equal(string.Empty, url);
    }
}
