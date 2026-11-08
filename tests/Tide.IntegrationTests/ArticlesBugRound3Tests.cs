using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Api;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Bug round 3 (articles, intake): proposals through the API to a retired source, at once from a new caller, or of the
/// same address twice at once; a prune racing a colleague's promotion; an API source "polled".
/// </summary>
public class ArticlesBugRound3Tests : IDisposable
{
    private TestDb _db = new();

    public ArticlesBugRound3Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private Task<Guid?> Propose(string url, string title = "Suspicious sign-ins") => _db.Api().ProposeAsync(new ApiProposal(title, url, null));

    private async Task<List<Source>> Sources()
    {
        await using var ctx = _db.Context();
        return await ctx.Sources.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task AProposalToARetiredApiSource_IsRefused_RatherThanHiddenFromTheInbox()
    {
        // R3-1: an administrator retired the caller's source; the API still answered 201 for each proposal, and every item
        // landed under the retired source, which the New tab leaves out: proposals nobody would ever see.
        _db.User.DisplayName = "SOAR";
        (await Propose("https://soar.example.org/cases/1")).Should().NotBeNull();
        var source = (await Sources()).Single();
        await _db.Intake().SetSourceRetiredAsync(source.Id, true);

        await FluentActions.Invoking(() => Propose("https://soar.example.org/cases/2")).Should().ThrowAsync<InvalidOperationException>().WithMessage("*retired*");

        await using var ctx = _db.Context();
        (await ctx.FeedItems.CountAsync()).Should().Be(1, "nothing was taken in under the retired source");
        await _db.Intake().SetSourceRetiredAsync(source.Id, false);
        (await Propose("https://soar.example.org/cases/2")).Should().NotBeNull("brought back, it takes proposals again");
    }

    [Fact]
    public async Task ACallersNameInAnotherCase_IsTheSameSource()
    {
        // R3-2: the source was found with ==, case-sensitive on SQLite, so "soar" made a second source beside "SOAR"
        // (SQL Server's collation found the first).
        _db.User.DisplayName = "SOAR";
        await Propose("https://soar.example.org/cases/1");
        _db.User.DisplayName = "soar";
        await Propose("https://soar.example.org/cases/2");

        (await Sources()).Should().ContainSingle().Which.Name.Should().Be("SOAR (API)");
    }

    [Fact]
    public async Task TwoFirstProposalsAtOnce_ShareTheCallersSource()
    {
        // R3-3: a new caller's first proposals, sent together, each found no source and made one; every one but the first
        // failed on the unique name (a 500), and its item was lost.
        _db.User.DisplayName = "SOAR";
        Guid theirs = Guid.Empty;
        _db.BeforeSave = saving =>
        {
            if (!saving.ChangeTracker.Entries<Source>().Any(e => e.State == EntityState.Added)) return;
            _db.BeforeSave = null;
            using var ctx = _db.PlainContext();
            var s = new Source { Name = "SOAR (API)", Kind = SourceKind.Api, Url = "https://api.invalid/SOAR", Enabled = false, PollMinutes = 1440 };
            ctx.Sources.Add(s);
            ctx.SaveChanges();
            theirs = s.Id;
        };

        var id = await Propose("https://soar.example.org/cases/1");

        id.Should().NotBeNull();
        (await Sources()).Should().ContainSingle().Which.Id.Should().Be(theirs);
        await using var check = _db.Context();
        (await check.FeedItems.SingleAsync()).SourceId.Should().Be(theirs);
    }

    [Fact]
    public async Task TheSameAddressProposedTwiceAtOnce_IsOneItem_AndTheSecondIsTold()
    {
        // R3-4: both calls found the address new; the second insert failed on the unique address (a 500), where a repeat
        // proposal is answered "already there".
        _db.User.DisplayName = "SOAR";
        await Propose("https://soar.example.org/cases/0");   // the source exists
        var source = (await Sources()).Single().Id;
        const string url = "https://soar.example.org/cases/1";
        _db.BeforeSave = saving =>
        {
            if (!saving.ChangeTracker.Entries<FeedItem>().Any(e => e.State == EntityState.Added)) return;
            _db.BeforeSave = null;
            using var ctx = _db.PlainContext();
            ctx.FeedItems.Add(new FeedItem
            {
                SourceId = source, Title = "From the retry", Url = url, UrlHash = UrlCanonicalizer.Hash(UrlCanonicalizer.Canonicalize(url)!),
                FetchedAtUtc = DateTimeOffset.UtcNow,
            });
            ctx.SaveChanges();
        };

        (await Propose(url)).Should().BeNull("the other call has it");

        await using var check = _db.Context();
        (await check.FeedItems.CountAsync(i => i.Url == url)).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task APruneRacingAColleaguesPromotion_LeavesThePromotedItem(string provider)
    {
        // R3-5: the prune read the old new item, a colleague promoted it, and the prune deleted what it had read: the
        // article lost its item (its excerpt and kept text with it), or the promotion failed part way, leaving its draft.
        _db = _db.On(provider);
        var source = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "News", Url = "https://news.example.com/feed", PollMinutes = 30 });
        var old = DateTimeOffset.UtcNow.AddDays(-200);
        FeedItem racing, plain;
        await using (var ctx = _db.Context())
        {
            racing = new FeedItem { SourceId = source, Title = "Old but promoted", Url = "https://news.example.com/1", UrlHash = "r3-1", FetchedAtUtc = old };
            plain = new FeedItem { SourceId = source, Title = "Old and untouched", Url = "https://news.example.com/2", UrlHash = "r3-2", FetchedAtUtc = old };
            ctx.FeedItems.AddRange(racing, plain);
            ctx.FeedItemTexts.Add(new FeedItemText { FeedItemId = plain.Id, Text = "kept text", How = "auto", FetchedAtUtc = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
        }
        var article = await _db.Articles().CreateAsync("Robin's article");
        _db.Commands.Before(sql => sql.Contains("DELETE", StringComparison.Ordinal) && sql.Contains("FeedItems", StringComparison.Ordinal), () =>
        {
            using var ctx = _db.PlainContext();
            ctx.FeedItems.Where(i => i.Id == racing.Id).ExecuteUpdate(u => u.SetProperty(i => i.Status, FeedItemStatus.Promoted).SetProperty(i => i.ArticleId, article));
        });

        (await _db.Intake().PruneAsync()).Should().Be(1);

        await using var check = _db.Context();
        var left = await check.FeedItems.AsNoTracking().SingleAsync();
        (left.Id, left.Status, left.ArticleId).Should().Be((racing.Id, FeedItemStatus.Promoted, (Guid?)article));
        (await check.FeedItemTexts.AnyAsync()).Should().BeFalse("the pruned item's kept text went with it");
    }

    [Fact]
    public async Task AnApiSource_IsNeverPolled()
    {
        // R3-6: "Poll now" on an API source fetched its placeholder address and recorded a failure on it; ticked "Poll it",
        // every pass did.
        _db.User.DisplayName = "SOAR";
        await Propose("https://soar.example.org/cases/1");
        var source = (await Sources()).Single();
        await using (var ctx = _db.Context())
        {
            await ctx.Sources.Where(s => s.Id == source.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.Enabled, true));
        }

        await FluentActions.Invoking(() => _db.Intake().PollNowAsync(source.Id)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*API*");
        await _db.Intake().PollDueAsync();

        _db.Http.Requests.Should().BeEmpty();
        var after = (await Sources()).Single();
        (after.LastPolledAtUtc, after.LastError, after.ConsecutiveFailures).Should().Be(((DateTimeOffset?)null, (string?)null, 0));
    }
}
