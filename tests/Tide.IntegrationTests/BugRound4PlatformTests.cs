using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.History;
using Tide.Domain.Entities;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Bug round 4, platform: the History panel's older page after a change.</summary>
public class BugRound4PlatformTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task Record(Guid article, DateTimeOffset at, string title)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        db.AuditLog.Add(new AuditLogEntry
        {
            AtUtc = at, UserId = "dev:tester", UserName = "Tester", Action = "Modified", EntityType = nameof(Article),
            EntityKey = article.ToString("D"), ChangesJson = $$"""{"Title":["was","{{title}}"]}""",
        });
        await db.SaveChangesAsync();
    }

    // The log grows at the top. A change saved after the first page was read (a brief's send marks its articles presented,
    // which doesn't move the edit version the panel reloads on) pushed every row down one, so "Show 50 older" began with
    // the last row already shown, and the history listed it twice.
    [Fact]
    public async Task ShowOlder_AfterAChangeAtTheTop_ListsEachChangeOnce()
    {
        var article = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        for (var i = 0; i <= HistoryService.PageSize; i++)
            await Record(article, start.AddMinutes(-i), $"title-{i:00}-end");

        await using var r = await RenderAsync<HistoryPanel>(app, "/articles/" + article, new() { ["Id"] = article });
        await r.Click(p => p.Toggle());
        var first = await r.Html();
        first.Should().Contain($"title-{HistoryService.PageSize - 1:00}-end").And.NotContain($"title-{HistoryService.PageSize:00}-end");

        await Record(article, start.AddMinutes(1), "title-new-end");
        await r.Click(p => p.More());

        var html = await r.Html();
        for (var i = 0; i <= HistoryService.PageSize; i++)
            Regex.Count(html, $"title-{i:00}-end").Should().Be(1, $"change {i} is listed once");
    }
}
