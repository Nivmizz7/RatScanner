#nullable enable

using System;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace RatScanner.Tests;

public sealed class WebViewHostingContractTests
{
    [Fact]
    public void Main_window_retains_transparency_required_by_minimal_ui()
    {
        string root = FindRepositoryRoot();
        XDocument pageSwitcher = XDocument.Load(Path.Combine(root, "src", "App", "PageSwitcher.xaml"));
        XElement window = Assert.IsType<XElement>(pageSwitcher.Root);

        Assert.Equal("True", window.Attribute("AllowsTransparency")?.Value);
        Assert.Equal("None", window.Attribute("WindowStyle")?.Value);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RatScanner.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
