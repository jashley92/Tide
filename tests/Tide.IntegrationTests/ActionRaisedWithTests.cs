using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Domain.Enums;
using Tide.Web.Components.ActionRail;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The action rail's "Also raised from it": its link to all the article's actions goes to the article, so it's there only
/// for someone who may open the article (who reads the team edition), as the article card beside it is.
/// </summary>
public class ActionRaisedWithTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Reader = new("dev:raised-reader", "Rae Reader", "Reader");

    private static ActionRow Row(int n) => new(Guid.NewGuid(), n, $"Raised {n}", ActionKind.IocSweep, Priority.P2, Guid.NewGuid(), "Cyber Defense",
        null, null, null, ActionStatus.Open, ActionOutcome.None, null, null, null, null, 0, DateTimeOffset.UtcNow);

    [Fact]
    public async Task TheLinkToTheArticlesActions_IsOnlyForSomeoneWhoMayOpenTheArticle()
    {
        var article = Guid.NewGuid();
        var rows = Enumerable.Range(1, ActionRaisedWith.Shown + 1).Select(Row).ToList();
        Dictionary<string, object?> values = new() { [nameof(ActionRaisedWith.Rows)] = rows, [nameof(ActionRaisedWith.ArticleId)] = article };

        await using (var analyst = await RenderAsync<ActionRaisedWith>(app, "/actions/1", values))
            (await analyst.Html()).Should().Contain($"href=\"articles/{article}?side=actions\"");

        await using var reader = await RenderAsync<ActionRaisedWith>(app, "/actions/1", values, Reader);
        var html = await reader.Html();
        html.Should().Contain("Also raised from it").And.NotContain($"articles/{article}", "the article isn't theirs to open");
    }
}
