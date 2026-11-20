using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.TestSupport;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The app with its timers held on a clock a test moves on, so the editor's autosave pause runs out only when the test
/// says. Anything else here that waits on the app's clock (a hosted job's pacing, say) waits for good, so test that elsewhere.
/// </summary>
public sealed class AutosaveClockApp : TideApp
{
    public ManualClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Clock));
    }
}

/// <summary>
/// When the editor's autosave runs, against what else is going on: it waits while Discard's question or the leave question
/// is open, so what the answer drops isn't saved first, and starts again when the answer keeps the edits; and what's typed
/// during an upload is saved once the upload is over, not left unsaved until the next keystroke.
/// </summary>
public class ArticleAutosaveTimingTests(AutosaveClockApp app) : IClassFixture<AutosaveClockApp>
{
    private const string Typed = "We run it.";

    private async Task<Guid> NewArticle()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var article = new Article
        {
            Title = "Autosave timing", AuthorId = "x", AuthorName = "X", Tlp = TlpLevel.Clear, Bluf = "Exploited in the wild.",
            WhyItMattersTlp = TlpLevel.Amber,
        };
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return article.Id;
    }

    private async Task<Article> Saved(Guid id)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(id))!;
    }

    // The editor, signed in as a circuit is: its saves run from the pause's timer and after a dialog is answered, not
    // from a click, so the sign-in comes from the circuit's own state rather than the request a click would carry.
    private async Task<Rendered<ArticleEditorPanel>> Editor(Guid id)
    {
        var r = await RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });
        if (r.Scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>() is IHostEnvironmentAuthenticationStateProvider circuit)
            circuit.SetAuthenticationState(Task.FromResult(new AuthenticationState(r.Signedin.User)));
        return r;
    }

    // The pause runs out, and whatever it starts (a save, or another pause) has had time to run on the renderer.
    private async Task PauseRunsOut(Rendered<ArticleEditorPanel> r)
    {
        app.Clock.Advance(ArticleEditorPanel.AutosavePause + TimeSpan.FromMilliseconds(500));
        await Task.Delay(300);
        await r.Until(p => !p.Busy);
    }

    // Pauses run out one after another until the typing is saved: as many as the editor needs, and no more than 20.
    private async Task<bool> SavedInTheEnd(Rendered<ArticleEditorPanel> r, Guid id)
    {
        for (var i = 0; i < 20; i++)
        {
            await PauseRunsOut(r);
            if ((await Saved(id)).WhyItMatters == Typed) return true;
        }
        return false;
    }

    [Fact]
    public async Task Discard_AnsweredAfterThePause_LeavesTheSavedArticleAsItWas()
    {
        var id = await NewArticle();
        var before = await Saved(id);
        await using var r = await Editor(id);
        await r.Click(p => p.MentionAsync(Typed));

        await r.Press(p => p.Discard());
        await PauseRunsOut(r);   // a slow answer: the pause the edit started has long run out
        await r.Click(p => p.Confirm.Confirm());
        (await r.Until(p => !p.Dirty)).Should().BeTrue("the saved article is loaded again");

        var saved = await Saved(id);
        (saved.WhyItMatters, saved.EditVersion).Should().Be((before.WhyItMatters, before.EditVersion), "nothing was saved while the question was open");
    }

    // The editor stays open from one article to the next (the brief's stepper, Ctrl+K): Discard's question about the
    // first is cancelled, so it neither holds the next one's autosave nor drops its edits.
    [Fact]
    public async Task Discard_LeftOpen_WhenTheNextArticleOpens_IsCancelled_AndTheNextOneAutosaves()
    {
        var first = await NewArticle();
        var next = await NewArticle();
        var before = await Saved(first);
        await using var r = await Editor(first);
        await r.Click(p => p.MentionAsync("Typed on the first."));
        await r.Press(p => p.Discard());
        (await r.Html()).Should().Contain("Discard your changes?");

        await r.Click(p => p.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Id"] = next })));
        (await r.Until(p => !p.Dirty)).Should().BeTrue("the next article is loaded");
        (await r.Html()).Should().NotContain("Discard your changes?", "the question about the first article has gone");
        await r.Click(p => p.MentionAsync(Typed));

        (await SavedInTheEnd(r, next)).Should().BeTrue("nothing holds the next article's autosave");
        (await Saved(first)).EditVersion.Should().Be(before.EditVersion);
        r.Component.Dirty.Should().BeFalse();
    }

    [Fact]
    public async Task Discard_Cancelled_StillAutosavesAfterAPause()
    {
        var id = await NewArticle();
        await using var r = await Editor(id);
        await r.Click(p => p.MentionAsync(Typed));

        await r.Press(p => p.Discard());
        await PauseRunsOut(r);
        (await Saved(id)).WhyItMatters.Should().BeNull("nothing saves while the question is open");
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Confirm.Cancel());

        (await SavedInTheEnd(r, id)).Should().BeTrue("Cancel keeps the edits, so the pause starts again");
        r.Component.Dirty.Should().BeFalse();
    }

    [Fact]
    public async Task DiscardAndLeave_AnsweredAfterThePause_LeavesTheSavedArticleAsItWas()
    {
        var id = await NewArticle();
        var before = await Saved(id);
        await using var r = await Editor(id);
        await r.Click(p => p.MentionAsync(Typed));

        var leaving = Task.FromResult(false);
        await r.Press(p => leaving = p.MayLeave());
        await PauseRunsOut(r);
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Leave.Close(LeaveDialog.Choice.Discard));

        (await leaving).Should().BeTrue();
        (await Saved(id)).EditVersion.Should().Be(before.EditVersion, "nothing was saved while the question was open");
    }

    [Fact]
    public async Task Stay_StillAutosavesAfterAPause()
    {
        var id = await NewArticle();
        await using var r = await Editor(id);
        await r.Click(p => p.MentionAsync(Typed));

        var leaving = Task.FromResult(true);
        await r.Press(p => leaving = p.MayLeave());
        await PauseRunsOut(r);
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Leave.Stay());

        (await leaving).Should().BeFalse();
        (await SavedInTheEnd(r, id)).Should().BeTrue("Stay keeps the edits, so the pause starts again");
    }

    [Fact]
    public async Task AnEditTypedDuringAnUpload_IsSavedOnceTheUploadEnds()
    {
        var id = await NewArticle();
        await using var r = await Editor(id);
        var upload = new HeldFile("shot.png", Samples.Png);

        await r.Press(p => p.OnFiles(new InputFileChangeEventArgs([upload])));
        (await r.Until(p => p.Uploading)).Should().BeTrue();
        await r.Click(p => p.MentionAsync(Typed));
        await PauseRunsOut(r);   // a big screenshot: the pause runs out while it's still uploading
        (await Saved(id)).WhyItMatters.Should().BeNull();

        upload.Arrive();
        (await r.Until(p => !p.Uploading)).Should().BeTrue();

        (await SavedInTheEnd(r, id)).Should().BeTrue("the edit is tried again after the upload, without another keystroke");
        (await Saved(id)).Screenshots.Should().ContainSingle();
        r.Component.Dirty.Should().BeFalse();
    }

    /// <summary>A file picked for upload whose bytes arrive only when the test says (<see cref="Arrive"/>): a big
    /// screenshot on a slow connection.</summary>
    private sealed class HeldFile(string name, byte[] bytes) : IBrowserFile
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arrive() => _arrived.TrySetResult();

        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => bytes.Length;
        public string ContentType => "image/png";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new Held(bytes, _arrived.Task);

        private sealed class Held(byte[] bytes, Task arrived) : MemoryStream(bytes)
        {
            public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                await arrived;
                await base.CopyToAsync(destination, bufferSize, cancellationToken);
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await arrived;
                return await base.ReadAsync(buffer, cancellationToken);
            }
        }
    }
}
