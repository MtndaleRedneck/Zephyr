using Xunit;
using ZephyrVlcRemote;

namespace ZephyrVlcRemote.Tests;

/// <summary>"Feedback & bugs" must open Zephyr's own destinations, never BingeMote's form.</summary>
public class FeedbackLinksTests
{
    private const string BingeMoteFormId = "1FAIpQLSeD1ytVTxoA0GgftLr5wSu2kZm1yxRwCHcOV4Pb100Lm6hLcQ";
    private const string ZephyrFormId = "1FAIpQLSdzwHtL4OAUFpffzp0WqLq99AfY0QIHrl-CPjFLTp4nL1ThuA";

    [Fact]
    public void FormLinkIsTheZephyrFormNotBingeMote()
    {
        var url = FeedbackLinks.FormUrl("Play button stuck", "1.0.1", "Windows 11 (build 22631)");
        Assert.StartsWith("https://docs.google.com/forms/d/e/" + ZephyrFormId + "/viewform?usp=pp_url&", url);
        Assert.DoesNotContain(BingeMoteFormId, url);
        Assert.DoesNotContain("BingeMote", url, StringComparison.OrdinalIgnoreCase);
        // BingeMote form field ids must not leak into the Zephyr link.
        Assert.DoesNotContain("entry.1980878269", url);
        Assert.Contains("entry.574867238=Play%20button%20stuck", url);
        Assert.Contains("entry.764416810=Zephyr%20VLC%20Remote", url);
        Assert.Contains("entry.2049802033=1.0.1%20%28Windows%2011%20%28build%2022631%29%29", url);
        // The reply-email field is never prefilled.
        Assert.DoesNotContain("entry.1538046022", url);
    }

    [Fact]
    public void IssueLinkIsZephyrsOwnRepo()
    {
        var url = FeedbackLinks.IssueUrl("Volume keys do nothing", "1.0.1", "Windows 10 (build 19045)");
        Assert.StartsWith("https://github.com/MtndaleRedneck/Zephyr/issues/new?title=", url);
        Assert.DoesNotContain("BingeMote", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Uri.EscapeDataString("Zephyr VLC Remote PC version: 1.0.1"), url);
    }

    [Fact]
    public void LongIssueStaysUnderTheLinkLimitAndKeepsTheVersion()
    {
        var url = FeedbackLinks.IssueUrl(new string('x', 4000), "1.0.1", "Windows 11 (build 22631)");
        Assert.True(url.Length <= 8000, "issue link is " + url.Length + " chars");
        Assert.Contains(Uri.EscapeDataString("Windows version: Windows 11 (build 22631)"), url);
    }
}
