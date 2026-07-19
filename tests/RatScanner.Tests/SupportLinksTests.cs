using Xunit;

namespace RatScanner.Tests;

public sealed class SupportLinksTests
{
    [Fact]
    public void Support_link_constants_point_at_the_fork_not_upstream()
    {
        // Crash reports and the in-app FAQ must land where this build is
        // actually maintained (tarkovtracker-org). Filing against the upstream
        // repo would silently lose every 4.x crash report.
        Assert.Contains("tarkovtracker-org", Constants.Links.SupportGitHub);
        Assert.Contains("tarkovtracker-org", Constants.Links.SupportFAQ);
        Assert.DoesNotContain("RatScanner/RatScanner/issues", Constants.Links.SupportGitHub);
    }

    [Fact]
    public void Support_FAQ_resolves_to_a_faq_document_path()
    {
        Assert.EndsWith("FAQ.md", Constants.Links.SupportFAQ);
    }
}
