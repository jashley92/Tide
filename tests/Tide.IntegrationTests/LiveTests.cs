using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

public class PresenceTests
{
    private const string Brief = "brief:2026-10-22";

    [Fact]
    public void Others_AreThePeopleElseAtAPlace_EachOnce_UntilTheyLeave()
    {
        var presence = new Presence();
        var changes = new List<string>();
        presence.Changed += changes.Add;

        using var me = presence.Join(Brief, "S-ME", "Me");
        var robin = presence.Join(Brief, "S-ROBIN", "Robin");
        using var robinAgain = presence.Join(Brief, "S-ROBIN", "Robin");   // a second tab
        using var elsewhere = presence.Join("article:x", "S-SAM", "Sam");

        presence.Others(Brief, "S-ME").Select(p => p.Name).Should().Equal("Robin");
        robin.Dispose();
        robin.Dispose();   // leaving twice is leaving once
        presence.Others(Brief, "S-ME").Select(p => p.Name).Should().Equal(["Robin"], "the other tab is still open");
        robinAgain.Dispose();
        presence.Others(Brief, "S-ME").Should().BeEmpty();
        presence.Others("nowhere", "S-ME").Should().BeEmpty();
        changes.Count(c => c == Brief).Should().Be(5, "three joins and two leaves");
    }

    [Fact]
    public void Others_AreInNameOrder()
    {
        var presence = new Presence();
        using var sam = presence.Join(Brief, "S-SAM", "Sam");
        using var ash = presence.Join(Brief, "S-ASH", "Ash");

        presence.Others(Brief, "S-ME").Select(p => p.Name).Should().Equal("Ash", "Sam");
    }

    [Fact]
    public void Editing_ShowsWhileAnyOfTheirTabsHasUnsavedChanges_AndSaysSoOnlyWhenItChanges()
    {
        var presence = new Presence();
        var changes = 0;
        using var robin = presence.Join(Brief, "S-ROBIN", "Robin");
        using var robinAgain = presence.Join(Brief, "S-ROBIN", "Robin");
        presence.Changed += _ => changes++;

        presence.Others(Brief, "S-ME").Single().Editing.Should().BeFalse();
        robin.SetEditing(true);
        robin.SetEditing(true);   // no change, no news
        presence.Others(Brief, "S-ME").Single().Should().Be(new PresentPerson("S-ROBIN", "Robin", Editing: true));
        changes.Should().Be(1);

        robin.SetEditing(false);
        presence.Others(Brief, "S-ME").Single().Editing.Should().BeFalse();
        robinAgain.SetEditing(true);
        presence.Others(Brief, "S-ME").Single().Editing.Should().BeTrue("the other tab has unsaved changes");
        changes.Should().Be(3);
    }

    [Fact]
    public void Editing_AfterLeaving_IsIgnored()
    {
        var presence = new Presence();
        var robin = presence.Join(Brief, "S-ROBIN", "Robin");
        robin.Dispose();
        var changes = 0;
        presence.Changed += _ => changes++;

        robin.SetEditing(true);

        changes.Should().Be(0);
        presence.Others(Brief, "S-ME").Should().BeEmpty();
    }

    [Fact]
    public void RemoveCircuit_LeavesEveryPlaceThatTabJoined_AndNoOneElses()
    {
        var presence = new Presence();
        using var a = presence.Join(Brief, "S-ROBIN", "Robin", "circuit-1");
        using var b = presence.Join("article:x", "S-ROBIN", "Robin", "circuit-1");
        using var sam = presence.Join(Brief, "S-SAM", "Sam", "circuit-2");
        using var prerender = presence.Join(Brief, "S-ASH", "Ash");
        var left = new List<string>();
        presence.Changed += left.Add;

        presence.RemoveCircuit("circuit-1");
        presence.RemoveCircuit("");   // a prerender has no circuit; nothing to clear

        presence.Others(Brief, "S-ME").Select(p => p.Name).Should().Equal("Ash", "Sam");
        presence.Others("article:x", "S-ME").Should().BeEmpty();
        left.Should().BeEquivalentTo([Brief, "article:x"]);
    }

    [Fact]
    public void Circuit_KnowsItsId_OnceOpen_AndClearsItsPresence_WhenItCloses()
    {
        var presence = new Presence();
        var circuit = new PresenceCircuit(presence);
        circuit.CircuitId.Should().BeEmpty("no circuit has opened yet (a prerender)");
        circuit.Opened("circuit-1");
        using var robin = presence.Join(Brief, "S-ROBIN", "Robin", circuit.CircuitId);

        circuit.Closed("circuit-1");

        circuit.CircuitId.Should().Be("circuit-1");
        presence.Others(Brief, "S-ME").Should().BeEmpty();
    }
}

public class ChangeFeedTests
{
    [Fact]
    public void Publish_TellsSubscribersWhatChanged()
    {
        var feed = new ChangeFeed();
        Change? heard = null;
        feed.Changed += c => heard = c;

        feed.Publish(ChangeFeed.Triage);

        heard.Should().Be(new Change(ChangeFeed.Triage, "system", "", Guid.Empty));
    }

    [Fact]
    public void Publish_WithNoOneListening_IsFine()
    {
        var publish = () => new ChangeFeed().Publish(ChangeFeed.Actions);
        publish.Should().NotThrow();
    }

    [Fact]
    public void ASubscriberThatThrows_DoesntFailThePublisher_OrSilenceTheOthers()
    {
        var feed = new ChangeFeed();
        var heard = 0;
        feed.Changed += _ => throw new ObjectDisposedException("a closing page");
        feed.Changed += _ => heard++;

        var publish = () => feed.Publish(ChangeFeed.Triage);

        publish.Should().NotThrow();
        heard.Should().Be(1);
    }

    [Fact]
    public void Topics_NameTheThing()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        ChangeFeed.Article(id).Should().Be("article:11111111-2222-3333-4444-555555555555");
        ChangeFeed.Brief(id).Should().Be("brief:11111111-2222-3333-4444-555555555555");
        ChangeFeed.Action(id).Should().Be("action:11111111-2222-3333-4444-555555555555");
        ChangeFeed.BriefDay(new DateOnly(2026, 10, 22)).Should().Be("brief-day:2026-10-22");
    }
}

public class LiveChangesTests
{
    private readonly ChangeFeed _feed = new();
    private readonly ChangeOrigin _mine = new();
    private readonly ManualTime _time = new();
    private readonly List<IReadOnlyList<Change>> _applied = [];

    private LiveChanges Listen(Func<Change, bool>? wanted = null, Func<Func<Task>, Task>? dispatch = null) =>
        new(_feed, _mine, wanted ?? (c => c.Topic.StartsWith("article:", StringComparison.Ordinal)), dispatch ?? (work => work()),
            heard => { _applied.Add(heard); return Task.CompletedTask; }, TimeSpan.FromSeconds(1), _time);

    private static Change From(string who, string topic = "article:a") => new(topic, "S-" + who, who, Guid.NewGuid());

    [Fact]
    public void ABurst_IsOneReload_AfterThePause()
    {
        using var live = Listen();
        _feed.Publish(From("Robin"));
        _feed.Publish(From("Robin", "article:b"));
        _feed.Publish(From("Sam"));

        _applied.Should().BeEmpty("the burst may not be over");
        _time.Due.Should().Be(TimeSpan.FromSeconds(1), "each change restarts the pause");
        _time.Fire();

        _applied.Should().ContainSingle().Which.Select(c => c.ActorName).Should().Equal("Robin", "Robin", "Sam");
        _time.Fire();
        _applied.Should().ContainSingle("nothing new was heard since");
    }

    [Fact]
    public void ItsOwnSaves_AndWhatItDoesntShow_AreIgnored()
    {
        using var live = Listen();
        _feed.Publish(new Change("article:a", "S-ME", "Me", _mine.Id));
        _feed.Publish(From("Robin", ChangeFeed.Actions));

        _time.Fire();

        _applied.Should().BeEmpty();
        _time.Due.Should().BeNull("nothing started the pause");
    }

    [Fact]
    public void AfterThePageCloses_NothingIsHeard()
    {
        var live = Listen();
        _feed.Publish(From("Robin"));
        live.Dispose();
        live.Dispose();   // closing twice is closing once

        _feed.Publish(From("Sam"));
        _time.Fire();

        _applied.Should().BeEmpty();
    }

    [Fact]
    public void AReloadThatFails_AsThePageCloses_DoesntEscape()
    {
        using var live = Listen(dispatch: _ => Task.FromException(new ObjectDisposedException("the page")));
        _feed.Publish(From("Robin"));

        var fire = () => _time.Fire();

        fire.Should().NotThrow();
    }

    // Two colleagues' changes a pause apart: the second reload mustn't start while the first is still reading, or a slow
    // first read (from before the second change) finishes last and leaves the page on the older state.
    [Fact]
    public async Task ASecondReload_WaitsForTheFirst_AndThenReadsWhatCameMeanwhile()
    {
        var first = new TaskCompletionSource();
        var calls = new List<(string Who, bool Finished)>();
        var running = 0;
        var overlapped = false;
        using var live = new LiveChanges(_feed, _mine, _ => true, work => work(), async heard =>
        {
            var at = calls.Count;
            calls.Add((heard[0].ActorName, false));
            if (Interlocked.Increment(ref running) > 1) overlapped = true;
            if (at == 0) await first.Task;   // the first read is slow
            Interlocked.Decrement(ref running);
            calls[at] = (calls[at].Who, true);
        }, TimeSpan.FromSeconds(1), _time);

        _feed.Publish(From("Robin"));
        _time.Fire();                    // Robin's reload starts, and waits on its read
        _feed.Publish(From("Sam"));
        _time.Fire();                    // Sam's change is heard while it reads

        calls.Should().ContainSingle("Sam's reload waits for Robin's").Which.Who.Should().Be("Robin");
        first.SetResult();
        for (var i = 0; i < 50 && calls.Count < 2; i++) await Task.Delay(20);

        overlapped.Should().BeFalse();
        calls.Should().Equal(("Robin", true), ("Sam", true));
    }

    [Theory]
    [InlineData(new string[0], "Someone")]
    [InlineData(new[] { "" }, "Someone")]
    [InlineData(new[] { "Robin", "Robin" }, "Robin")]
    [InlineData(new[] { "Robin", "Sam" }, "Robin and Sam")]
    [InlineData(new[] { "Robin", "Sam", "Ash" }, "Robin, Sam and Ash")]
    public void Who_NamesEachPersonOnce(string[] names, string expected) =>
        LiveChanges.Who(names.Select(n => new Change("t", "S", n, Guid.Empty))).Should().Be(expected);

    /// <summary>A clock whose one timer fires when the test says.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public TimeSpan? Due { get; private set; }

        public void Fire()
        {
            Due = null;
            _callback?.Invoke(_state);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new Timer(this);
        }

        private sealed class Timer(ManualTime owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.Due = dueTime == Timeout.InfiniteTimeSpan ? null : dueTime;
                return true;
            }

            public void Dispose() => owner._callback = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

public class ChangeInterceptorTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly List<Change> _heard = [];

    public ChangeInterceptorTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        _db.User.DisplayName = "Robin Analyst";
        _db.Feed.Changed += c => { lock (_heard) _heard.Add(c); };
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SavingAnArticle_TellsWhoSavedIt_FromWhere()
    {
        var id = await _db.Articles().CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);

        _heard.Should().Equal(
            new Change(ChangeFeed.Article(id), _db.User.UserId, "Robin Analyst", _db.Origin.Id),
            new Change(ChangeFeed.Articles, _db.User.UserId, "Robin Analyst", _db.Origin.Id));
    }

    [Fact]
    public async Task ABriefAndItsArticles_AreHeardByTopic_EachOncePerSave()
    {
        var article = await _db.Articles().CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var day = new DateOnly(2026, 10, 22);
        var brief = await _db.Briefs().GetOrStartAsync(day);
        _heard.Clear();

        await _db.Briefs().AddArticleAsync(brief.Id, article);

        _heard.Select(c => c.Topic).Should().OnlyHaveUniqueItems().And.Contain(ChangeFeed.Brief(brief.Id));
        BriefPage.Follow(await _db.Briefs().GetAsync(day), day).Should().BeEquivalentTo(
            [ChangeFeed.BriefDay(day), ChangeFeed.Brief(brief.Id), ChangeFeed.Article(article)]);
    }

    // UI4-1: a colleague's adversary or product tag doesn't move the article's edit version, so it's heard only by the
    // Related tab's cards: not by the article's editor, nor by a brief (or Present mode) that has the article on it.
    [Fact]
    public async Task TaggingAnArticle_IsHeardOnItsTagsTopic_NotTheArticles()
    {
        var article = await _db.Articles().CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var day = new DateOnly(2026, 10, 23);
        var brief = await _db.Briefs().GetOrStartAsync(day);
        await _db.Briefs().AddArticleAsync(brief.Id, article);
        var adversary = await _db.Adversaries().SaveAsync(new Tide.Application.Adversaries.AdversaryForm { Name = "Live-test Spider" });
        var product = await _db.Stack().SaveAsync(new Tide.Application.Stack.StackForm { Name = "Live-test Gateway" });
        _heard.Clear();

        await _db.Adversaries().TagArticleAsync(article, adversary);
        await _db.Stack().TagArticleAsync(article, product);
        await _db.Adversaries().UntagArticleAsync(article, adversary);

        // The article's tags topic, and the profiles' (the Adversaries list's counts, a profile's timeline): never the article's own.
        _heard.Select(c => c.Topic).Should().OnlyContain(t => t == ChangeFeed.ArticleTags(article) || t == ChangeFeed.Adversaries)
            .And.Contain(ChangeFeed.ArticleTags(article)).And.NotContain(ChangeFeed.Article(article));
        BriefPage.Follow(await _db.Briefs().GetAsync(day), day).Should().NotContain(ChangeFeed.ArticleTags(article));
    }

    [Fact]
    public async Task AnActionAndItsComments_AreHeard_AndTheListHearsTheAction()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var actions = _db.Actions();
        var number = await actions.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team });
        var id = (await actions.GetAsync(number))!.Action.Id;
        _heard.Select(c => c.Topic).Should().Contain([ChangeFeed.Action(id), ChangeFeed.Actions]);
        _heard.Clear();

        await actions.AddCommentAsync(id, "Started the sweep.");

        _heard.Select(c => c.Topic).Should().Equal(ChangeFeed.Action(id));
    }

    [Fact]
    public async Task ARotationRequest_AndItsApproval_AreHeardAsTheRotation()
    {
        var rotation = _db.Rotation();
        await rotation.AddMemberAsync(_db.User.UserId, "Robin Analyst");
        await rotation.AddMemberAsync("dev:sam", "Sam");
        var week = Tide.Application.Rotation.RotationService.WeekStart(_db.Clock.Today).AddDays(7);
        await rotation.SetWeekAsync(week, _db.User.UserId);
        _heard.Select(c => c.Topic).Should().OnlyContain(t => t == ChangeFeed.Rotation, "a member and a week are the rotation");
        _heard.Clear();

        var change = await rotation.RequestAsync(new Tide.Application.Rotation.RotationRequest { PartnerId = "dev:sam", GiveFrom = week, GiveTo = week });

        _heard.Select(c => c.Topic).Should().Contain(ChangeFeed.Rotation);
        _heard.Clear();
        _db.User.UserId = "dev:sam";
        await rotation.ApproveAsync(change.Id);
        _heard.Select(c => c.Topic).Should().Contain(ChangeFeed.Rotation);
    }

    [Fact]
    public async Task ADaysNumbers_AreHeardByDay_AndTheMetricsKept_ByEveryDay()
    {
        var metrics = _db.Metrics();
        var group = await metrics.AddGroupAsync("Queue");
        var id = await metrics.SaveMetricAsync(new Tide.Application.Metrics.MetricForm { GroupId = group, Name = "Open tickets" });
        _heard.Select(c => c.Topic).Should().OnlyContain(t => t == ChangeFeed.Metrics);
        _heard.Clear();
        var day = new DateOnly(2026, 10, 22);

        await metrics.SaveValueAsync(id, day, 12, null);
        await metrics.SaveValueAsync(id, day, null, null);

        _heard.Should().HaveCount(2).And.OnlyContain(c => c.Topic == ChangeFeed.MetricsDay(day) && c.ActorName == "Robin Analyst",
            "entering a value and clearing it are both heard");
        ChangeFeed.IsMetricsOf(_heard[0], day).Should().BeTrue();
        ChangeFeed.IsMetricsOf(_heard[0], day.AddDays(1)).Should().BeFalse();
        ChangeFeed.IsMetricsOf(new Change(ChangeFeed.Metrics, "x", "", Guid.Empty), day).Should().BeTrue();
    }

    [Fact]
    public async Task AChecklistTick_IsHeardAsItsAction()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var actions = _db.Actions();
        var number = await actions.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team });
        var id = (await actions.GetAsync(number))!.Action.Id;
        await actions.AddChecklistItemAsync(id, "Check the proxy logs");
        var item = (await actions.ChecklistAsync(id)).Single();
        _heard.Clear();

        await actions.SetChecklistItemAsync(item.Id, true);

        _heard.Select(c => c.Topic).Should().Equal(ChangeFeed.Action(id));
    }

    [Fact]
    public async Task ASaveThatFails_TellsNoOne()
    {
        var id = await _db.Articles().CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        _heard.Clear();
        _db.BeforeSave = _ => throw new DbUpdateException("the database said no");

        var save = () => _db.Articles().SetStatusAsync(id, ArticleStatus.Ready);

        await save.Should().ThrowAsync<Exception>();
        _heard.Should().BeEmpty();
    }

    [Fact]
    public void SavingSynchronously_IsHeardToo()
    {
        using var ctx = _db.Context();
        var team = new Team { Name = "Ops" };
        ctx.Teams.Add(team);
        ctx.SaveChanges();
        _heard.Select(c => c.Topic).Should().Equal(ChangeFeed.Teams);
        _heard.Clear();

        var action = new ActionItem { Title = "Patch", TeamId = team.Id, Number = 1 };
        ctx.Actions.Add(action);
        ctx.SaveChanges();

        _heard.Select(c => c.Topic).Should().BeEquivalentTo([ChangeFeed.Action(action.Id), ChangeFeed.Actions]);
    }

    [Fact]
    public void Topics_ForEachThingAPageShows()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        string[] Of(object entity) => ChangeInterceptor.TopicsOf(entity).ToArray();

        Of(new ArticleBullet { ArticleId = a }).Should().Equal(ChangeFeed.Article(a));
        Of(new ArticleScreenshot { ArticleId = a }).Should().Equal(ChangeFeed.Article(a));
        Of(new ArticleTag { ArticleId = a }).Should().Equal(ChangeFeed.Article(a));
        Of(new ArticleTechnique { ArticleId = a }).Should().Equal(ChangeFeed.Article(a));
        Of(new ArticleIndicator { ArticleId = a }).Should().Equal(ChangeFeed.Article(a));
        Of(new ArticleLink { ArticleId = a }).Should().Equal(ChangeFeed.Article(a));
        Of(new ArticleCve { ArticleId = a }).Should().Equal(ChangeFeed.Article(a));
        Of(new ArticleAdversary { ArticleId = a }).Should().Equal(ChangeFeed.ArticleTags(a), ChangeFeed.Adversaries);
        Of(new ArticleProduct { ArticleId = a }).Should().Equal(ChangeFeed.ArticleTags(a));
        Of(new BriefItem { BriefId = b }).Should().Equal(ChangeFeed.Brief(b));
        Of(new BriefSend { BriefId = b }).Should().Equal(ChangeFeed.Brief(b));
        Of(new ActionComment { ActionId = a }).Should().Equal(ChangeFeed.Action(a));
        Of(new HuntQuery { ActionId = a }).Should().Equal(ChangeFeed.Action(a));
        Of(new Article { Id = a }).Should().Equal(ChangeFeed.Article(a), ChangeFeed.Articles);
        Of(new Article { Id = a, StoryId = b }).Should().Equal(ChangeFeed.Article(a), ChangeFeed.Articles, ChangeFeed.Story(b));
        Of(new RotationChange()).Should().Equal(ChangeFeed.Rotation);
        Of(new RotationOverride()).Should().Equal(ChangeFeed.Rotation);
        Of(new RotationWeek()).Should().Equal(ChangeFeed.Rotation);
        Of(new RotationMember()).Should().Equal(ChangeFeed.Rotation);
        Of(new Story { Id = a }).Should().Equal(ChangeFeed.Stories, ChangeFeed.Story(a));
        Of(new ActionChecklistItem { ActionId = a }).Should().Equal(ChangeFeed.Action(a));
        Of(new ActionEvent { ActionId = a }).Should().Equal(ChangeFeed.Action(a));
        Of(new MetricValue { BriefDate = new DateOnly(2026, 10, 22) }).Should().Equal("metrics-day:2026-10-22");
        Of(new MetricDefinition()).Should().Equal(ChangeFeed.Metrics);
        Of(new MetricGroup()).Should().Equal(ChangeFeed.Metrics);
        Of(new StackProduct()).Should().Equal(ChangeFeed.Stack);
        Of(new Adversary()).Should().Equal(ChangeFeed.Adversaries);
        Of(new Team()).Should().Equal(ChangeFeed.Teams);
        Of(new TeamMember()).Should().Equal(ChangeFeed.Teams);
        Of(new AuditLogEntry()).Should().BeEmpty();
    }
}
