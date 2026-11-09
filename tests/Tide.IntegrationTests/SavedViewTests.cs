using AwesomeAssertions;
using Tide.Application.Preferences;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Each person's saved views of the Actions page: saved, listed in their order, renamed, moved and deleted by that person
/// alone; at most 12; a name once per person whatever its case; and only the page's own filters kept, each within its length.
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
        var urgent = await Views.SaveAsync("  Urgent  ", "view=open&layout=list&priority=p1,p2");
        var mine = await Views.SaveAsync("Mine by kind", "?view=mine&layout=list&group=kind");
        var blocked = await Views.SaveAsync("Blocked", "view=open&layout=board&status=blocked");

        urgent.Name.Should().Be("Urgent", "the name is trimmed");
        mine.Query.Should().Be("view=mine&layout=list&group=kind", "the leading ? isn't kept");
        (await Views.ListAsync()).Select(v => v.Name).Should().Equal("Urgent", "Mine by kind", "Blocked");

        await Views.RenameAsync(urgent.Id, "P1 and P2");
        await Views.MoveAsync(blocked.Id, -1);
        (await Views.ListAsync()).Select(v => v.Name).Should().Equal("P1 and P2", "Blocked", "Mine by kind");

        await Views.MoveAsync(urgent.Id, 5);
        (await Views.ListAsync()).Select(v => v.Name).Should().Equal("Blocked", "Mine by kind", "P1 and P2");
        await Views.MoveAsync(blocked.Id, -1);
        (await Views.ListAsync()).Select(v => v.Name).Should().Equal("Blocked", "Mine by kind", "P1 and P2");

        await Views.DeleteAsync(mine.Id);
        var left = await Views.ListAsync();
        left.Select(v => v.Name).Should().Equal("Blocked", "P1 and P2");
        left.Should().Contain(v => v.Id == urgent.Id).Which.Query.Should().Be("view=open&layout=list&priority=p1,p2");

        var next = await Views.SaveAsync("Due this week", "view=open&due=week");
        (await Views.ListAsync())[^1].Id.Should().Be(next.Id, "a new view goes after the others");
    }

    [Fact]
    public async Task AnotherPerson_CantSee_Rename_Move_OrDeleteThem()
    {
        var urgent = await Views.SaveAsync("Urgent", "view=open&priority=p1");

        As(Sam);
        (await Views.ListAsync()).Should().BeEmpty("Robin's views are Robin's");
        await FluentActions.Awaiting(() => Views.RenameAsync(urgent.Id, "Mine now")).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Awaiting(() => Views.MoveAsync(urgent.Id, 1)).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Awaiting(() => Views.DeleteAsync(urgent.Id)).Should().ThrowAsync<KeyNotFoundException>();
        var sams = await Views.SaveAsync("Urgent", "view=mine");
        sams.Id.Should().NotBe(urgent.Id, "the same name is free for someone else");

        As(Robin);
        (await Views.ListAsync()).Should().ContainSingle().Which.Should().Be(urgent, "nothing Sam did reached Robin's");
    }

    [Fact]
    public async Task APerson_KeepsAtMostTwelve()
    {
        for (var i = 1; i <= SavedViewService.MaxViews; i++) await Views.SaveAsync($"View {i}", $"view=open&parent={i}");
        await FluentActions.Awaiting(() => Views.SaveAsync("One more", "view=open")).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*up to 12*");
        (await Views.ListAsync()).Should().HaveCount(SavedViewService.MaxViews);

        As(Sam);
        await Views.SaveAsync("Sam's first", "view=open");
        (await Views.ListAsync()).Should().ContainSingle().Which.Name.Should().Be("Sam's first", "the limit is each person's");
    }

    [Fact]
    public async Task Names_Empty_TooLong_OrAlreadyTheirs_AreRefused()
    {
        var urgent = await Views.SaveAsync("Urgent", "view=open");
        var other = await Views.SaveAsync(new string('x', SavedActionView.MaxName), "view=mine");

        foreach (var name in new[] { "", "   ", new string('y', SavedActionView.MaxName + 1), "urgent", " URGENT " })
            await FluentActions.Awaiting(() => Views.SaveAsync(name, "view=open")).Should().ThrowAsync<ArgumentException>($"\"{name}\" can't be a new view's name");
        await FluentActions.Awaiting(() => Views.RenameAsync(other.Id, "Urgent")).Should().ThrowAsync<ArgumentException>();

        await Views.RenameAsync(urgent.Id, "URGENT");
        (await Views.ListAsync()).Should().Contain(v => v.Id == urgent.Id).Which.Name.Should().Be("URGENT", "a view can change its own name's case");
    }

    [Theory]
    [InlineData("view=open&evil=1")]
    [InlineData("view=open&returnUrl=https://example.org")]
    [InlineData("view=open&view=mine")]
    [InlineData("VIEW=open")]
    public async Task AQuery_WithAnythingButThePagesOwnFilters_IsRefused(string query)
    {
        await FluentActions.Awaiting(() => Views.SaveAsync("Odd", query)).Should().ThrowAsync<ArgumentException>();
        (await Views.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AValue_LongerThanItsFilterTakes_IsRefused()
    {
        await FluentActions.Awaiting(() => Views.SaveAsync("Long", "q=" + new string('a', 201))).Should().ThrowAsync<ArgumentException>().WithMessage("*q filter*");
        await FluentActions.Awaiting(() => Views.SaveAsync("Long", "assignee=" + new string('a', 257))).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Views.SaveAsync("Long", "view=" + new string('a', 17))).Should().ThrowAsync<ArgumentException>();
        (await Views.SaveAsync("Longest words", "q=" + new string('a', 200))).Query.Should().Be("q=" + new string('a', 200));
    }

    [Fact]
    public async Task AQuery_IsKeptInTheOrderThePageWritesIt_WithoutItsPage()
    {
        var v = await Views.SaveAsync("Mixed", "priority=p1,p2&page=3&q=edge+vpn&layout=list&view=open&status=&raised=me");
        v.Query.Should().Be("view=open&layout=list&q=edge%20vpn&priority=p1,p2&raised=me", "the page and empty values go, the rest in a fixed order");
        SavedViewService.Normalize("?view=open&q=edge%20vpn&priority=p1%2Cp2&layout=list").Should().Be("view=open&layout=list&q=edge%20vpn&priority=p1,p2");
        SavedViewService.TryNormalize("view=open&nope=1").Should().BeNull();
    }

    [Fact]
    public async Task AViewer_KeepsViews_ButSomeoneWithoutTheActionsPage_OrSignedOut_DoesNot()
    {
        As("S-VIEWER", AppRole.Viewer);
        await Views.SaveAsync("Viewer's", "view=open");
        (await Views.ListAsync()).Should().ContainSingle();

        As("S-READER", AppRole.Reader);
        await FluentActions.Awaiting(() => Views.ListAsync()).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => Views.SaveAsync("Reader's", "view=open")).Should().ThrowAsync<ForbiddenException>();

        As(Robin);
        _db.User.IsAuthenticated = false;
        await FluentActions.Awaiting(() => Views.ListAsync()).Should().ThrowAsync<ForbiddenException>();
    }
}
