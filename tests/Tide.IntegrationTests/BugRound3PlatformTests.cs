using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tide.Application.Intake;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Security;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>Bug round 3, platform: bundle keys in another case, the prune retried, a token caller's name.</summary>
public class BugRound3PlatformTests : IDisposable
{
    private readonly TestDb _db = new();

    public BugRound3PlatformTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private static ConfigBundle Bundle(string scorecard = "[]", string queryTemplates = "[]") => ConfigBundle.Parse(Encoding.UTF8.GetBytes($$"""
        {
          "format": "{{ConfigBundle.FormatName}}", "version": {{ConfigBundle.CurrentVersion}},
          "exportedAtUtc": "2026-11-01T00:00:00Z", "exportedBy": "x", "appVersion": "x",
          "settings": [], "roleMappings": [], "sources": [], "scorecard": {{scorecard}}, "queryTemplates": {{queryTemplates}}, "holidays": []
        }
        """));

    private ConfigBundleService Bundles() => new(_db, _db.User, new CountingRoleDirectory(), TimeProvider.System);

    // A hand-edited "KQL" was found as a dialect ignoring case but matched to the saved rows and stored as typed: beside the
    // app's "kql" row SQL Server's key refused it (the whole import failed), and SQLite kept a row the hunts never read,
    // while the preview said it was added.
    [Fact]
    public async Task ABundleQueryTemplateInAnotherCase_UpdatesTheAppsRow()
    {
        await using (var ctx = _db.Context())
        {
            ctx.QueryTemplates.Add(new QueryTemplate { Dialect = "kql", Kind = HuntValueKind.Domain, Template = "old {{values}}", UpdatedBy = "x" });
            await ctx.SaveChangesAsync();
        }

        var done = await Bundles().PreviewAndImportAsync(Bundle(queryTemplates: """[ { "dialect": "KQL", "kind": "domain", "template": "new {{values}}" } ]"""));

        done.Changes.Should().ContainSingle(c => c.Area == "Query templates").Which.Kind.Should().Be(BundleChangeKind.Update);
        await using var check = _db.Context();
        (await check.QueryTemplates.AsNoTracking().ToListAsync()).Should().ContainSingle()
            .Which.Should().Match<QueryTemplate>(t => t.Dialect == "kql" && t.Template == "new {{values}}");
    }

    // The same for a scorecard key: "Threats" for the app's "threats" was a second category on SQLite, refused on SQL Server.
    [Fact]
    public async Task ABundleScorecardKeyInAnotherCase_UpdatesTheAppsCategoryAndTag()
    {
        await using (var ctx = _db.Context())
        {
            var cat = new ScorecardCategory { Key = "threats", Name = "Threats" };
            ctx.ScorecardCategories.Add(cat);
            ctx.ScorecardTags.Add(new ScorecardTag { Category = cat, Key = "phishing", Name = "Phishing" });
            await ctx.SaveChangesAsync();
        }
        var bundle = Bundle(scorecard: """
            [ { "key": "Threats", "name": "Threats seen", "sortOrder": 0, "isRetired": false,
                "tags": [ { "key": "PHISHING", "name": "Phishing lures", "sortOrder": 0, "isRetired": false } ] } ]
            """);

        (await Bundles().PreviewAsync(bundle)).Changes.Where(c => c.Area == "Scorecard").Select(c => c.Kind)
            .Should().Equal(BundleChangeKind.Update, BundleChangeKind.Update);
        await Bundles().PreviewAndImportAsync(bundle);

        await using var check = _db.Context();
        (await check.ScorecardCategories.AsNoTracking().ToListAsync()).Should().ContainSingle()
            .Which.Should().Match<ScorecardCategory>(c => c.Key == "threats" && c.Name == "Threats seen");
        (await check.ScorecardTags.AsNoTracking().ToListAsync()).Should().ContainSingle()
            .Which.Should().Match<ScorecardTag>(t => t.Key == "phishing" && t.Name == "Phishing lures");
    }

    // A prune that failed (the database busy a moment) was put off a day, though every other part is tried again at the
    // next pass; Diagnostics showed it due tomorrow.
    [Fact]
    public async Task AFailedPrune_IsTriedAgainAtTheNextPass()
    {
        using var rig = new SettingsRig(new() { ["Intake:Enabled"] = "false" });
        rig.Db.Commands.Before(sql => sql.Contains("\"FeedItems\"", StringComparison.Ordinal) && sql.Contains("\"FetchedAtUtc\" <", StringComparison.Ordinal),
            () => throw new InvalidOperationException("The database is busy."));
        var jobs = new JobMonitor(TimeProvider.System);
        var hosted = new IntakeHostedService(rig.Get<IServiceScopeFactory>(), new Tide.Application.Abstractions.ChangeFeed(), jobs, new HeldLease(),
            rig.Get<IOptionsMonitor<IntakeOptions>>(), rig.Get<IOptionsMonitor<CveOptions>>(), TimeProvider.System, NullLogger<IntakeHostedService>.Instance);

        await hosted.PassAsync(CancellationToken.None);
        jobs.Get(JobMonitor.Prune).LastSucceeded.Should().BeFalse();
        jobs.Get(JobMonitor.Prune).NextDueUtc.Should().BeBefore(DateTimeOffset.UtcNow.AddHours(1), "it's tried again at the next pass");

        await hosted.PassAsync(CancellationToken.None);
        jobs.Get(JobMonitor.Prune).LastSucceeded.Should().BeTrue();
        jobs.Get(JobMonitor.Prune).NextDueUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddHours(23));
    }

    // A token's caller is named as issued: "SOAR\Prod" was taken for a Windows account and looked up in AD on every call,
    // then named "Prod" (or whoever AD said), so its detection push no longer matched its revocation.
    [Fact]
    public void ATokenCallersName_IsTheNameItWasIssuedUnder()
    {
        var identity = new ClaimsIdentity(ApiTokenAuthenticationHandler.SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "api:" + Guid.NewGuid().ToString("N")));
        identity.AddClaim(new Claim(ClaimTypes.Name, @"SOAR\Prod"));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        http.Request.Path = "/api/v1/detections";

        new CurrentUser(new HttpContextAccessor { HttpContext = http }, new ServerAuthenticationStateProvider()).DisplayName.Should().Be(@"SOAR\Prod");
    }
}
