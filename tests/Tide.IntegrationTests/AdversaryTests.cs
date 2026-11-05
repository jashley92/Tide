using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Adversaries;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>EP-73: adversary profiles, their timelines, merging, MITRE's import and triage's mark.</summary>
public class AdversaryServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public AdversaryServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<Guid> Article(string title, string? actors = null, params string[] techniques)
    {
        var id = await _db.Articles().CreateAsync(title, "Vendor", "https://vendor.example.com/" + Guid.NewGuid().ToString("N"));
        var form = ArticleForm.From((await _db.Articles().GetAsync(id))!);
        form.TechniqueIds = techniques.ToList();
        await _db.Articles().SaveAsync(form);
        await _db.SetEarlierActorsAsync(id, actors);
        return id;
    }

    private Task<Guid> Spider() => _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Scattered Spider", Aliases = "UNC3944; Octo Tempest", MitreId = "g1015" });

    [Fact]
    public async Task AProfilesTimeline_HoldsWhatsTaggedAndWhatNamesIt_WithItsTechniques()
    {
        var spider = await Spider();
        var named = await Article("Help-desk calls reset MFA", "UNC3944", "T1566.004", "T1621");
        var tagged = await Article("Okta phishing kit reused", null, "T1621");
        await Article("Unrelated ransomware note", "LockBit");
        await _db.Adversaries().TagArticleAsync(tagged, spider);

        var d = (await _db.Adversaries().GetAsync(spider))!;

        d.Adversary.MitreId.Should().Be("G1015");
        d.Articles.Select(a => (a.Id, a.Tagged)).Should().BeEquivalentTo([(named, false), (tagged, true)]);
        d.Untagged.Select(a => a.Id).Should().Equal(named);
        d.Techniques[0].Should().Be(new AdversaryTechnique("T1621", "Multi-Factor Authentication Request Generation", 2));
        (await _db.Adversaries().LinkAsync(spider, [named])).Should().Be(1);
        (await _db.Adversaries().GetAsync(spider))!.Untagged.Should().BeEmpty();

        var layer = JsonDocument.Parse((await _db.Adversaries().NavigatorLayerAsync(spider))!).RootElement;
        layer.GetProperty("domain").GetString().Should().Be("enterprise-attack");
        layer.GetProperty("techniques").EnumerateArray().Select(t => (t.GetProperty("techniqueID").GetString(), t.GetProperty("score").GetInt32()))
            .Should().Contain(("T1621", 2));
    }

    [Fact]
    public async Task AnArticle_IsOfferedTheProfilesItNames_AndAnAliasFindsItsProfile()
    {
        var spider = await Spider();
        var article = await Article("Retailer breached", "Octo Tempest");
        var titled = await Article("Octo Tempest breaches a retailer");

        // The earlier free-text note's name is offered from the note; a title's, as "Names X".
        (await _db.Adversaries().ForArticleAsync(article)).FromNote.Select(a => a.Id).Should().Equal(spider);
        (await _db.Adversaries().ForArticleAsync(article)).Suggested.Should().BeEmpty("a profile is offered once");
        (await _db.Adversaries().ForArticleAsync(titled)).Suggested.Select(a => a.Id).Should().Equal(spider);
        (await _db.Adversaries().AddByNameAsync("octo tempest")).Should().Be(spider);
        var added = await _db.Adversaries().AddByNameAsync("Muddled Libra");
        added.Should().NotBe(spider);
        await FluentActions.Invoking(() => _db.Adversaries().SaveAsync(new AdversaryForm { Name = "x", MitreId = "T1190" })).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Merging_MovesTagsAndNames_AndRetiresTheOther()
    {
        var spider = await Spider();
        var libra = await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Muddled Libra", Aliases = "Starfraud", Watched = true });
        var article = await Article("SIM swaps");
        await _db.Adversaries().TagArticleAsync(article, libra);

        await _db.Adversaries().MergeAsync(libra, spider);

        var d = (await _db.Adversaries().GetAsync(spider))!;
        d.Adversary.Aliases.Should().Be("UNC3944; Octo Tempest; Muddled Libra; Starfraud");
        d.Adversary.Watched.Should().BeTrue();
        d.Articles.Single().Id.Should().Be(article);
        var gone = (await _db.Adversaries().GetAsync(libra))!;
        (gone.Adversary.IsRetired, gone.Adversary.MergedIntoId, gone.MergedIntoName).Should().Be((true, spider, "Scattered Spider"));
    }

    [Fact]
    public async Task MitresGroups_AddProfiles_AndGiveExistingOnesTheirNames()
    {
        await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "UNC3944", Notes = "Ours." });
        var bundle = """
            { "type": "bundle", "objects": [
              { "type": "intrusion-set", "name": "Scattered Spider", "aliases": ["Scattered Spider", "Roasted 0ktapus", "Octo Tempest", "UNC3944"],
                "external_references": [ { "source_name": "mitre-attack", "external_id": "G1015" } ] },
              { "type": "intrusion-set", "name": "APT28", "aliases": ["APT28", "Fancy Bear"], "external_references": [ { "source_name": "mitre-attack", "external_id": "G0007" } ] },
              { "type": "intrusion-set", "name": "Old group", "revoked": true },
              { "type": "campaign", "name": "C0027", "external_references": [ { "source_name": "mitre-attack", "external_id": "C0027" } ] },
              { "type": "attack-pattern", "name": "Phishing" } ] }
            """;

        var first = await _db.Adversaries().ImportMitreAsync(new MemoryStream(Encoding.UTF8.GetBytes(bundle)));
        var again = await _db.Adversaries().ImportMitreAsync(new MemoryStream(Encoding.UTF8.GetBytes(bundle)));

        first.Should().Be(new MitreImport(Added: 2, Updated: 1, Skipped: 1));
        again.Should().Be(new MitreImport(0, 0, 1));
        var all = await _db.Adversaries().ListAsync(includeRetired: true);
        var ours = all.Single(r => r.Adversary.Name == "UNC3944").Adversary;
        (ours.MitreId, ours.Notes).Should().Be(("G1015", "Ours."));
        ours.Aliases.Should().Be("Scattered Spider; Roasted 0ktapus; Octo Tempest");
        all.Single(r => r.Adversary.MitreId == "C0027").Adversary.Kind.Should().Be(AdversaryKind.Campaign);
        await FluentActions.Invoking(() => _db.Adversaries().ImportMitreAsync(new MemoryStream("nope"u8.ToArray()))).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MitresGroups_AreFetchedFromItsTaxiiServer_WithTheTaxiiAcceptHeader()
    {
        _db.Http.Set(AdversaryService.MitreTaxiiUrl, """{ "objects": [ { "type": "intrusion-set", "name": "APT29", "aliases": ["APT29", "Cozy Bear"], "external_references": [ { "source_name": "mitre-attack", "external_id": "G0016" } ] } ] }""");

        (await _db.Adversaries().ImportFromMitreAsync()).Should().Be(new MitreImport(1, 0, 0));

        _db.Http.Requests.Single().Request!.Headers!["Accept"].Should().Be("application/taxii+json;version=2.1");
        _db.Http.Responses.Clear();
        await FluentActions.Invoking(() => _db.Adversaries().ImportFromMitreAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*upload the file instead*");
    }

    [Fact]
    public async Task Triage_MarksItemsThatNameAWatchedProfile()
    {
        var spider = await Spider();
        await _db.Adversaries().SetWatchedAsync(spider, true);
        var source = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "News", Url = "https://news.example.com/feed", PollMinutes = 30 });
        await using (var ctx = _db.Context())
        {
            ctx.FeedItems.Add(new FeedItem { SourceId = source, Title = "Octo Tempest hits another retailer", Url = "https://news.example.com/a", UrlHash = "a", FetchedAtUtc = DateTimeOffset.UtcNow });
            ctx.FeedItems.Add(new FeedItem { SourceId = source, Title = "Unrelated", Url = "https://news.example.com/b", UrlHash = "b", FetchedAtUtc = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
        }

        var rows = await _db.Intake().InboxAsync(new InboxFilter());

        rows.Single(r => r.Item.UrlHash == "a").Watching!.Name.Should().Be("Scattered Spider");
        rows.Single(r => r.Item.UrlHash == "b").Watching.Should().BeNull();
    }

    [Fact]
    public async Task ReadersDontSeeProfiles_AndOnlyAnalystsChangeThem()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        await FluentActions.Invoking(() => _db.Adversaries().SaveAsync(new AdversaryForm { Name = "x" })).Should().ThrowAsync<ForbiddenException>();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Invoking(() => _db.Adversaries().ListAsync()).Should().ThrowAsync<ForbiddenException>();
    }
}

/// <summary>The Adversaries page and the editor's card.</summary>
public class AdversaryPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheList_Pages100AtATime_WithAFilter_AndEveryProfileOnRequest()
    {
        var ids = new List<Guid>();
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var adversaries = scope.ServiceProvider.GetRequiredService<AdversaryService>();
            for (var i = 0; i < 130; i++) ids.Add(await adversaries.SaveAsync(new AdversaryForm { Name = $"paging-test Group {i:000}", MitreId = $"G9{i:000}" }));
        }
        try
        {
            await using var list = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.AdversariesPage>(app, "/adversaries");
            var html = await list.Html();
            html.Should().NotContain("paging-test Group", "a profile never reported or watched is shown on request")
                .And.Contain("Filter by name, other name or MITRE id");

            await list.Click(c => c.ToggleAll(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true }));
            html = System.Net.WebUtility.HtmlDecode(await list.Html());
            list.Component.Shown.Should().HaveCountGreaterThanOrEqualTo(130);
            html.Should().Contain($"1–100 of {list.Component.Shown!.Count:N0} profiles", "the rest are on the next page, not cut off")
                .And.Contain("aria-label=\"Pages of profiles\"").And.Contain("aria-label=\"Page 2\"");
        }
        finally
        {
            await using var scope = app.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<Tide.Application.Abstractions.IAppDbContextFactory>().CreateAsync();
            db.Adversaries.RemoveRange(db.Adversaries.Where(a => ids.Contains(a.Id)));
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task TheListAndAProfile_Render()
    {
        Guid id;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<AdversaryService>().SaveAsync(new AdversaryForm { Name = "page-test Spider", Aliases = "UNC-page", Watched = true });

        await using var list = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.AdversariesPage>(app, "/adversaries");
        (await list.Html()).Should().Contain("page-test Spider").And.Contain("Import MITRE ATT&amp;CK");

        await using var profile = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.AdversariesPage>(app, $"/adversaries/{id}", new() { ["Id"] = id });
        var html = await profile.Html();
        html.Should().Contain("Timeline").And.Contain("Techniques seen in our articles").And.Contain("Stop watching").And.Contain("Our notes (Markdown, internal)");
    }

    // CON-1: Robin's card on an article stays open while Sam tags it. Robin's card shows Sam's tag, and Robin's own tag
    // then adds to it rather than replacing it with the list the card first loaded.
    [Fact]
    public async Task AnOpenCard_ShowsAColleaguesTag_AndItsOwnTagKeepsIt()
    {
        Guid article, spider, libra;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var svc = scope.ServiceProvider.GetRequiredService<AdversaryService>();
            spider = await svc.SaveAsync(new AdversaryForm { Name = "card-test Spider" });
            libra = await svc.SaveAsync(new AdversaryForm { Name = "card-test Libra" });
            article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Card tagging", "Vendor", null, TlpLevel.Clear);
        }
        await using var robin = await ComponentHarness.RenderAsync<Tide.Web.Components.Shared.ArticleAdversaryCard>(app, $"/articles/{article}",
            new() { ["ArticleId"] = article, ["CanWrite"] = true });
        (await robin.Html()).Should().Contain("No adversary tagged.");

        await using (var sam = ComponentHarness.SignedIn(app, new ComponentHarness.SignIn("dev:sam", "Sam", "Analyst")))
            await sam.ServiceProvider.GetRequiredService<AdversaryService>().TagArticleAsync(article, spider);
        (await robin.HtmlOnce("Untag card-test Spider")).Should().Contain("Untag card-test Spider", "the card follows the article's tags");

        await robin.Click(c => c.Tag(libra));
        await robin.Click(c => c.Tag(libra));

        await using var check = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        (await check.ServiceProvider.GetRequiredService<AdversaryService>().ForArticleAsync(article)).Tagged.Select(a => a.Name)
            .Should().BeEquivalentTo(["card-test Spider", "card-test Libra"]);
    }
}
