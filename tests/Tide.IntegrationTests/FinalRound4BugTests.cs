using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Metrics;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Security;
using Tide.Web.Security;

namespace Tide.IntegrationTests;

/// <summary>Bug round 4, final fixes: two first saves of a day's number, names in order on SQLite, a stand-in name kept out
/// of records.</summary>
public class FinalRound4BugTests : IDisposable
{
    private TestDb _db = new();
    private static readonly DateOnly Fri = new(2026, 10, 23);

    public FinalRound4BugTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // Two people saving the first value for a metric and day at the same moment both found no row and both added one: the
    // second hit the unique (metric, day) index and saw an error. Now it updates the row the first made.
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TwoFirstSavesAtOnce_TheSecondUpdatesTheFirstsRow_AndTheHistorySaysSo(string provider)
    {
        _db = _db.On(provider);
        var metrics = _db.Metrics();
        var id = await metrics.SaveMetricAsync(new MetricForm { GroupId = await metrics.AddGroupAsync("Alert queue"), Name = "Open alerts" });
        _db.BeforeSave = saving =>
        {
            if (!saving.ChangeTracker.Entries<MetricValue>().Any(e => e.State == EntityState.Added)) return;
            _db.BeforeSave = null;
            // A colleague's first save lands in this moment, recorded as theirs would be.
            using var colleague = _db.Context();
            colleague.MetricValues.Add(new MetricValue { DefinitionId = id, BriefDate = Fri, Value = 4, Source = MetricSource.Manual, EnteredById = "dev:robin", EnteredByName = "Robin" });
            colleague.SaveChanges();
        };

        await metrics.SaveValueAsync(id, Fri, 7, "after the colleague");

        await using var check = _db.Context();
        var row = await check.MetricValues.SingleAsync(v => v.DefinitionId == id && v.BriefDate == Fri);
        (row.Value, row.Note, row.EnteredByName).Should().Be((7m, "after the colleague", "Test Analyst"));
        var history = await check.AuditLog.Where(e => e.EntityType == nameof(MetricValue)).Select(e => e.Action).ToListAsync();
        history.Should().BeEquivalentTo(["Added", "Modified"], "the colleague's add, then this save's change of it; the failed add was never saved");
    }

    [Fact]
    public async Task ClearingAValueAColleagueNeverSaved_StillDoesNothing()
    {
        var metrics = _db.Metrics();
        var id = await metrics.SaveMetricAsync(new MetricForm { GroupId = await metrics.AddGroupAsync("Alert queue"), Name = "Open alerts" });

        await metrics.SaveValueAsync(id, Fri, null, null);

        await using var check = _db.Context();
        (await check.MetricValues.AnyAsync()).Should().BeFalse();
    }

    // SQLite's own order is case-sensitive: a plain OrderBy(Name) put "citrix NetScaler" after "Zscaler". Every list of
    // names sorts ignoring case, as on SQL Server; the editor's card and present mode's chips the same way.
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task OurStack_TheEditorsCard_AndThePresentChips_ListNamesIgnoringCase_OnEitherProvider(string provider)
    {
        _db = _db.On(provider);
        var stack = _db.Stack();
        var article = await _db.Articles().CreateAsync("Edge devices under attack");
        foreach (var name in new[] { "Zscaler", "citrix NetScaler", "Fortinet FortiGate", "Ivanti" })
            await stack.TagArticleAsync(article, await stack.SaveAsync(new StackForm { Name = name }));

        string[] expected = ["citrix NetScaler", "Fortinet FortiGate", "Ivanti", "Zscaler"];
        (await stack.ListAsync()).Select(r => r.Product.Name).Should().Equal(expected, "SQLite put the capitals first");
        (await stack.ForArticleAsync(article)).Tagged.Select(p => p.Name).Should().Equal(expected, "the editor's card");
        (await stack.ForArticlesAsync([article]))[article].Select(p => p.Name).Should().Equal(expected, "present mode's chips");
    }

    [Fact]
    public async Task TheEditorsCard_PutsRetiredProductsLast_AsThePresentChipsDo()
    {
        var stack = _db.Stack();
        var article = await _db.Articles().CreateAsync("Edge devices under attack");
        var old = await stack.SaveAsync(new StackForm { Name = "Aruba" });
        await stack.TagArticleAsync(article, old);
        await stack.TagArticleAsync(article, await stack.SaveAsync(new StackForm { Name = "zyxel" }));
        await stack.SetRetiredAsync(old, true);

        (await stack.ForArticleAsync(article)).Tagged.Select(p => p.Name).Should().Equal("zyxel", "Aruba");
        (await stack.ForArticlesAsync([article]))[article].Select(p => p.Name).Should().Equal("zyxel", "Aruba");
    }

    // AD slow to answer: CurrentUser.DisplayName fell back to the bare username, and "jdoe" was stored as a comment's
    // author or an assignee's name. The user mirror's real name for them is used instead; with none, the username.
    [Fact]
    public void AStandInName_GivesWayToTheMirrorsName_AndIsKeptWithoutOne()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.Context());
        var mirror = new UserDirectory(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        using (var ctx = _db.Context())
        {
            ctx.Users.Add(new AppUser { Sid = "S-1-5-21-9-9-9-1001", DisplayName = "Jordan Rivera" });
            ctx.SaveChanges();
        }
        // A domain AD can't name (no such domain), so each lookup gives the bare username as a stand-in.
        var domain = "NODOMAIN" + Guid.NewGuid().ToString("N")[..6];

        Signed($"{domain}\\jdoe", "S-1-5-21-9-9-9-1001", mirror).DisplayName.Should().Be("Jordan Rivera");
        Signed($"{domain}\\newcomer", "S-1-5-21-9-9-9-1002", mirror).DisplayName.Should().Be("newcomer", "no name is recorded for them yet");
        Signed($"{domain}\\jdoe", "S-1-5-21-9-9-9-1001", users: null).DisplayName.Should().Be("jdoe", "with no mirror there's only the username");
        Signed("Dev Analyst", "dev:analyst", mirror).DisplayName.Should().Be("Dev Analyst", "a name that isn't DOMAIN\\sam is the name");
    }

    [Fact]
    public async Task TheMirrorsName_IsReadOnce_AndFollowsAChangeOfName()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.Context());
        var mirror = new UserDirectory(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        const string sid = "S-1-5-21-9-9-9-2001";
        mirror.RecordedName(sid).Should().BeNull("no one by that id has signed in");

        await mirror.TouchAsync(sid, "Jordan Rivera", null, null, "");
        var before = _db.Commands.Count;
        mirror.RecordedName(sid).Should().Be("Jordan Rivera");
        _db.Commands.Count.Should().Be(before, "the name written is kept in memory");

        await mirror.TouchAsync(sid, "Jordan Rivera-Lee", null, null, "");
        mirror.RecordedName(sid).Should().Be("Jordan Rivera-Lee");
    }

    private static CurrentUser Signed(string accountName, string sid, IUserDirectory? users)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, accountName), new Claim(ClaimTypes.NameIdentifier, sid)], "Negotiate");
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        return new CurrentUser(http, new NoAuthState(), users);
    }

    private sealed class NoAuthState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
