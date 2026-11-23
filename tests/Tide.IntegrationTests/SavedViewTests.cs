using AwesomeAssertions;
using Tide.Application.Preferences;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Each person's saved views of a page (Actions, Triage, Articles, Search): saved, listed in their order, renamed, moved
/// and deleted by that person alone; at most 12 a page; a name once per person and page whatever its case; and only the
/// page's own filters kept, each within its length.
/// </summary>
public class SavedViewTests : IDisposable
{
    private TestDb _db = new();

    private const string Robin = "S-ROBIN", Sam = "S-SAM";

    public SavedViewTests() => As(Robin);

    public void Dispose() => _db.Dispose();

    private void As(string id, AppRole role = AppRole.Analyst)
    {
        _db.User.UserId = id;
        _db.User.DisplayName = id;
        _db.User.Roles = new HashSet<AppRole> { role };
        _db.User.IsAuthenticated = true;
    }

    private SavedViewService Views => _db.SavedViews();

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Views_AreSaved_Listed_Renamed_Moved_AndDeleted(string provider)
    {
        _db = _db.On(provider);
        As(Robin);
        var urgent = await Views.SaveAsync(SavedViewPage.Actions, "  Urgent  ", "view=open&layout=list&priority=p1,p2");
        var mine = await Views.SaveAsync(SavedViewPage.Actions, "Mine by kind", "?view=mine&layout=list&group=kind");
        var blocked = await Views.SaveAsync(SavedViewPage.Actions, "Blocked", "view=open&layout=board&status=blocked");

        urgent.Name.Should().Be("Urgent", "the name is trimmed");
        mine.Query.Should().Be("view=mine&layout=list&group=kind", "the leading ? isn't kept");
        (await Views.ListAsync(SavedViewPage.Actions)).Select(v => v.Name).Should().Equal("Urgent", "Mine by kind", "Blocked");

        await Views.RenameAsync(SavedViewPage.Actions, urgent.Id, "P1 and P2");
        await Views.MoveAsync(SavedViewPage.Actions, blocked.Id, -1);
        (await Views.ListAsync(SavedViewPage.Actions)).Select(v => v.Name).Should().Equal("P1 and P2", "Blocked", "Mine by kind");

        await Views.MoveAsync(SavedViewPage.Actions, urgent.Id, 5);
        (await Views.ListAsync(SavedViewPage.Actions)).Select(v => v.Name).Should().Equal("Blocked", "Mine by kind", "P1 and P2");
        await Views.MoveAsync(SavedViewPage.Actions, blocked.Id, -1);
        (await Views.ListAsync(SavedViewPage.Actions)).Select(v => v.Name).Should().Equal("Blocked", "Mine by kind", "P1 and P2");

        await Views.DeleteAsync(SavedViewPage.Actions, mine.Id);
        var left = await Views.ListAsync(SavedViewPage.Actions);
        left.Select(v => v.Name).Should().Equal("Blocked", "P1 and P2");
        left.Should().Contain(v => v.Id == urgent.Id).Which.Query.Should().Be("view=open&layout=list&priority=p1,p2");

        var next = await Views.SaveAsync(SavedViewPage.Actions, "Due this week", "view=open&due=week");
        (await Views.ListAsync(SavedViewPage.Actions))[^1].Id.Should().Be(next.Id, "a new view goes after the others");
    }

    [Fact]
    public async Task AnotherPerson_CantSee_Rename_Move_OrDeleteThem()
    {
        var urgent = await Views.SaveAsync(SavedViewPage.Actions, "Urgent", "view=open&priority=p1");

        As(Sam);
        (await Views.ListAsync(SavedViewPage.Actions)).Should().BeEmpty("Robin's views are Robin's");
        await FluentActions.Awaiting(() => Views.RenameAsync(SavedViewPage.Actions, urgent.Id, "Mine now")).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Awaiting(() => Views.MoveAsync(SavedViewPage.Actions, urgent.Id, 1)).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Awaiting(() => Views.DeleteAsync(SavedViewPage.Actions, urgent.Id)).Should().ThrowAsync<KeyNotFoundException>();
        var sams = await Views.SaveAsync(SavedViewPage.Actions, "Urgent", "view=mine");
        sams.Id.Should().NotBe(urgent.Id, "the same name is free for someone else");

        As(Robin);
        (await Views.ListAsync(SavedViewPage.Actions)).Should().ContainSingle().Which.Should().Be(urgent, "nothing Sam did reached Robin's");
    }

    [Fact]
    public async Task APerson_KeepsAtMostTwelve()
    {
        for (var i = 1; i <= SavedViewService.MaxViews; i++) await Views.SaveAsync(SavedViewPage.Actions, $"View {i}", $"view=open&parent={i}");
        await FluentActions.Awaiting(() => Views.SaveAsync(SavedViewPage.Actions, "One more", "view=open")).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*up to 12*");
        (await Views.ListAsync(SavedViewPage.Actions)).Should().HaveCount(SavedViewService.MaxViews);

        As(Sam);
        await Views.SaveAsync(SavedViewPage.Actions, "Sam's first", "view=open");
        (await Views.ListAsync(SavedViewPage.Actions)).Should().ContainSingle().Which.Name.Should().Be("Sam's first", "the limit is each person's");
    }

    [Fact]
    public async Task Names_Empty_TooLong_OrAlreadyTheirs_AreRefused()
    {
        var urgent = await Views.SaveAsync(SavedViewPage.Actions, "Urgent", "view=open");
        var other = await Views.SaveAsync(SavedViewPage.Actions, new string('x', SavedActionView.MaxName), "view=mine");

        foreach (var name in new[] { "", "   ", new string('y', SavedActionView.MaxName + 1), "urgent", " URGENT " })
            await FluentActions.Awaiting(() => Views.SaveAsync(SavedViewPage.Actions, name, "view=open")).Should().ThrowAsync<ArgumentException>($"\"{name}\" can't be a new view's name");
        await FluentActions.Awaiting(() => Views.RenameAsync(SavedViewPage.Actions, other.Id, "Urgent")).Should().ThrowAsync<ArgumentException>();

        await Views.RenameAsync(SavedViewPage.Actions, urgent.Id, "URGENT");
        (await Views.ListAsync(SavedViewPage.Actions)).Should().Contain(v => v.Id == urgent.Id).Which.Name.Should().Be("URGENT", "a view can change its own name's case");
    }

    [Theory]
    [InlineData("view=open&evil=1")]
    [InlineData("view=open&returnUrl=https://example.org")]
    [InlineData("view=open&view=mine")]
    [InlineData("VIEW=open")]
    public async Task AQuery_WithAnythingButThePagesOwnFilters_IsRefused(string query)
    {
        await FluentActions.Awaiting(() => Views.SaveAsync(SavedViewPage.Actions, "Odd", query)).Should().ThrowAsync<ArgumentException>();
        (await Views.ListAsync(SavedViewPage.Actions)).Should().BeEmpty();
    }

    [Fact]
    public async Task AValue_LongerThanItsFilterTakes_IsRefused()
    {
        await FluentActions.Awaiting(() => Views.SaveAsync(SavedViewPage.Actions, "Long", "q=" + new string('a', 201))).Should().ThrowAsync<ArgumentException>().WithMessage("*q filter*");
        await FluentActions.Awaiting(() => Views.SaveAsync(SavedViewPage.Actions, "Long", "assignee=" + new string('a', 257))).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Views.SaveAsync(SavedViewPage.Actions, "Long", "view=" + new string('a', 17))).Should().ThrowAsync<ArgumentException>();
        (await Views.SaveAsync(SavedViewPage.Actions, "Longest words", "q=" + new string('a', 200))).Query.Should().Be("q=" + new string('a', 200));
    }

    [Fact]
    public async Task AQuery_IsKeptInTheOrderThePageWritesIt_WithoutItsPage()
    {
        var v = await Views.SaveAsync(SavedViewPage.Actions, "Mixed", "priority=p1,p2&page=3&q=edge+vpn&layout=list&view=open&status=&raised=me");
        v.Query.Should().Be("view=open&layout=list&q=edge%20vpn&priority=p1,p2&raised=me", "the page and empty values go, the rest in a fixed order");
        SavedViewService.Normalize(SavedViewPage.Actions, "?view=open&q=edge%20vpn&priority=p1%2Cp2&layout=list").Should().Be("view=open&layout=list&q=edge%20vpn&priority=p1,p2");
        SavedViewService.TryNormalize(SavedViewPage.Actions, "view=open&nope=1").Should().BeNull();
    }

    [Fact]
    public async Task AViewer_KeepsViews_ButSomeoneWithoutTheActionsPage_OrSignedOut_DoesNot()
    {
        As("S-VIEWER", AppRole.Viewer);
        await Views.SaveAsync(SavedViewPage.Actions, "Viewer's", "view=open");
        (await Views.ListAsync(SavedViewPage.Actions)).Should().ContainSingle();

        As("S-READER", AppRole.Reader);
        await FluentActions.Awaiting(() => Views.ListAsync(SavedViewPage.Actions)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => Views.SaveAsync(SavedViewPage.Actions, "Reader's", "view=open")).Should().ThrowAsync<ForbiddenException>();

        As(Robin);
        _db.User.IsAuthenticated = false;
        await FluentActions.Awaiting(() => Views.ListAsync(SavedViewPage.Actions)).Should().ThrowAsync<ForbiddenException>();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task EachPage_HasItsOwnViews_ItsOwnTwelve_AndItsOwnNames(string provider)
    {
        _db = _db.On(provider);
        As(Robin);
        var actions = await Views.SaveAsync(SavedViewPage.Actions, "Urgent", "view=open&priority=p1");
        var triage = await Views.SaveAsync(SavedViewPage.Triage, "Urgent", "tab=new&cve=kev&named=true");
        var articles = await Views.SaveAsync(SavedViewPage.Articles, "Urgent", "assess=p1-p2&author=me");
        var search = await Views.SaveAsync(SavedViewPage.Search, "Urgent", "q=CVE-2026-1234");

        (await Views.ListAsync(SavedViewPage.Actions)).Should().ContainSingle().Which.Should().Be(actions, "the same name is free on each page");
        (await Views.ListAsync(SavedViewPage.Triage)).Should().ContainSingle().Which.Should().Be(triage);
        (await Views.ListAsync(SavedViewPage.Articles)).Should().ContainSingle().Which.Should().Be(articles);
        (await Views.ListAsync(SavedViewPage.Search)).Should().ContainSingle().Which.Should().Be(search);

        // A view is changed only as one of its own page's: another page's id isn't there.
        await FluentActions.Awaiting(() => Views.RenameAsync(SavedViewPage.Triage, actions.Id, "Moved")).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Awaiting(() => Views.DeleteAsync(SavedViewPage.Search, triage.Id)).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Awaiting(() => Views.MoveAsync(SavedViewPage.Actions, articles.Id, 1)).Should().ThrowAsync<KeyNotFoundException>();

        for (var i = 2; i <= SavedViewService.MaxViews; i++) await Views.SaveAsync(SavedViewPage.Triage, $"View {i}", $"tab=new&q=word{i}");
        await FluentActions.Awaiting(() => Views.SaveAsync(SavedViewPage.Triage, "One more", "tab=dismissed")).Should().ThrowAsync<InvalidOperationException>();
        await Views.SaveAsync(SavedViewPage.Articles, "Another", "status=draft");
        (await Views.ListAsync(SavedViewPage.Articles)).Should().HaveCount(2, "the twelve are each page's");
    }

    [Fact]
    public void Triage_DropsTheRowAndThePage_AsActionsDropsThePage()
    {
        SavedViewService.Normalize(SavedViewPage.Triage, "?older=true&at=6f1c0d8e-0000-4000-8000-000000000001&row=3&page=2&promote=x&named=true&cve=kev&tab=new")
            .Should().Be("tab=new&cve=kev&named=true&older=true", "the row, the page and a promote form opened aren't part of a view");
        SavedViewService.TryNormalize(SavedViewPage.Triage, "tab=new&view=open").Should().BeNull("Actions' own filters aren't Triage's");
        SavedViewService.TryNormalize(SavedViewPage.Triage, "tab=new&q=" + new string('a', 201)).Should().BeNull();
    }

    [Fact]
    public void Articles_AndSearch_KeepTheirOwnFilters()
    {
        SavedViewService.Normalize(SavedViewPage.Articles, "page=4&new=1&tag=ransomware&assess=p1-p2&from=2026-10-01&author=me&q=edge vpn")
            .Should().Be("q=edge%20vpn&author=me&assess=p1-p2&tag=ransomware&from=2026-10-01");
        SavedViewService.TryNormalize(SavedViewPage.Articles, "layout=board").Should().BeNull();
        SavedViewService.Normalize(SavedViewPage.Search, "q=evil%5B.%5Dcom").Should().Be("q=evil%5B.%5Dcom");
        SavedViewService.TryNormalize(SavedViewPage.Search, "q=x&tab=new").Should().BeNull();
        SavedViewService.QueryOf(SavedViewPage.Search, [new("q", null)]).Should().BeEmpty("no search, nothing to keep");
    }

    [Fact]
    public async Task WhoMayKeepViews_IsWhoMayOpenThePage()
    {
        As("S-VIEWER", AppRole.Viewer);
        await Views.SaveAsync(SavedViewPage.Articles, "Viewer's", "status=ready");
        await Views.SaveAsync(SavedViewPage.Search, "Viewer's", "q=lockbit");
        await FluentActions.Awaiting(() => Views.ListAsync(SavedViewPage.Triage)).Should().ThrowAsync<ForbiddenException>("triage is the writers'");
        await FluentActions.Awaiting(() => Views.SaveAsync(SavedViewPage.Triage, "Viewer's", "tab=new")).Should().ThrowAsync<ForbiddenException>();

        As("S-READER", AppRole.Reader);
        await FluentActions.Awaiting(() => Views.ListAsync(SavedViewPage.Articles)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => Views.ListAsync(SavedViewPage.Search)).Should().ThrowAsync<ForbiddenException>();
    }
}
