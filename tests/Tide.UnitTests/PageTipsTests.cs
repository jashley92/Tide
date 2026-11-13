using AwesomeAssertions;
using Tide.Application.Operations;
using Tide.Application.Preferences;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>
/// A page's tip: one per page that changed, on the first visit after the release it belongs to, only on a page the
/// person can use, and never again once dismissed.
/// </summary>
public class PageTipsTests
{
    private static ReleaseVersion V(string s) => ReleaseVersion.Parse(s)!;

    [Theory]
    [InlineData("", "today")]
    [InlineData("?row=3", "today")]
    [InlineData("triage", "triage")]
    [InlineData("triage?tab=sources#x", "triage")]
    [InlineData("articles/0d6f5d38-7a2e-4b9f-9f6e-1d2c3b4a5e6f", "editor")]
    [InlineData("actions", "actions")]
    [InlineData("actions?layout=list", "actions")]
    [InlineData("actions/12", "action")]
    [InlineData("search?q=1.2.3.4", "search")]
    [InlineData("library/templates", "library")]
    [InlineData("stack", "stack")]
    public void EachChangedPage_HasOneTip(string path, string key) =>
        PageTips.For(path)!.Key.Should().Be(key);

    [Theory]
    [InlineData("articles")]
    [InlineData("articles/new")]
    [InlineData("actions/new")]
    [InlineData("present/2026-11-12")]
    [InlineData("help")]
    [InlineData("admin/organization")]
    [InlineData("triagex")]
    public void APageThatDidntChange_OrPresentMode_HasNone(string path) =>
        PageTips.For(path).Should().BeNull();

    [Fact]
    public void EveryTip_SaysWhatChanged()
    {
        PageTips.All.Should().OnlyHaveUniqueItems(t => t.Key);
        PageTips.All.Should().OnlyContain(t => t.Title.Length > 0 && t.Text.Length > 0 && t.Version == V("1.13.0"));
        PageTips.For("")!.Text.Should().Contain("]", "Today's tip points at the context rail and its key");
    }

    [Fact]
    public void NoTip_NamesAButtonByAWordItMayNotShow()
    {
        // On a narrow window the Context button is its icon alone.
        PageTips.All.Should().OnlyContain(t => !t.Text.Contains("use Context") && !t.Text.Contains(", or Context"));
        PageTips.For("")!.Text.Should().Contain("rail button");
    }

    [Fact]
    public void SearchsTip_SitsInItsCorner_OffThePagesOwnSearchBox()
    {
        // Pointed at the top bar's search box it sat over the page's own box and its description.
        PageTips.For("search")!.Target.Should().BeEmpty();
    }

    [Fact]
    public void TriagesTip_ShowsOnlyBesideTheReadingPane()
    {
        // On a narrow window the reading pane isn't beside the list, and a tip in the corner sat over the decisions.
        PageTips.For("triage")!.NeedsTarget.Should().BeTrue();
        PageTips.All.Where(t => t.Key != "triage").Should().OnlyContain(t => !t.NeedsTarget);
    }

    [Fact]
    public void ATip_IsDue_OnceAfterItsRelease()
    {
        var triage = PageTips.For("triage")!;
        triage.Needs.Should().Be(Permission.WriteArticles);
        triage.IsDue(running: V("1.13.0"), dismissed: null).Should().BeTrue();
        triage.IsDue(V("1.13.0"), V("1.13.0")).Should().BeFalse("dismissed with Got it");
        triage.IsDue(V("1.14.0"), V("1.13.0")).Should().BeFalse("a later release without a new tip for the page");
        triage.IsDue(V("1.12.0"), null).Should().BeFalse("not before the release it's about");
    }

    [Fact]
    public void EachTip_IsAPreference_ThatHoldsTheReleaseDismissed_ForWhoeverCanUseItsPage()
    {
        foreach (var tip in PageTips.All)
        {
            var d = UserPreferences.For(tip.Preference);
            d.Needs.Should().Be(tip.Needs);
            d.Default.Should().BeEmpty();
            d.Allows("1.13.0").Should().BeTrue();
            d.Allows("on").Should().BeFalse();
        }
    }
}
