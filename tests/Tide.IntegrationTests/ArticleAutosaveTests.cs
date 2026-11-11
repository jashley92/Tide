using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.History;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Autosave in the article editor, and the three things it must not break. Concurrency: every autosave is checked against
/// the edit version, as Save is, and a colleague's save stops it, never overwritten. Markings: a change to a TLP marking,
/// or one that would put a part into the company-wide edition, is never autosaved, even with other edits pending; it waits
/// for Save. Audit and history: each autosave is a save through the one save path, audited as itself and marked as
/// automatic; the history shows one editing session's autosaves as one entry; an article on a sent brief is never
/// autosaved. It's off for someone who can't edit.
/// </summary>
public class ArticleAutosaveTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Colleague = new("dev:autosave-colleague", "Sam Colleague", "Analyst");

    private async Task<Guid> NewArticle(Action<Article>? fill = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var article = new Article
        {
            Title = "Autosave check", AuthorId = "x", AuthorName = "X", Tlp = TlpLevel.Clear, Bluf = "Exploited in the wild.",
            WhyItMattersTlp = TlpLevel.Amber,
        };
        article.Bullets.Add(new ArticleBullet { Text = "One", Tlp = TlpLevel.Clear });
        article.Bullets.Add(new ArticleBullet { Text = "Two", Tlp = TlpLevel.Amber, SortOrder = 1 });
        fill?.Invoke(article);
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return article.Id;
    }

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    private Task<Article> Saved(Guid id) => As(async sp => (await sp.GetRequiredService<ArticleService>().GetAsync(id))!);

    private async Task<ArticleForm> Form(Guid id) => ArticleForm.From(await Saved(id));

    private Task<AutosaveResult> Autosave(ArticleForm form, SignIn? who = null) => As(sp => sp.GetRequiredService<ArticleService>().AutosaveAsync(form), who);

    // ---- The service: what may be saved without asking ----

    [Fact]
    public async Task AnEditToText_IsSaved_ThroughTheSavePath_AndMovesTheEditVersion()
    {
        var id = await NewArticle();
        var form = await Form(id);
        form.Bluf = "Exploited in the wild since Monday.";
        form.Bullets[0].Text = "One, rewritten.";

        var result = await Autosave(form);

        result.Hold.Should().Be(AutosaveHold.None);
        result.EditVersion.Should().NotBeNull().And.NotBe(form.EditVersion);
        var saved = await Saved(id);
        (saved.Bluf, saved.Bullets.OrderBy(b => b.SortOrder).First().Text).Should().Be(("Exploited in the wild since Monday.", "One, rewritten."));
        saved.EditVersion.Should().Be(result.EditVersion!.Value);
    }

    [Fact]
    public async Task AColleaguesSave_StopsAutosave_AndIsNeverOverwritten()
    {
        var id = await NewArticle();
        var mine = await Form(id);
        var theirs = await Form(id);
        theirs.Bluf = "Sam's bottom line.";
        await As(sp => sp.GetRequiredService<ArticleService>().SaveAsync(theirs), Colleague);

        mine.Bluf = "My bottom line.";
        var autosave = () => Autosave(mine);

        await autosave.Should().ThrowAsync<StaleEditException>();
        (await Saved(id)).Bluf.Should().Be("Sam's bottom line.");
    }

    // A dropped connection can't make one edit save twice: a save repeated from the same copy (the reply to the first
    // lost, say) is refused as stale, since the first moved the edit version.
    [Fact]
    public async Task TheSameCopy_AutosavedTwice_IsSavedOnce()
    {
        var id = await NewArticle();
        var form = await Form(id);
        form.Bluf = "Typed before the connection dropped.";
        var first = await Autosave(form);
        var audit = await As(async sp => await sp.GetRequiredService<AppDbContext>().AuditLog.CountAsync(e => e.EntityKey == id.ToString()));

        var again = () => Autosave(form);

        await again.Should().ThrowAsync<StaleEditException>();
        (await Saved(id)).EditVersion.Should().Be(first.EditVersion!.Value);
        (await As(async sp => await sp.GetRequiredService<AppDbContext>().AuditLog.CountAsync(e => e.EntityKey == id.ToString()))).Should().Be(audit);
    }

    public static TheoryData<string> MarkingChanges => ["article", "bullet", "why"];

    [Theory]
    [MemberData(nameof(MarkingChanges))]
    public async Task AChangedMarking_IsNeverAutosaved_EvenWithOtherEditsPending(string which)
    {
        var id = await NewArticle(a => a.WhyItMatters = "We run it.");
        var form = await Form(id);
        var before = await Saved(id);
        form.Bluf = "A text edit alongside it.";
        switch (which)
        {
            case "article": form.SetTlp(TlpLevel.Green); break;
            case "bullet": form.Bullets[1].Tlp = TlpLevel.Clear; break;   // lowered: the bullet would go to everyone
            default: form.WhyItMattersTlp = TlpLevel.Red; break;            // raised: still a marking, still waits
        }

        var result = await Autosave(form);

        result.Should().Be(new AutosaveResult(null, AutosaveHold.Markings));
        var saved = await Saved(id);
        (saved.EditVersion, saved.Bluf, saved.Tlp, saved.WhyItMattersTlp).Should().Be((before.EditVersion, before.Bluf, before.Tlp, before.WhyItMattersTlp));
    }

    public static TheoryData<string> Widenings => ["new public bullet", "why written", "bottom line written"];

    [Theory]
    [MemberData(nameof(Widenings))]
    public async Task AnEditThatPutsAPartIntoTheCompanyWideEdition_WaitsForSave(string which)
    {
        // A CLEAR threat with "Why it matters" at CLEAR but empty, and no bottom line yet.
        var id = await NewArticle(a => { a.Classification = Classification.Threat; a.WhyItMattersTlp = TlpLevel.Clear; a.Bluf = ""; });
        var form = await Form(id);
        switch (which)
        {
            case "new public bullet": form.Bullets.Add(new BulletForm { Text = "A third, for everyone.", Tlp = TlpLevel.Clear }); break;
            case "why written": form.WhyItMatters = "Our gateways run it."; break;
            default: form.Bluf = "Now with a bottom line."; break;
        }

        (await Autosave(form)).Hold.Should().Be(AutosaveHold.Markings);
        (await Saved(id)).EditVersion.Should().Be(form.EditVersion);
    }

    [Fact]
    public async Task NarrowingWhatGoesOut_IsAutosaved_AsAnyTextEditIs()
    {
        var id = await NewArticle();
        var form = await Form(id);
        form.Bullets.RemoveAt(0);   // the CLEAR bullet goes: fewer parts reach everyone

        (await Autosave(form)).Hold.Should().Be(AutosaveHold.None);
        (await Saved(id)).Bullets.Select(b => b.Text).Should().Equal("Two");
    }

    [Fact]
    public async Task AnArticleOnASentBrief_IsNeverAutosaved()
    {
        var id = await NewArticle();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var brief = new Brief { BriefDate = new DateOnly(2020, 1, 6), Status = BriefStatus.Sent };
            brief.Items.Add(new BriefItem { ArticleId = id });
            db.Briefs.Add(brief);
            await db.SaveChangesAsync();
        }
        var form = await Form(id);
        form.Bluf = "A correction typed in.";

        (await Autosave(form)).Should().Be(new AutosaveResult(null, AutosaveHold.SentBrief));
        (await Saved(id)).Bluf.Should().Be("Exploited in the wild.");
    }

    [Fact]
    public async Task SomeoneWhoCantEdit_IsRefused()
    {
        var id = await NewArticle();
        var form = await Form(id);
        form.Bluf = "Not theirs to write.";

        var autosave = () => Autosave(form, new SignIn("dev:autosave-viewer", "Viewer", "Viewer"));

        await autosave.Should().ThrowAsync<ForbiddenException>();
    }

    // ---- Audit and history ----

    [Fact]
    public async Task EachAutosave_IsAuditedAsItself_AndTheHistoryShowsTheSessionAsOneEntry()
    {
        var id = await NewArticle();
        var form = await Form(id);
        foreach (var bluf in new[] { "First autosave.", "Second autosave.", "Third autosave." })
        {
            form.Bluf = bluf;
            form.EditVersion = (await Autosave(form)).EditVersion!.Value;
        }

        using (var scope = app.Services.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLog.AsNoTracking()
                .Where(e => e.EntityType == nameof(Article) && e.EntityKey == id.ToString() && e.Action == "Modified").ToListAsync();
            rows.Should().HaveCount(3, "the audit log keeps every save");
            rows.Should().OnlyContain(r => r.ChangesJson!.Contains("\"Autosaved\""), "each says it was saved automatically");
        }

        var history = await As(sp => sp.GetRequiredService<HistoryService>().ArticleAsync(id));
        var session = history.Entries.Should().ContainSingle(e => e.What.StartsWith("Autosaved")).Subject;
        session.What.Should().Be("Autosaved 3 times");
        session.Changes.Should().ContainSingle(c => c.Field == "Bottom line").Which.Should()
            .Be(new HistoryChange("Bottom line", "Exploited in the wild.", "Third autosave."), "from before the first to after the last");

        // An explicit Save afterwards is its own entry, as it always was.
        form.Bluf = "Saved by hand.";
        await As(sp => sp.GetRequiredService<ArticleService>().SaveAsync(form));
        history = await As(sp => sp.GetRequiredService<HistoryService>().ArticleAsync(id));
        history.Entries.First().What.Should().Be("Changed");
        history.Entries.Count(e => e.What.StartsWith("Autosaved")).Should().Be(1);
    }

    [Fact]
    public async Task AColleaguesSave_InTheMiddle_EndsTheSession()
    {
        var id = await NewArticle();
        var form = await Form(id);
        form.Bluf = "Mine, first.";
        await Autosave(form);
        var theirs = await Form(id);
        theirs.Title = "Sam's title";
        await As(sp => sp.GetRequiredService<ArticleService>().AutosaveAsync(theirs), Colleague);
        form = await Form(id);
        form.Bluf = "Mine, after Sam.";
        await Autosave(form);

        var history = await As(sp => sp.GetRequiredService<HistoryService>().ArticleAsync(id));
        history.Entries.Where(e => e.What.StartsWith("Autosaved")).Select(e => e.UserName).Should().Equal("Tester", "Sam Colleague", "Tester");
    }

    // ---- The editor ----

    private Task<Rendered<ArticleEditorPanel>> Editor(Guid id, SignIn? who = null) =>
        RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id }, who);

    private static string Bar(string html)
    {
        var bar = html[html.IndexOf("class=\"tide-sticky-save", StringComparison.Ordinal)..];
        return bar[..bar.IndexOf("</div>", StringComparison.Ordinal)];
    }

    [Fact]
    public async Task TheEditor_AutosavesAPause_SaysWhen_AndHasNothingLeftToAskAboutOnLeaving()
    {
        var id = await NewArticle();
        await using var r = await Editor(id);
        await r.Click(p => p.MentionAsync("We run it."));   // an edit, to "Why it matters" (already AMBER: nothing widens)
        Bar(await r.Html()).Should().Contain("Unsaved changes");

        await r.Click(p => p.AutosaveNowAsync());

        var bar = Bar(await r.Html());
        bar.Should().Contain("tide-autosave").And.Contain("Saved at ").And.NotContain("Unsaved changes");
        (await Saved(id)).WhyItMatters.Should().Be("We run it.");
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.MayLeave())).Should().BeTrue("nothing is left unsaved, so leaving asks nothing");
    }

    [Fact]
    public async Task TheEditor_HoldsAMarkingChange_ForSave_AndSaysSo()
    {
        var id = await NewArticle(a => a.WhyItMattersTlp = TlpLevel.Clear);
        await using var r = await Editor(id);
        await r.Click(p => p.MentionAsync("We run it."));                // a text edit
        await r.Click(p => p.FixAsync(ArticleFix.WhyItMattersAmber));   // and a marking: CLEAR to AMBER

        await r.Click(p => p.AutosaveNowAsync());

        var bar = Bar(await r.Html());
        bar.Should().Contain("Unsaved changes").And.Contain("A marking changed, or something now reaches everyone: Save to keep it.");
        (await Saved(id)).WhyItMatters.Should().BeNull("nothing autosaves until Save, the text edit included");
        r.Component.Dirty.Should().BeTrue("so leaving still asks");
    }

    [Fact]
    public async Task TheEditor_StopsWhenAColleagueSaved_KeepingWhatWasTyped()
    {
        var id = await NewArticle();
        await using var r = await Editor(id);
        await r.Click(p => p.MentionAsync("My paragraph."));
        var theirs = await Form(id);
        theirs.Bluf = "Sam's bottom line.";
        await As(sp => sp.GetRequiredService<ArticleService>().SaveAsync(theirs), Colleague);

        await r.Click(p => p.AutosaveNowAsync());
        await r.Click(p => p.MentionAsync("And more after."));
        await r.Click(p => p.AutosaveNowAsync());   // stopped: it doesn't try again

        var html = await r.Html();
        Bar(html).Should().Contain("Not saved automatically: someone else saved this article.");
        html.Should().Contain("My paragraph.&#xA;And more after.</textarea>", "the typing stays in the form");
        (await Saved(id)).Bluf.Should().Be("Sam's bottom line.");
        r.Component.Dirty.Should().BeTrue();
    }

    [Fact]
    public async Task TheEditor_NeverAutosaves_ForSomeoneWhoCantEdit()
    {
        var id = await NewArticle();
        var version = (await Saved(id)).EditVersion;
        await using var r = await Editor(id, new SignIn("dev:autosave-owner", "Owner", "ActionOwner"));

        await r.Click(p => p.AutosaveNowAsync());

        (await Saved(id)).EditVersion.Should().Be(version);
        (await r.Html()).Should().NotContain("tide-autosave");
    }
}
