using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Briefs;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Undo of Promote and keep triaging: it takes back only what the promote made, never work done on the draft
/// since, in whatever table that work went.</summary>
public class TriagePromoteUndoTests(StayApp app) : IClassFixture<StayApp>
{
    private async Task<Guid[]> Seed(string key, int count, FeedItemStatus status = FeedItemStatus.New)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = new Source { Name = "Undo source " + key, Url = "https://undo-promote.example/feed/" + key, DefaultTlp = TlpLevel.Green };
        var at = DateTimeOffset.UtcNow;
        var items = Enumerable.Range(1, count).Select(n => new FeedItem
        {
            Source = source, Title = $"undo-{key} report {n}: a breach at a payroll provider", Url = $"https://undo-promote.example/{key}/{n}",
            UrlHash = $"undo-promote-{key}-{n}", Cves = $"CVE-2026-{Math.Abs((key + n).GetHashCode()) % 90000 + 10000}",
            PublishedAtUtc = at.AddMinutes(-n), FetchedAtUtc = at.AddMinutes(-n), Status = status,
            StatusByName = status == FeedItemStatus.New ? null : "Robin", StatusAtUtc = status == FeedItemStatus.New ? null : at.AddHours(-1),
        }).ToList();
        db.Add(source);
        db.AddRange(items);
        await db.SaveChangesAsync();
        return [.. items.Select(i => i.Id)];
    }

    private async Task<T> Read<T>(Func<AppDbContext, Task<T>> read)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<FeedItem> Item(Guid id) => Read(db => db.FeedItems.AsNoTracking().SingleAsync(i => i.Id == id));

    private Task<bool> ArticleExists(Guid id) => Read(db => db.Articles.AnyAsync(a => a.Id == id));

    // Work someone might do on the draft after "Write it" opened it in another tab, none of which moves its edit version.
    public static TheoryData<string> Work() => ["link", "adversary", "ai-draft", "review", "action", "screenshot"];

    [Theory]
    [MemberData(nameof(Work))]
    public async Task Undo_IsRefused_OnceTheDraftHasWorkOnIt(string work)
    {
        var ids = await Seed("work-" + work, 1);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var promoter = scope.ServiceProvider.GetRequiredService<TriagePromoter>();
        var done = await promoter.PromoteAsync([ids[0]], new PromoteForm());
        var article = done.Outcome.ArticleId;
        await Read<int>(async db =>
        {
            switch (work)
            {
                case "link":
                    db.Add(new ArticleLink { ArticleId = article, Url = "https://elsewhere.example/" + work, Title = "Another report", AddedByName = "Robin" });
                    break;
                case "adversary":
                    var adversary = new Adversary { Name = "Undo crew " + Guid.NewGuid().ToString("N")[..6] };
                    db.Add(adversary);
                    db.Add(new ArticleAdversary { ArticleId = article, AdversaryId = adversary.Id });
                    break;
                case "ai-draft":
                    db.Add(new AiDraft { ArticleId = article, Provider = "test", Model = "test" });
                    break;
                case "review":
                    db.Add(new ArticleReview { ArticleId = article, RequestedById = "dev:robin", RequestedByName = "Robin", ReviewerId = "dev:sam", ReviewerName = "Sam" });
                    break;
                case "action":
                    var team = new Team { Name = "Undo team " + Guid.NewGuid().ToString("N")[..6] };
                    db.Add(team);
                    var number = await db.Actions.IgnoreQueryFilters().MaxAsync(a => (int?)a.Number) ?? 0;
                    db.Add(new ActionItem { Number = number + 1, Title = "Sweep for it", TeamId = team.Id, ArticleId = article });
                    break;
                case "screenshot":
                    await db.Articles.Where(a => a.Id == article).ExecuteUpdateAsync(u => u.SetProperty(a => a.ScreenshotsChangedAtUtc, DateTimeOffset.UtcNow));
                    break;
            }
            return await db.SaveChangesAsync();
        });

        (await promoter.UndoAsync(done.Undo!)).Should().BeFalse($"a {work} is someone's work on the draft");
        (await ArticleExists(article)).Should().BeTrue("their work stands");
        (await Item(ids[0])).Status.Should().Be(FeedItemStatus.Promoted);
    }

    [Fact]
    public async Task EveryTableThatPointsAtAnArticle_IsCountedByUndo()
    {
        // A table added later with a foreign key to Article is counted by the model, so Undo already sees it. This list
        // fails when one is added, for a look at whether the promote itself writes to it (then its count is in the
        // snapshot, and Undo still compares like with like).
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = Referrers.Keys<Article>(db.Model).Select(Referrers.Name).ToList();
        keys.Should().BeEquivalentTo(KnownArticleChildren, "a new table pointing at Article needs a look at Triage's promote and its Undo");
        Referrers.Keys<Brief>(db.Model).Should().NotBeEmpty();
    }

    // The tables pointing at Article as of this change. Undo counts them all from the model whatever this says.
    private static readonly string[] KnownArticleChildren =
    [
        "ActionItem.ArticleId", "AiDraft.ArticleId", "ArticleAdversary.ArticleId", "ArticleBullet.ArticleId", "ArticleCve.ArticleId",
        "ArticleIndicator.ArticleId", "ArticleLink.ArticleId", "ArticleProduct.ArticleId", "ArticleReview.ArticleId",
        "ArticleScreenshot.ArticleId", "ArticleTag.ArticleId", "ArticleTechnique.ArticleId", "BriefItem.ArticleId", "FeedItem.ArticleId",
    ];

    [Fact]
    public async Task Undo_OfAGroup_PutsEveryReportBack_AndDeletesTheOneDraft()
    {
        var ids = await Seed("group", 3, FeedItemStatus.New);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var promoter = scope.ServiceProvider.GetRequiredService<TriagePromoter>();
        var done = await promoter.PromoteAsync(ids, new PromoteForm());
        (await Read(db => db.ArticleLinks.CountAsync(l => l.ArticleId == done.Outcome.ArticleId))).Should().Be(2, "the other two reports are links on it");

        (await promoter.UndoAsync(done.Undo!)).Should().BeTrue("the links the promote itself made are its own");
        (await ArticleExists(done.Outcome.ArticleId)).Should().BeFalse();
        foreach (var id in ids)
        {
            var item = await Item(id);
            (item.Status, item.ArticleId, item.StatusByName).Should().Be((FeedItemStatus.New, (Guid?)null, (string?)null));
        }
    }

    [Fact]
    public async Task Undo_IsRefused_OnceTheBriefHasBeenSent()
    {
        var ids = await Seed("sent", 1);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var promoter = sp.GetRequiredService<TriagePromoter>();
        var done = await promoter.PromoteAsync([ids[0]], new PromoteForm { AddToBrief = true });
        var briefId = done.Undo!.BriefId!.Value;
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Briefs.Where(b => b.Id == briefId).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BriefStatus.Sent));
        try
        {
            (await promoter.UndoAsync(done.Undo)).Should().BeFalse("what was sent stays as it went");
            (await ArticleExists(done.Outcome.ArticleId)).Should().BeTrue();
            (await Read(d => d.BriefItems.AnyAsync(i => i.ArticleId == done.Outcome.ArticleId))).Should().BeTrue();
        }
        finally
        {
            await db.Briefs.Where(b => b.Id == briefId).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BriefStatus.Draft));
        }
    }

    [Fact]
    public async Task Undo_RemovesABriefThePromoteStarted_AndKeepsOneThatWasThere()
    {
        var ids = await Seed("brief-start", 2);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var promoter = sp.GetRequiredService<TriagePromoter>();
        var day = sp.GetRequiredService<BriefService>().CurrentDate;
        var db = sp.GetRequiredService<AppDbContext>();
        if (await db.Briefs.Include(b => b.Items).FirstOrDefaultAsync(b => b.BriefDate == day) is { } had)
        {
            db.BriefItems.RemoveRange(had.Items);
            db.Briefs.Remove(had);
            await db.SaveChangesAsync();
        }

        // Started by the promote, and nothing else since: Undo leaves no empty brief behind.
        var first = await promoter.PromoteAsync([ids[0]], new PromoteForm { AddToBrief = true });
        first.Undo!.StartedBriefVersion.Should().NotBeNull();
        (await promoter.UndoAsync(first.Undo)).Should().BeTrue();
        (await Read(d => d.Briefs.AnyAsync(b => b.BriefDate == day))).Should().BeFalse("the brief the promote started goes with it");

        // There already (started by the first of two promotes, changed by the second): Undo of the second keeps it.
        var started = await promoter.PromoteAsync([ids[0]], new PromoteForm { AddToBrief = true });
        var second = await promoter.PromoteAsync([ids[1]], new PromoteForm { AddToBrief = true });
        second.Undo!.StartedBriefVersion.Should().BeNull("the brief was there already");
        (await promoter.UndoAsync(second.Undo)).Should().BeTrue();
        (await Read(d => d.Briefs.AnyAsync(b => b.BriefDate == day))).Should().BeTrue();
        // And the first's Undo now finds its brief changed since (the second came and went): the brief stays.
        (await promoter.UndoAsync(started.Undo!)).Should().BeTrue();
        (await Read(d => d.Briefs.AnyAsync(b => b.BriefDate == day))).Should().BeTrue("someone else changed it since it was started");
    }

    [Fact]
    public async Task AnUndoPressedWhileAnotherStepSaves_WaitsForIt_RatherThanBeingDropped()
    {
        var ids = await Seed("busy", 2);
        await using var r = await RenderAsync<Triage>(app, "/triage?q=undo-busy");
        await r.HtmlOnce("undo-busy report 1");
        await r.Click(t => t.OnTriageKey("promote"));
        await r.Click(t => t.SubmitPromote(stay: true));
        var toasts = r.Scope.ServiceProvider.GetRequiredService<ToastService>();
        var toast = toasts.Toasts[^1];

        // Another step is being saved as Undo is pressed.
        var busy = typeof(Triage).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await r.Renderer.Dispatcher.InvokeAsync(() => busy.SetValue(r.Component, true));
        await r.Press(_ => toasts.UndoAsync(toast.Id));
        await Task.Delay(400);
        (await Item(ids[0])).Status.Should().Be(FeedItemStatus.Promoted, "it waits for the other step");
        await r.Renderer.Dispatcher.InvokeAsync(() => busy.SetValue(r.Component, false));

        (await r.Until(_ => toasts.Toasts.Any(t => t.Message.StartsWith("Promote undone", StringComparison.Ordinal)))).Should().BeTrue();
        (await Item(ids[0])).Status.Should().Be(FeedItemStatus.New);
    }

    [Theory]
    [InlineData("/articles/1", true)]
    [InlineData("/triage?tab=new", true)]
    [InlineData("articles/1", false)]
    [InlineData("//evil.example/x", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("https://evil.example/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/", false)]
    [InlineData("/a b", false)]
    [InlineData("", false)]
    public void AToastLinksOnlyToAPageOfThisApp(string href, bool allowed)
    {
        ToastService.ToastLink.IsAppPath(href).Should().Be(allowed);
        var make = () => new ToastService.ToastLink(href, "Write it");
        if (allowed) make().Relative.Should().Be(href[1..], "drawn against the app's base");
        else make.Should().Throw<ArgumentException>();
    }
}
