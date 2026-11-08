using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// The owner's decisions on checklists: two items added at the same moment keep one order on every load, and two people
/// applying one playbook at once add its steps once, neither moving the action's edit version.
/// </summary>
public class OwnerDecisionsChecklistsTests : IDisposable
{
    private TestDb _db = new(connectionPerContext: true);   // the playbook test races two applies

    public OwnerDecisionsChecklistsTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<ActionItem> NewAction()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var n = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team });
        return (await _db.Actions().GetAsync(n))!.Action;
    }

    // Two added at once read the same highest place and take the same next one. Here the one added second, with the
    // lower id, is written first (its save got in first), so neither the id nor the row's place in the table says which
    // came first: only when each was added does.
    [Fact]
    public async Task TwoItemsAddedAtTheSameMoment_KeepTheOrderTheyWereAddedIn_OnEveryLoad()
    {
        var a = await NewAction();
        var at = DateTimeOffset.UtcNow;
        foreach (var item in new[]
        {
            new ActionChecklistItem { Id = Guid.Parse("00000000-0000-0000-0000-00000000000b"), ActionId = a.Id, SortOrder = 0, Text = "Second", AddedAtUtc = at.AddMilliseconds(5) },
            new ActionChecklistItem { Id = Guid.Parse("ffffffff-0000-0000-0000-00000000000a"), ActionId = a.Id, SortOrder = 0, Text = "First", AddedAtUtc = at },
        })
        {
            await using var ctx = _db.PlainContext();
            ctx.ActionChecklistItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        (await _db.Actions().ChecklistAsync(a.Id)).Select(i => i.Text).Should().Equal("First", "Second");
        (await _db.Actions().GetAsync(a.Number))!.Action.Checklist.Select(i => i.Text).Should().Equal("First", "Second");
    }

    [Fact]
    public async Task AnItemAdded_IsStampedWithWhenItWasAdded_AndDoesntMoveTheActionsEditVersion()
    {
        var a = await NewAction();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _db.Actions().AddChecklistItemAsync(a.Id, "Pull the indicators");

        var now = (await _db.Actions().GetAsync(a.Number))!.Action;
        now.Checklist.Single().AddedAtUtc.Should().BeOnOrAfter(before);
        now.EditVersion.Should().Be(a.EditVersion, "a checklist change doesn't make an open form stale");
    }

    // Both read the checklist before either saved, and both added every step.
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TwoPeopleApplyingOnePlaybookAtOnce_AddItsStepsOnce(string provider)
    {
        _db = _db.On(provider);
        var a = await NewAction();
        var playbook = await _db.Playbooks().SaveAsync(new PlaybookForm
        {
            Name = "Ransomware sweep", Kind = ActionKind.IocSweep, Steps = ["Pull the indicators", "Sweep proxy logs", "Sweep EDR"],
        });

        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        _db.BeforeSave = ctx =>
        {
            if (!ctx.ChangeTracker.Entries<ActionChecklistItem>().Any(e => e.State == EntityState.Added)) return;
            if (Interlocked.Exchange(ref held, 1) == 1) return;
            reading.TrySetResult();
            Thread.Sleep(400);   // the first has read the checklist and is about to save
        };

        var first = Task.Run(() => _db.Actions().ApplyPlaybookAsync(a.Id, playbook));
        await reading.Task;
        var second = Task.Run(() => _db.Actions().ApplyPlaybookAsync(a.Id, playbook));
        var added = await Task.WhenAll(first, second);
        _db.BeforeSave = null;

        added.Should().BeEquivalentTo([3, 0], "the second finds the steps there");
        var now = (await _db.Actions().GetAsync(a.Number))!.Action;
        now.Checklist.Select(i => i.Text).Should().Equal("Pull the indicators", "Sweep proxy logs", "Sweep EDR");
        now.EditVersion.Should().Be(a.EditVersion, "applying a playbook doesn't make an open form stale");
    }
}
