using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Indicators;
using Tide.Application.Search;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.IntegrationTests;

/// <summary>
/// An indicator lookup finds rows by their stored keys (<see cref="ArticleIndicator.ValueKey"/>,
/// <see cref="ArticleIndicator.HostRev"/>, <see cref="ActionIndicator.ValueKey"/>), and finds rows saved before the keys
/// were kept the old way, by their value, until the background fill keys them: the same answer before, during and after.
/// </summary>
public class IndicatorKeyTests
{
    // As long as a search term can be (SearchText.MaxTermLength); a stored value past what SQL Server could index is
    // keyed the same way (MigrationFillTests).
    private static readonly string LongUrl = "https://evil.example.net/" + new string('a', SearchText.MaxTermLength - 25);

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task A_lookup_finds_the_same_before_the_keys_are_filled_while_and_after(string provider)
    {
        using var db = new TestDb(provider: provider);
        db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var exact = await Article(db, "C2 domain", (IndicatorType.Domain, "evil.example.net"));
        var sub = await Article(db, "Subdomain", (IndicatorType.Domain, "mail.cdn.evil.example.net"));
        var url = await Article(db, "Long URL", (IndicatorType.Url, LongUrl));
        var mail = await Article(db, "Lure sender", (IndicatorType.EmailAddress, "billing@evil.example.net"));
        await Article(db, "Lookalike", (IndicatorType.Domain, "notevil.example.net"), (IndicatorType.Domain, "evil.example.net.attacker.example"));
        var ip = await Article(db, "Address", (IndicatorType.IpAddress, "198.51.100.7"));
        await Article(db, "Another address", (IndicatorType.IpAddress, "198.51.100.70"));
        var team = await db.Teams().AddAsync("Cyber Defense", null, null);
        var own = await db.Actions().CreateAsync(new ActionForm { Title = "Sweep from a phone call", Kind = ActionKind.IocSweep, TeamId = team });
        await db.Actions().AddIndicatorsAsync((await db.Actions().GetAsync(own))!.Action.Id, [new IndicatorForm { Type = IndicatorType.Domain, Value = "evil.example.net" }]);

        async Task<string> Answers()
        {
            var search = new SearchService(db, db.User);
            var lines = new List<string>();
            foreach (var q in new[] { "EVIL[.]example.net", "198.51.100.7", LongUrl.ToUpperInvariant().Replace("HTTPS", "https"), "notevil.example.net" })
            {
                var ind = (await search.SearchAsync(q)).Indicator!;
                lines.Add($"{q[..Math.Min(q.Length, 30)]}: on {string.Join(",", ind.Sightings.Items.Where(s => !s.Related).Select(s => s.ArticleId).Order())}"
                    + $"; related {string.Join(",", ind.Sightings.Items.Where(s => s.Related).Select(s => s.ArticleId).Order())}"
                    + $"; actions {string.Join(",", ind.Actions.Items.Select(a => a.Number))}; total {ind.Sightings.Total}");
            }
            return string.Join("\n", lines);
        }

        var keyed = await Answers();
        keyed.Should().Contain($"EVIL[.]example.net: on {exact}; related {string.Join(",", new[] { sub, url, mail }.Order())}; actions {own}");
        keyed.Should().Contain($"198.51.100.7: on {ip}; related ; actions ;");
        keyed.Should().Contain($": on {url}; related", "a long value is found by its key, in either case");

        // As an upgrade leaves the rows saved before the keys were kept.
        await using (var ctx = db.Context())
        {
            await ctx.ArticleIndicators.ExecuteUpdateAsync(s => s.SetProperty(i => i.ValueKey, (string?)null).SetProperty(i => i.HostRev, (string?)null));
            await ctx.ActionIndicators.ExecuteUpdateAsync(s => s.SetProperty(i => i.ValueKey, (string?)null));
        }
        await using (var ctx = db.Context()) (await IndicatorKeyFill.DoneAsync(ctx)).Should().BeFalse();
        (await Answers()).Should().Be(keyed, "a row not yet keyed is found by its value");

        await using (var ctx = db.Context())
        {
            (await IndicatorKeyFill.FillAsync(ctx, batches: 1, batch: 3)).Should().Be((3, false));
            (await IndicatorKeyFill.LeftAsync(ctx)).Should().Be(6);
        }
        (await Answers()).Should().Be(keyed, "some rows keyed and some not");

        await using (var ctx = db.Context())
        {
            while (!(await IndicatorKeyFill.FillAsync(ctx, batches: 1, batch: 3)).Done) { }
            (await IndicatorKeyFill.DoneAsync(ctx)).Should().BeTrue();
            foreach (var i in await ctx.ArticleIndicators.AsNoTracking().ToListAsync())
                (i.ValueKey, i.HostRev).Should().Be((IndicatorKey.ValueOf(i.Value), IndicatorKey.HostRevOf(i.Type, i.Value)));
            (await ctx.ActionIndicators.AsNoTracking().SingleAsync()).ValueKey.Should().Be(IndicatorKey.ValueOf("evil.example.net"));
        }
        (await Answers()).Should().Be(keyed);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task The_fill_writes_only_the_keys_and_never_over_a_row_saved_meanwhile(string provider)
    {
        using var db = new TestDb(provider: provider);
        var id = await Article(db, "Keyed later", (IndicatorType.Domain, "first.example.net"));
        DateTimeOffset? modified;
        await using (var ctx = db.Context())
        {
            modified = (await ctx.Articles.AsNoTracking().SingleAsync(a => a.Id == id)).ModifiedAtUtc;
            await ctx.ArticleIndicators.ExecuteUpdateAsync(s => s.SetProperty(i => i.ValueKey, (string?)null).SetProperty(i => i.HostRev, (string?)null));
        }
        var audited = await AuditRows(db);

        await using (var ctx = db.Context())
            (await IndicatorKeyFill.FillAsync(ctx, batches: 5)).Should().Be((1, true));

        await using (var ctx = db.Context())
        {
            (await ctx.Articles.AsNoTracking().SingleAsync(a => a.Id == id)).ModifiedAtUtc.Should().Be(modified, "keying isn't an edit");
            (await ctx.ArticleIndicators.AsNoTracking().SingleAsync()).HostRev.Should().Be("net.example.first");
        }
        (await AuditRows(db)).Should().Be(audited, "keying isn't an edit");
    }

    private static async Task<int> AuditRows(TestDb db)
    {
        await using var ctx = db.Context();
        return await ctx.AuditLog.CountAsync();
    }

    private static async Task<Guid> Article(TestDb db, string title, params (IndicatorType Type, string Value)[] indicators)
    {
        var svc = db.Articles();
        var id = await svc.CreateAsync(title, "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Bluf = "Summary.";
        form.Indicators = indicators.Select(i => new IndicatorForm { Type = i.Type, Value = i.Value }).ToList();
        await svc.SaveAsync(form);
        return id;
    }
}
