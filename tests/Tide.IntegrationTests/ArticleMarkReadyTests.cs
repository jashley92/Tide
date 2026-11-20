using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// The editor's Save and mark ready: while a blocking readiness check fails on what's on screen, the button is disabled
/// and says why beside it, and pressing it anyway (a script, a stale page) says only that, rather than "Saved." and then
/// that it isn't ready.
/// </summary>
public class ArticleMarkReadyTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<Guid> NewArticle(Action<Article>? fill = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var article = new Article { Title = "Mark ready check", AuthorId = "x", AuthorName = "X", Classification = Classification.Informational };
        fill?.Invoke(article);
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return article.Id;
    }

    private static string ReadyButton(string html)
    {
        var at = html.IndexOf("data-guide=\"mark-ready\"", StringComparison.Ordinal);
        at.Should().BePositive();
        var start = html.LastIndexOf("<button", at, StringComparison.Ordinal);
        return html[start..(html.IndexOf("</button>", at, StringComparison.Ordinal) + 9)];
    }

    private static string[] Told(ComponentHarness.Rendered<ArticleEditorPanel> r) =>
        [.. r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Select(t => t.Message)];

    [Fact]
    public async Task WhileABlockingCheckFails_TheButtonIsDisabled_AndSaysWhyBesideIt()
    {
        var id = await NewArticle();
        await using var r = await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });
        var html = System.Net.WebUtility.HtmlDecode(await r.Html());

        var button = ReadyButton(html);
        button.Should().Contain(" disabled").And.Contain("aria-describedby=\"not-ready-");
        var described = button[(button.IndexOf("aria-describedby=\"", StringComparison.Ordinal) + 18)..];
        described = described[..described.IndexOf('"')];
        html.Should().Contain($"<span id=\"{described}\" class=\"small tide-muted\">It isn't ready yet: Add the bottom line (BLUF).</span>");
    }

    [Fact]
    public async Task PressedAnyway_ItSaysOnlyWhy_WithoutSavingFirst()
    {
        var id = await NewArticle(a => a.Bluf = "Exploited in the wild.");
        await using var r = await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });

        await r.Click(p => p.MarkReady());

        Told(r).Should().Equal("It isn't ready yet: Add at least one bullet.");
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        (await scope.ServiceProvider.GetRequiredService<Application.Articles.ArticleService>().GetAsync(id))!.Status.Should().Be(ArticleStatus.Draft);
    }

    [Fact]
    public async Task WithNothingBlocking_TheButtonIsEnabled_AndMarksItReady()
    {
        var id = await NewArticle(a =>
        {
            a.Bluf = "Exploited in the wild.";
            a.Bullets.Add(new ArticleBullet { Text = "Patch is out." });
        });
        await using var r = await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });

        var button = ReadyButton(await r.Html());
        button.Should().NotContain(" disabled").And.NotContain("aria-describedby");
        System.Net.WebUtility.HtmlDecode(await r.Html()).Should().NotContain("It isn't ready yet");

        await r.Click(p => p.MarkReady());

        Told(r).Should().Equal("Marked ready for a brief.");
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        (await scope.ServiceProvider.GetRequiredService<Application.Articles.ArticleService>().GetAsync(id))!.Status.Should().Be(ArticleStatus.Ready);
    }
}
