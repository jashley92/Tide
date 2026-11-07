using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Adversaries;
using Tide.Application.Articles;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Bug round 3, concurrency: two people recording one outreach reply, a merge racing a tag, and a profile's or a
/// product's form saved over a colleague's change.</summary>
public class ConcurrencyRound3BugTests : IDisposable
{
    private TestDb _db = new(connectionPerContext: true);   // the merge test races a tag

    public ConcurrencyRound3BugTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<OutreachMessage> SentEmail()
    {
        var team = await _db.Teams().AddAsync("Network", null, null);
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Ask the vendor", Kind = ActionKind.Outreach, TeamId = team });
        var action = (await svc.GetAsync(n))!.Action;
        return await _db.Outreach().MarkSentAsync(action.Id,
            new OutreachSend { To = "psirt@vendor.example", Subject = "Are we affected?", Body = "Hello,\n\nAre we affected?", Tlp = TlpLevel.Green });
    }

    // Sam records the reply between this one's read (still waiting) and its save, as two people pressing Record at once do.
    private void SamRecordsMeanwhile(Guid messageId) => _db.BeforeSave = ctx =>
    {
        if (!ctx.ChangeTracker.Entries<OutreachMessage>().Any(e => e.State is EntityState.Modified or EntityState.Deleted)) return;
        _db.BeforeSave = null;
        using var other = _db.PlainContext();
        other.OutreachMessages.Where(m => m.Id == messageId).ExecuteUpdate(u => u
            .SetProperty(m => m.RepliedAtUtc, DateTimeOffset.UtcNow).SetProperty(m => m.ReplyRecordedByName, "Sam")
            .SetProperty(m => m.ReplyNotes, "Sam's notes: not affected.").SetProperty(m => m.EditVersion, Guid.NewGuid()));
    };

    [Fact]
    public async Task TwoPeopleRecordingTheSameReply_TheSecondIsRefused_AndTheFirstsNotesKept()
    {
        var m = await SentEmail();
        SamRecordsMeanwhile(m.Id);

        await FluentActions.Invoking(() => _db.Outreach().RecordReplyAsync(m.Id, "My notes: checking.", close: false))
            .Should().ThrowAsync<StaleEditException>().WithMessage("Sam recorded the reply to this email at the same moment*");

        var kept = (await _db.Outreach().ForActionAsync(m.ActionId)).Messages.Single();
        (kept.ReplyRecordedByName, kept.ReplyNotes).Should().Be(("Sam", "Sam's notes: not affected."));
    }

    [Fact]
    public async Task TakingAnEmailBack_AsAColleagueRecordsItsReply_IsRefused_AndTheReplyKept()
    {
        var m = await SentEmail();
        SamRecordsMeanwhile(m.Id);

        await FluentActions.Invoking(() => _db.Outreach().WithdrawAsync(m.Id))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("Sam recorded its reply at the same moment, so it stays.");

        (await _db.Outreach().ForActionAsync(m.ActionId)).Messages.Should().ContainSingle().Which.ReplyNotes.Should().Be("Sam's notes: not affected.");
    }

    // A tag added to the old profile after the merge read its tags, and before it saved, stayed on the retired profile.
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ATagAddedToAProfileAsItMerges_EndsOnTheProfileItMergedInto(string provider)
    {
        _db = _db.On(provider);
        var adv = _db.Adversaries();
        var spider = await adv.SaveAsync(new AdversaryForm { Name = "Scattered Spider" });
        var libra = await adv.SaveAsync(new AdversaryForm { Name = "Muddled Libra" });
        var first = await _db.Articles().CreateAsync("SIM swaps", "Vendor", "https://vendor.example/1");
        var second = await _db.Articles().CreateAsync("Help-desk resets", "Vendor", "https://vendor.example/2");
        await adv.TagArticleAsync(first, libra);

        var merging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _db.BeforeSave = ctx =>
        {
            if (!ctx.ChangeTracker.Entries<Adversary>().Any(e => e.Entity.MergedIntoId is not null && e.State == EntityState.Modified)) return;
            merging.TrySetResult();
            Thread.Sleep(400);   // the merge has read the old profile's tags and is about to save
        };

        var merge = Task.Run(() => _db.Adversaries().MergeAsync(libra, spider));
        await merging.Task;
        var tag = Task.Run(() => _db.Adversaries().TagArticleAsync(second, libra));
        await Task.WhenAll(merge, tag);
        _db.BeforeSave = null;

        await using var ctx = _db.Context();
        (await ctx.ArticleAdversaries.Where(t => t.AdversaryId == libra).CountAsync()).Should().Be(0, "nothing is left on the retired profile");
        (await ctx.ArticleAdversaries.Where(t => t.AdversaryId == spider).Select(t => t.ArticleId).ToListAsync()).Should().BeEquivalentTo([first, second]);
    }

    [Fact]
    public async Task AProfilesForm_OpenedBeforeAMergeIntoIt_CantSaveTheMergedNamesAway()
    {
        var adv = _db.Adversaries();
        var spider = await adv.SaveAsync(new AdversaryForm { Name = "Scattered Spider", Aliases = "UNC3944" });
        var libra = await adv.SaveAsync(new AdversaryForm { Name = "Muddled Libra" });
        var open = AdversaryForm.From((await adv.GetAsync(spider))!.Adversary);

        await adv.MergeAsync(libra, spider);   // a colleague, in another tab
        open.Notes = "Calls the help desk.";

        await FluentActions.Invoking(() => adv.SaveAsync(open)).Should().ThrowAsync<StaleEditException>().WithMessage("Someone else changed Scattered Spider*");
        (await adv.GetAsync(spider))!.Adversary.Aliases.Should().Be("UNC3944; Muddled Libra", "the merged names stay");

        // Opened again, on top of theirs, it saves.
        var again = AdversaryForm.From((await adv.GetAsync(spider))!.Adversary);
        again.Notes = "Calls the help desk.";
        await adv.SaveAsync(again);
        (await adv.GetAsync(spider))!.Adversary.Notes.Should().Be("Calls the help desk.");
    }

    [Fact]
    public async Task AProductsForm_SavedAfterAColleaguesSave_IsRefused_AndTheirsKept()
    {
        var stack = _db.Stack();
        var id = await stack.SaveAsync(new StackForm { Name = "Edge VPN", Versions = "22.7R1" });
        var mine = StackForm.From((await stack.GetAsync(id))!.Product);
        var theirs = StackForm.From((await stack.GetAsync(id))!.Product);

        theirs.Versions = "22.7R2.4";
        await stack.SaveAsync(theirs);
        mine.Notes = "Two gateways.";

        await FluentActions.Invoking(() => stack.SaveAsync(mine)).Should().ThrowAsync<StaleEditException>().WithMessage("Someone else saved Edge VPN since you opened it*");
        (await stack.GetAsync(id))!.Product.Versions.Should().Be("22.7R2.4");
    }
}

/// <summary>Our stack's page, with a colleague saving the product someone has typed into.</summary>
public class ConcurrencyRound3PageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Colleague = new("dev:r3c-robin", "Robin", "Analyst", "Admin");

    private static IReadOnlyList<ToastService.Toast> Toasts<T>(Rendered<T> r) => r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts;

    [Fact]
    public async Task AProductsTypedForm_SaysWhoSavedItMeanwhile_AndItsSaveIsRefused()
    {
        Guid id;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = "R3c Gateway " + Guid.NewGuid().ToString("N")[..6], Versions = "1.0" });

        await using var typed = await RenderAsync<StackPage>(app, $"/stack/{id}", new() { ["Id"] = id });
        await typed.Click(p => { p.Form!.Notes = "Typed, not saved"; return Task.CompletedTask; });

        await using (var robin = SignedIn(app, Colleague))
        {
            var svc = robin.ServiceProvider.GetRequiredService<StackService>();
            var form = StackForm.From((await svc.GetAsync(id))!.Product);
            form.Versions = "1.1";
            await svc.SaveAsync(form);
        }

        for (var i = 0; i < 50 && typed.Component.ChangedBy is null; i++) await Task.Delay(100);
        typed.Component.ChangedBy.Should().Be("Robin");
        typed.Component.Form!.Notes.Should().Be("Typed, not saved", "what's typed stays");

        await typed.Click(p => p.Save());
        Toasts(typed).Select(t => t.Message).Should().Contain(m => m.StartsWith("Someone else saved"));
        await using (var check = SignedIn(app, SignIn.Analyst))
            (await check.ServiceProvider.GetRequiredService<StackService>().GetAsync(id))!.Product.Versions.Should().Be("1.1", "Robin's save stands");

        await typed.Click(p => p.ReloadTheirs());
        (typed.Component.ChangedBy, typed.Component.Form!.Versions).Should().Be((null, "1.1"));
    }
}
