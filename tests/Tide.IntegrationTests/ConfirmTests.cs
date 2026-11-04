using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Briefs;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// The one confirmation (ConfirmDialog), the one empty state and the one loading state: the dialog on its own, and the
/// confirmations the pages ask, opened as their buttons open them.
/// </summary>
public partial class ConfirmTests(TideApp app) : IClassFixture<TideApp>
{
    [System.Text.RegularExpressions.GeneratedRegex(@"Dismiss all (\d+) shown…")]
    private static partial System.Text.RegularExpressions.Regex DismissAllShown();

    /// <summary>Renders <typeparamref name="T"/>, signed in as the dev user (Analyst and Admin), and hands it over.</summary>
    private Task<ComponentHarness.Rendered<T>> Render<T>(string path, Dictionary<string, object?>? values = null) where T : IComponent =>
        ComponentHarness.RenderAsync<T>(app, path, values);

    private static RenderFragment Text(string s) => b => b.AddContent(0, s);

    [Fact]
    public async Task AClosedConfirmation_DrawsNothing()
    {
        await using var r = await Render<ConfirmDialog>("/");
        (await r.Html()).Should().BeEmpty();
    }

    [Fact]
    public async Task AConfirmation_SaysWhatHappens_WithAnOutlineCancel_AndAButtonNamedForTheAction()
    {
        await using var r = await Render<ConfirmDialog>("/");
        Task<bool>? answer = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { answer = r.Component.Ask("Delete draft?", "It can't be undone.", "Delete draft"); });
        var html = await r.Html();
        html.Should().Contain("role=\"alertdialog\"").And.Contain("aria-modal=\"true\"")
            .And.Contain(">Delete draft?</h2>").And.Contain("It can&#x27;t be undone.")
            .And.Contain("class=\"btn btn-outline-secondary btn-sm\"").And.Contain(">Cancel</button>")
            .And.Contain("btn btn-danger btn-sm").And.Contain("Delete draft");

        await r.Renderer.Dispatcher.InvokeAsync(r.Component.Cancel);
        (await answer!).Should().BeFalse();
    }

    [Fact]
    public async Task AFormConfirmation_HoldsItsFields_AndItsButtonBackUntilTheyreDone()
    {
        await using var r = await Render<ConfirmDialog>("/", new()
        {
            ["ChildContent"] = Text("the outcome field"), ["Form"] = true, ["ConfirmDisabled"] = true, ["ConfirmIcon"] = "bi-check2",
        });
        await r.Press(c => c.Ask("Move A-1 to Done", null, "Move to Done", danger: false));
        var html = await r.Html();
        html.Should().Contain("role=\"dialog\"").And.Contain("tide-modal-content").And.Contain("the outcome field")
            .And.Contain("btn btn-primary btn-sm\" disabled").And.Contain("bi bi-check2").And.NotContain("tide-modal-body");
    }

    [Fact]
    public async Task ARefusedStep_KeepsTheConfirmationOpen_AndADoneOneClosesIt()
    {
        var results = new Queue<bool>([false, true]);
        await using var r = await Render<ConfirmDialog>("/", new() { ["Confirming"] = (Func<Task<bool>>)(() => Task.FromResult(results.Dequeue())) });
        Task<bool>? answer = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { answer = r.Component.Ask("Save?", "", "Save", danger: false); });

        await r.Renderer.Dispatcher.InvokeAsync(r.Component.Confirm);
        (await r.Html()).Should().Contain(">Save?</h2>");
        answer!.IsCompleted.Should().BeFalse();

        await r.Renderer.Dispatcher.InvokeAsync(r.Component.Confirm);
        (await answer).Should().BeTrue();
    }

    [Fact]
    public async Task DismissAll_AsksInTheDialog_WithTheReasonAndTheCount()
    {
        await using var r = await Render<Triage>("/triage");
        var page = await r.Html();
        var shown = DismissAllShown().Match(page);
        shown.Success.Should().BeTrue("the sample data has new items in the inbox");
        page.Should().NotContain("role=\"alert\"");

        await r.Press(t => t.AskDismissAll());
        var html = await r.Html();
        var n = shown.Groups[1].Value;
        html.Should().Contain($">Dismiss all {n} shown?</h2>").And.Contain("The inbox is shared.")
            .And.Contain("for=\"dismiss-all-reason\">Dismiss them as</label>").And.Contain($">Dismiss {n}</button>")
            .And.Contain("btn btn-danger btn-sm");
        foreach (var reason in Tide.Application.Intake.IntakeService.DismissReasons) html.Should().Contain($">{reason}</option>");
    }

    [Fact]
    public async Task ASendConfirmation_ShowsTheMarking_Subject_AndEveryRecipient()
    {
        var plan = new SendPlan(new DateOnly(2026, 10, 29), Edition.Team, TlpLevel.AmberStrict, ["a@example.com", "b@example.com"], 3, 0, 1,
            "TIDE daily brief, 29 October", true, "abc123");
        await using var r = await Render<SendConfirmation>("/", new() { ["Plan"] = plan });
        var html = await r.Html();
        html.Should().Contain("Subject: <strong>TIDE daily brief, 29 October</strong>").And.Contain("To 2 recipients:")
            .And.Contain("a@example.com; b@example.com").And.Contain("Marked <span").And.Contain("kept in the archive")
            .And.NotContain("Already sent").And.NotContain("Readers will see it");
        SendConfirmation.Title(plan).Should().Be("Send the security team edition now?");
        SendConfirmation.Label(plan).Should().Be("Send to 2");
    }

    [Fact]
    public async Task ASendConfirmation_WarnsWhenItWentBefore_OrMayHave()
    {
        var sent = new SendPlan(new DateOnly(2026, 10, 29), Edition.Public, TlpLevel.Green, ["all@example.com"], 2, 1, 0, "Brief", true, "f",
            LastSentAtUtc: DateTimeOffset.UtcNow.AddHours(-1), LastSentBy: "Robin");
        await using (var r = await Render<SendConfirmation>("/", new() { ["Plan"] = sent }))
        {
            (await r.Html()).Should().Contain("Already sent").And.Contain("by Robin").And.Contain("To 1 recipient:")
                .And.Contain("Readers will see it on the published briefs page.");
        }
        SendConfirmation.Title(sent).Should().Be("Send the company-wide edition again?");

        await using var unknown = await Render<SendConfirmation>("/", new() { ["Plan"] = sent with { LastOutcomeUnknown = true } });
        (await unknown.Html()).Should().Contain("may have gone").And.NotContain("Already sent");
    }

    [Fact]
    public async Task ARotationRequest_IsStillOneClick_InItsHighlightedPanel()
    {
        await using var r = await Render<RotationPage>("/rotation");
        (await r.Html()).Should().NotContain("tide-modal").And.NotContain("Approve&#x2026;").And.NotContain("tide-confirm");
    }

    [Fact]
    public async Task AnEmptyEdition_IsTheEmptyState()
    {
        var brief = new Brief { BriefDate = new DateOnly(2026, 10, 29), AnalystName = "Robin" };
        await using var r = await Render<EditionView>("/", new() { ["Edition"] = BriefEditions.Build(brief, Edition.Public, TlpLevel.Green) });
        var html = await r.Html();
        html.Should().Contain("tide-empty-state").And.Contain("Nothing in this edition").And.NotContain("class=\"tide-empty\"");
    }

    [Theory]
    [InlineData(false, "tide-empty-state  \"")]
    [InlineData(true, "tide-empty-state tide-empty-state-compact")]
    public async Task AnEmptyState_HasItsIconAndWords_AndACompactOneIsSmaller(bool compact, string css)
    {
        await using var r = await Render<EmptyState>("/", new() { ["Title"] = "Nothing sent yet.", ["Icon"] = "bi-send", ["Compact"] = compact });
        var html = await r.Html();
        html.Should().Contain(css).And.Contain("bi bi-send tide-empty-state-icon").And.Contain(">Nothing sent yet.</div>");
    }

    [Fact]
    public async Task TheLoadingState_IsAStatusLine_AndPlaceholderRows_WithNoInlineStyle()
    {
        await using var r = await Render<LoadingBlock>("/", new() { ["Rows"] = 2, ["Columns"] = 1, ["Label"] = "Loading the checklist…" });
        var html = await r.Html();
        html.Should().Contain("role=\"status\">Loading the checklist&#x2026;</span>").And.Contain("tide-skel tide-skel-w0").And.Contain("tide-skel-w1")
            .And.NotContain("style=");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/rotation")]
    [InlineData("/workload")]
    public async Task Pages_DrawTheirEmptyStates_AndNoInlineConfirmations(string path)
    {
        var html = await app.CreateClient().GetStringAsync(path);
        html.Should().NotContain("class=\"tide-empty\"").And.NotContain("tide-confirm");
    }
}
