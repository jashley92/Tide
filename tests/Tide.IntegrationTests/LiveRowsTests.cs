using FluentAssertions;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.TestSupport;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

public class LiveDiffTests
{
    private sealed record Row(string Id, string Shows);

    private static LiveDiff<string> Diff(Row[] before, Row[] after) => LiveDiff.Of(before, after, r => r.Id, r => r.Shows);

    [Fact]
    public void ArrivedLeftAndChanged_AreTold_ByKey()
    {
        var diff = Diff(
            [new("a", "1"), new("b", "1"), new("c", "1"), new("d", "1")],
            [new("e", "1"), new("a", "1"), new("c", "2")]);

        diff.Arrived.Should().Equal("e");
        diff.Left.Should().Equal("b", "d");
        diff.Changed.Should().Equal("c");
        diff.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void ARowThatLeft_StandsBeforeTheNextRowThatStayed_OrAtTheEnd()
    {
        var diff = Diff([new("a", ""), new("b", ""), new("c", ""), new("d", ""), new("e", "")], [new("a", ""), new("d", "")]);

        diff.LeftBefore.Should().BeEquivalentTo(new Dictionary<string, string> { ["b"] = "d", ["c"] = "d" });
        diff.Left.Should().Equal("b", "c", "e");
        diff.LeftBefore.Should().NotContainKey("e", "nothing stayed after it: it goes at the end");
    }

    [Fact]
    public void TheSameRows_AreNoChange_AndARepeatedKeyIsOneRow()
    {
        Diff([new("a", "1"), new("b", "1")], [new("a", "1"), new("b", "1")]).IsEmpty.Should().BeTrue();
        var diff = Diff([new("a", "1"), new("a", "1")], [new("a", "1"), new("a", "2")]);
        diff.IsEmpty.Should().BeTrue("the first of a repeated key is the row");
    }

    [Fact]
    public void ARecord_IsItsOwnSignature()
    {
        var diff = LiveDiff.Of([new Row("a", "1")], [new Row("a", "1"), new Row("b", "1")], r => r.Id, r => r);
        diff.Arrived.Should().Equal("b");
        diff.Changed.Should().BeEmpty();
    }
}

public class LiveMarksTests
{
    private readonly ManualClock _time = new();
    private int _renders;

    private LiveMarks<string> Marks() => new(work => work(), () => _renders++, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), _time);

    private static LiveDiff<string> Diff(string[] before, string[] after, params string[] changed) =>
        LiveDiff.Of(before.Select(k => (k, v: 0)), after.Select(k => (k, v: changed.Contains(k) ? 1 : 0)), r => r.k, r => r.v);

    [Fact]
    public void Arrivals_AndChanges_AreHighlighted_UntilTheGlowEnds()
    {
        using var marks = Marks();
        marks.Apply(Diff(["a", "b"], ["n", "a", "b"], "b"), unseen: true);

        marks.Css("n").Should().Be("tide-live-arrived");
        marks.Css("b").Should().Be("tide-live-changed");
        marks.Css("a").Should().BeEmpty();
        marks.IsMarked("n").Should().BeTrue();

        _time.Advance(TimeSpan.FromSeconds(2));

        marks.Css("n").Should().BeEmpty();
        marks.Css("b").Should().BeEmpty();
        _renders.Should().Be(1, "the page re-renders once as the batch's marks clear");
        marks.IsUnseen("n").Should().BeTrue("the New marker stays until the row is looked at");
        marks.Seen("n").Should().BeTrue();
        marks.Seen("n").Should().BeFalse("it was seen once");
        marks.IsUnseen("n").Should().BeFalse();
    }

    [Fact]
    public void ARowMarkedAgain_KeepsItsHighlight_UntilTheLaterGlowEnds()
    {
        using var marks = Marks();
        marks.Mark(["a"], LiveMark.Changed);
        _time.Advance(TimeSpan.FromSeconds(1));
        marks.Mark(["a"], LiveMark.Arrived);
        _time.Advance(TimeSpan.FromSeconds(1));

        marks.Css("a").Should().Be("tide-live-arrived", "the first batch's clearing leaves the later mark");
        marks.IsUnseen("a").Should().BeFalse("only arrivals asked to be unseen are");
        _time.Advance(TimeSpan.FromSeconds(1));
        marks.Css("a").Should().BeEmpty();
    }

    [Fact]
    public void ARowThatLeft_IsAGhost_SayingWhat_ForAMoment()
    {
        using var marks = Marks();
        marks.Apply(Diff(["a", "b", "c"], ["a"]), k => $"{k} went");

        marks.Ghosts.Should().Equal(
            new LiveGhost<string>("b", "b went", AtEnd: true, Before: null!),
            new LiveGhost<string>("c", "c went", AtEnd: true, Before: null!));
        _time.Advance(TimeSpan.FromSeconds(1));
        marks.Ghosts.Should().BeEmpty();
        _renders.Should().Be(1);
    }

    [Fact]
    public void AGhost_StandsBeforeTheRowThatFollowedIt_AndReplacesItsOwnMarks()
    {
        using var marks = Marks();
        marks.Apply(Diff(["x"], ["x", "b"]), unseen: true);
        marks.Apply(Diff(["a", "b", "c"], ["a", "c"]), k => k);

        marks.Ghosts.Should().ContainSingle().Which.Should().Be(new LiveGhost<string>("b", "b", false, "c"));
        marks.Css("b").Should().BeEmpty();
        marks.IsUnseen("b").Should().BeFalse();
    }

    [Fact]
    public void WithoutAGhostText_RowsThatLeftLeaveNothing_AndNoChangeMarksNothing()
    {
        using var marks = Marks();
        marks.Apply(Diff(["a"], []));
        marks.Apply(Diff(["a"], ["a"]));
        marks.Mark([], LiveMark.Changed);

        marks.Ghosts.Should().BeEmpty();
        _time.Pending.Should().Be(0, "nothing was marked, so nothing waits to clear");
    }

    [Fact]
    public void Clear_DropsEveryMark_AndAfterDispose_NothingIsMarkedOrRerendered()
    {
        var marks = Marks();
        marks.Apply(Diff(["a", "b"], ["n", "a"], "a"), k => k, unseen: true);
        marks.Clear();
        marks.Css("n").Should().BeEmpty();
        marks.IsUnseen("n").Should().BeFalse();
        marks.Ghosts.Should().BeEmpty();

        marks.Mark(["z"], LiveMark.Changed);
        marks.Dispose();
        marks.Dispose();   // twice is once
        marks.Mark(["y"], LiveMark.Changed);
        marks.Css("y").Should().BeEmpty();
        _time.Advance(TimeSpan.FromSeconds(5));
        _renders.Should().Be(0);
    }

    [Fact]
    public void AClearingThatFails_AsThePageCloses_DoesntEscape()
    {
        using var marks = new LiveMarks<string>(_ => Task.FromException(new ObjectDisposedException("the page")), () => _renders++,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), _time);
        marks.Mark(["a"], LiveMark.Arrived);

        var clear = () => _time.Advance(TimeSpan.FromSeconds(1));

        clear.Should().NotThrow();
    }
}

public class TriageNewsTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static FeedItem Item(FeedItemStatus status, string? by = null, int fetchedMinutes = -60, string title = "An item") => new()
    {
        Title = title, Status = status, StatusByName = by, FetchedAtUtc = ReadAt.AddMinutes(fetchedMinutes),
    };

    [Theory]
    [InlineData(FeedItemStatus.Dismissed, "Sam", "Sam dismissed “Edge VPN flaw”")]
    [InlineData(FeedItemStatus.Shortlisted, "Sam", "Sam shortlisted “Edge VPN flaw”")]
    [InlineData(FeedItemStatus.Duplicate, "Sam", "Sam marked “Edge VPN flaw” as a duplicate")]
    [InlineData(FeedItemStatus.Promoted, "Sam", "Sam promoted “Edge VPN flaw”")]
    [InlineData(FeedItemStatus.Dismissed, null, "Robin dismissed “Edge VPN flaw”")]
    public void ARowThatLeft_SaysWhoDidWhat(FeedItemStatus now, string? by, string expected) =>
        TriageNews.Left(FeedItemStatus.New, "Edge VPN flaw", Item(now, by), "Robin").Should().Be(expected);

    [Fact]
    public void ARowPutBack_IsTheColleaguesHeard_AndOneStillHereOrGone_LeftTheList()
    {
        TriageNews.Left(FeedItemStatus.Shortlisted, "Flaw", Item(FeedItemStatus.New), "Robin").Should().Be("Robin put back “Flaw”");
        TriageNews.Left(FeedItemStatus.New, "Flaw", Item(FeedItemStatus.New), "Robin").Should().Be("“Flaw” left this list");
        TriageNews.Left(FeedItemStatus.New, new string('x', 70), null, "Robin").Should().Be($"“{new string('x', 60)}…” left this list");
    }

    [Fact]
    public void AColleaguesBatch_AndThePoll_AreOneToast()
    {
        var left = new FeedItem?[] { Item(FeedItemStatus.Dismissed, "Sam"), Item(FeedItemStatus.Dismissed, "Sam"), Item(FeedItemStatus.Dismissed, "Sam"), null };
        var arrived = new[] { Item(FeedItemStatus.New, fetchedMinutes: 1), Item(FeedItemStatus.New, fetchedMinutes: 2) };

        TriageNews.Summary(FeedItemStatus.New, left, arrived, ReadAt, "Sam").Should().Be("Sam dismissed 3 items · 2 new from the feeds");
    }

    [Fact]
    public void APoll_Alone_SaysHowManyCame()
    {
        TriageNews.Summary(FeedItemStatus.New, [], [Item(FeedItemStatus.New, fetchedMinutes: 1)], ReadAt, "Someone").Should().Be("1 new item from the feeds");
        TriageNews.Summary(FeedItemStatus.Shortlisted, [], [], ReadAt, "Someone", newElsewhere: 4).Should().Be("4 new items from the feeds");
        TriageNews.Summary(FeedItemStatus.Shortlisted, [], [], ReadAt, "Someone", newElsewhere: -1).Should().BeNull();
    }

    [Fact]
    public void ArrivalsInOtherTabs_SayWhoSentThem_AndItemsPutBackInNewSaySo()
    {
        TriageNews.Summary(FeedItemStatus.Shortlisted, [], [Item(FeedItemStatus.Shortlisted, "Sam"), Item(FeedItemStatus.Shortlisted, "Sam")], ReadAt, "Sam")
            .Should().Be("Sam shortlisted 2 items");
        TriageNews.Summary(FeedItemStatus.Duplicate, [Item(FeedItemStatus.New)], [Item(FeedItemStatus.Duplicate, "Ash")], ReadAt, "Ash and Sam")
            .Should().Be("Ash and Sam put back 1 item · Ash marked 1 item as a duplicate");
        TriageNews.Summary(FeedItemStatus.New, [Item(FeedItemStatus.Duplicate, "Ash"), Item(FeedItemStatus.Duplicate, "Ash")], [Item(FeedItemStatus.New)], ReadAt, "Ash")
            .Should().Be("Ash marked 2 items as duplicates · Ash put back 1 item");
    }

    [Fact]
    public void ARowThatOnlyCameIntoThePage_IsntNews()
    {
        var left = new FeedItem?[] { Item(FeedItemStatus.Dismissed, "Sam") };
        TriageNews.Summary(FeedItemStatus.New, left, [Item(FeedItemStatus.New)], ReadAt, "Sam", entered: 0).Should().Be("Sam dismissed 1 item");
        TriageNews.Summary(FeedItemStatus.New, [], [Item(FeedItemStatus.New, fetchedMinutes: 1), Item(FeedItemStatus.New)], ReadAt, "Sam", entered: 1)
            .Should().Be("1 new item from the feeds", "the newest-fetched arrival is the one that entered");
    }

    [Fact]
    public void NothingToSay_IsNoToast() =>
        TriageNews.Summary(FeedItemStatus.New, [Item(FeedItemStatus.New), null], [], ReadAt, "Sam").Should().BeNull();

    [Fact]
    public void TheSignature_ChangesWithWhatTheRowShows()
    {
        var source = new Source { Name = "s" };
        var item = Item(FeedItemStatus.New, title: "Flaw");
        var row = new InboxRow(item, source, null, []);
        var same = new InboxRow(item, source, null, []);
        var covered = row with { Coverage = [new Coverage("CVE-2026-1", Guid.NewGuid(), "Article", null)] };

        TriageNews.Signature(row).Should().Be(TriageNews.Signature(same));
        TriageNews.Signature(covered).Should().NotBe(TriageNews.Signature(row));
        TriageNews.Signature(row with { SameCve = [Guid.NewGuid()] }).Should().NotBe(TriageNews.Signature(row));
    }
}
