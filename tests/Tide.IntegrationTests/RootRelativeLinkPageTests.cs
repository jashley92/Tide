using FluentAssertions;
using Tide.Application.Distribution;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Markdown shown on a page (a comment, an action's details, a note, a brief) links to TIDE from the server's
/// root ("[x](/actions/12)") under the app's sub-path, as the page knows it from its base address.</summary>
public class RootRelativeLinkPageTests(TideApp app) : IClassFixture<TideApp>
{
    private const string Text = "See [the action](/actions/12), [the brief](/tide/briefs/2026-11-06) and [x](//evil.example/a).";

    [Fact]
    public async Task UnderASubPath_ALinkFromTheRoot_IsGivenTheSubPath_OnlyOnce()
    {
        await using var block = await RenderAsync<Md>(app, "/actions/12", new() { ["Text"] = Text }, baseUri: "http://localhost/tide/");
        await using var inline = await RenderAsync<Md>(app, "/actions/12", new() { ["Text"] = Text, ["Inline"] = true }, baseUri: "http://localhost/tide/");

        foreach (var html in new[] { await block.Html(), await inline.Html() })
            html.Should().Contain("href=\"/tide/actions/12\" target=\"_blank\" rel=\"noopener noreferrer\"")
                .And.Contain("href=\"/tide/briefs/2026-11-06\"").And.NotContain("/tide/tide/")
                .And.Contain("href=\"#\"").And.NotContain("evil.example\"");
    }

    [Fact]
    public async Task AtTheServersRoot_ALinkFromTheRoot_IsAsTyped()
    {
        await using var page = await RenderAsync<Md>(app, "/actions/12", new() { ["Text"] = Text });

        (await page.Html()).Should().Contain("href=\"/actions/12\"").And.Contain("href=\"/tide/briefs/2026-11-06\"");
    }
}

/// <summary>An email template's body that links from the server's root points at the app's address (App:BaseUrl).</summary>
public sealed class RootRelativeLinkTemplateTests : IDisposable
{
    private readonly TestDb _db = new();

    public RootRelativeLinkTemplateTests() => _db.User.Roles = new HashSet<Tide.Domain.Enums.AppRole> { Tide.Domain.Enums.AppRole.Analyst, Tide.Domain.Enums.AppRole.Admin };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ASavedTemplatesRootLink_IsMadeAbsoluteInTheEmail()
    {
        var svc = _db.EmailTemplateAdmin();
        var v = (await svc.GetAllAsync()).Single(x => x.Template == EmailTemplateCatalog.Presenting);
        await svc.SaveAsync(v.Template.Id, v.Subject, v.Heading, v.Body + "\n\nSee [the rotation](/rotation).", v.Stamp);

        _db.EmailTemplates.Render(EmailTemplateCatalog.Presenting, new Dictionary<string, string>()).BodyHtml
            .Should().Contain("href=\"https://tide.example.org/rotation\"").And.NotContain("href=\"/rotation\"");
    }
}
