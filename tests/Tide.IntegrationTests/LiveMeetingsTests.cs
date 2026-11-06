using FluentAssertions;
using Tide.Application.Briefs;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>A brief meeting several people are in: following the presenter, hands, hand-off, takeover, ending.</summary>
public sealed class LiveMeetingsTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 1);
    private static readonly Guid BriefId = Guid.NewGuid(), Article = Guid.NewGuid();

    private readonly TestClock _time = new(new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly LiveMeetings _meetings;

    public LiveMeetingsTests() => _meetings = new LiveMeetings(_time);

    public void Dispose() => _meetings.Dispose();

    private LiveMeetingView View => _meetings.Get(Day)!;

    private static readonly Guid PatsPage = Guid.NewGuid();

    private IMeetingSeat Start() => _meetings.Start(Day, BriefId, "pat", "Pat", "c-pat", PatsPage, 0);

    private IMeetingSeat Join(string id, string name, bool canPresent = false, int slide = 0) =>
        _meetings.Join(Day, id, name, canPresent, "c-" + id, Guid.NewGuid(), slide)!;

    [Fact]
    public void Start_MakesTheMeetingLive_WithMePresenting_AndASecondStartIsRefused()
    {
        _meetings.Get(Day).Should().BeNull();
        _meetings.Join(Day, "sam", "Sam", false, "c", Guid.NewGuid(), 0).Should().BeNull("nothing to join yet");

        Start();

        View.Should().Match<LiveMeetingView>(v => v.PresenterId == "pat" && v.PresenterHere && !v.CanTakeOver && v.BriefId == BriefId);
        _meetings.Live().Should().ContainSingle();
        FluentActions.Invoking(() => _meetings.Start(Day, BriefId, "robin", "Robin", "c-robin", Guid.NewGuid(), 0)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ThePresentersSlide_IsTheMeetings_FollowersMoveWithIt_AndWhoeverWanderedOffShowsAsAway()
    {
        var pat = Start();
        var sam = Join("sam", "Sam");
        var lee = Join("lee", "Lee");
        var heard = new List<MeetingChange>();
        _meetings.Changed += heard.Add;

        pat.At(3);
        View.Slide.Should().Be(3);
        View.People.Should().OnlyContain(p => p.Slide == 3, "those following move with the presenter");
        heard.Should().ContainSingle("one change for the move, not one more per follower").Which.Origin.Should().Be(PatsPage);

        sam.At(1);
        View.Slide.Should().Be(3, "only the presenter moves the meeting");
        View.People.Single(p => p.UserId == "sam").Should().Match<MeetingPerson>(p => View.Away(p));
        sam.At(1);
        heard.Should().HaveCount(2, "the repeat is no news");

        pat.At(4);
        View.People.Single(p => p.UserId == "sam").Slide.Should().Be(1, "Sam wandered off: they stay where they are");
        View.People.Single(p => p.UserId == "lee").Slide.Should().Be(4);
        sam.At(4, following: true);
        pat.At(5);
        View.People.Should().OnlyContain(p => p.Slide == 5, "following again");

        View.People.Select(p => p.Name).Should().Equal("Pat", "Lee", "Sam");
        _ = lee;
    }

    [Fact]
    public void SomeoneInTwoTabs_IsOnePerson_OnTheSlideTheyMovedLast_AndLeavesWithTheLastTab()
    {
        Start();
        var one = Join("sam", "Sam");
        var two = Join("sam", "Sam", slide: 2);
        two.At(2);
        one.At(4);

        View.People.Single(p => p.UserId == "sam").Slide.Should().Be(4);
        one.Dispose();
        View.People.Should().Contain(p => p.UserId == "sam");
        two.Dispose();
        two.Dispose();
        View.People.Should().NotContain(p => p.UserId == "sam");
    }

    [Fact]
    public void Hands_GoUpOnAnArticle_ComeDownWhenLoweredOrCleared_AndOnlyThePresenterClearsThem()
    {
        var pat = Start();
        var sam = Join("sam", "Sam");
        var lee = Join("lee", "Lee");

        _meetings.Hand(sam, up: true, Article);
        _meetings.Hand(lee, up: true);
        View.Hands.Select(h => (h.Name, h.ArticleId)).Should().Equal(("Sam", (Guid?)Article), ("Lee", null));

        _meetings.ClearHands(sam, "lee").Should().BeFalse("not the presenter");
        _meetings.Hand(lee, up: false);
        _meetings.Hand(lee, up: false);
        View.Hands.Should().ContainSingle();

        _meetings.Hand(lee, up: true);
        _meetings.ClearHands(pat, "sam").Should().BeTrue();
        View.Hands.Single().Name.Should().Be("Lee");
        _meetings.ClearHands(pat).Should().BeTrue();
        View.Hands.Should().BeEmpty();

        _meetings.Hand(sam, up: true);
        sam.Dispose();
        View.Hands.Should().BeEmpty("a hand goes down when its owner leaves");
    }

    [Fact]
    public void Summon_CountsUp_ForThePresenterOnly()
    {
        var pat = Start();
        var sam = Join("sam", "Sam");

        pat.At(2);
        sam.At(4);
        _meetings.Summon(sam).Should().BeFalse();
        _meetings.Summon(pat).Should().BeTrue();
        View.Summons.Should().Be(1);
        View.People.Single(p => p.UserId == "sam").Slide.Should().Be(2, "brought back to the presenter's slide");
        pat.At(3);
        View.People.Single(p => p.UserId == "sam").Slide.Should().Be(3, "and following again");
    }

    [Fact]
    public void Lock_BringsEveryoneBack_AndKeepsThemOnThePresentersSlide_UntilUnlocked()
    {
        var pat = Start();
        var sam = Join("sam", "Sam");
        var robin = Join("robin", "Robin", canPresent: true);
        pat.At(2);
        sam.At(5);

        _meetings.Lock(sam, true).Should().BeFalse("only the presenter locks");
        _meetings.Lock(pat, true).Should().BeTrue();
        _meetings.Lock(pat, true).Should().BeFalse("already locked: no news");
        View.Should().Match<LiveMeetingView>(v => v.Locked && v.Summons == 1);
        View.People.Should().OnlyContain(p => p.Slide == 2, "locking brings back whoever wandered off");

        sam.At(6);
        View.People.Single(p => p.UserId == "sam").Slide.Should().Be(2, "locked: Sam can't browse off");
        pat.At(3);
        View.People.Should().OnlyContain(p => p.Slide == 3);

        _meetings.HandTo(pat, "robin").Should().BeTrue();
        View.Locked.Should().BeTrue("a new presenter keeps the lock");
        _meetings.Lock(pat, false).Should().BeFalse("no longer the presenter");
        _meetings.Lock(robin, false).Should().BeTrue();
        sam.At(6);
        View.People.Single(p => p.UserId == "sam").Slide.Should().Be(6, "unlocked: free to browse");
    }

    [Fact]
    public void ADroppedConnection_IsAway_AndAPresenterWhoseDropsStartsTheGrace_UntilTheyreBack()
    {
        var pat = Start();
        var robin = Join("robin", "Robin", canPresent: true);

        _meetings.SetConnected("c-robin", false);
        View.People.Select(p => p.Name).Should().Equal("Pat");
        _meetings.SetConnected("c-robin", true);
        View.People.Select(p => p.Name).Should().Equal("Pat", "Robin");

        _meetings.SetConnected("c-pat", false);
        View.PresenterHere.Should().BeFalse("the lid closed: away at once, not when the circuit is given up on");
        _time.Now += LiveMeetings.Grace;
        View.CanTakeOver.Should().BeTrue();
        _meetings.SetConnected("c-pat", true);
        View.Should().Match<LiveMeetingView>(v => v.PresenterHere && !v.CanTakeOver, "back in time");
        _meetings.SetConnected("", false);
        _ = (pat, robin);
    }

    [Fact]
    public void ALock_DoesntHoldAnyoneOnceThePresenterHasGone()
    {
        var pat = Start();
        var sam = Join("sam", "Sam");
        _meetings.Lock(pat, true);
        View.HeldOn("sam").Should().BeTrue();

        _meetings.SetConnected("c-pat", false);
        View.HeldOn("sam").Should().BeFalse();
        sam.At(4);
        View.People.Single().Slide.Should().Be(4, "free to browse while the presenter's away");
    }

    [Fact]
    public void AHandOff_TakesTheFollowersToTheNewPresentersSlide_WithItsName()
    {
        var pat = Start();
        var sam = Join("sam", "Sam");
        var robin = Join("robin", "Robin", canPresent: true);
        robin.At(5, key: "article:x");
        pat.At(2, key: "metrics");

        _meetings.HandTo(pat, "robin").Should().BeTrue();

        View.Should().Match<LiveMeetingView>(v => v.Slide == 5 && v.SlideKey == "article:x");
        View.People.Should().OnlyContain(p => !View.Away(p), "Sam, following, and Pat, now following, are on Robin's slide");
        _ = sam;
    }

    [Fact]
    public void HandTo_PassesPresentingToSomeoneThereWhoCanPresent_OnTheirSlide()
    {
        var pat = Start();
        var sam = Join("sam", "Sam");
        var robin = Join("robin", "Robin", canPresent: true);
        View.People.Single(p => p.UserId == "robin").Slide.Should().Be(0, "a follower joins on the meeting's slide");
        robin.At(2);   // to their own article, to present it

        _meetings.HandTo(pat, "sam").Should().BeFalse("Sam can't present");
        _meetings.HandTo(robin, "pat").Should().BeFalse("only the presenter hands off");
        _meetings.HandTo(pat, "nobody").Should().BeFalse();
        _meetings.HandTo(pat, "robin").Should().BeTrue();

        View.Should().Match<LiveMeetingView>(v => v.PresenterId == "robin" && v.PresenterName == "Robin" && v.Slide == 2);
        pat.At(5);
        View.Slide.Should().Be(2);
        robin.At(4);
        View.Slide.Should().Be(4);
        _meetings.Summon(sam).Should().BeFalse();
    }

    [Fact]
    public void ThePresenterOpensAnAction_ForTheMeeting_AndMovingOnOrHandingOffClosesIt()
    {
        var pat = Start();
        var sam = Join("sam", "Sam");
        var robin = Join("robin", "Robin", canPresent: true);
        var heard = new List<MeetingChange>();
        _meetings.Changed += heard.Add;

        _meetings.Open(sam, 12).Should().BeFalse("only the presenter opens one for the meeting");
        _meetings.Open(pat, 12).Should().BeTrue();
        View.Action.Should().Be(12);
        _meetings.Open(pat, 12).Should().BeFalse("no news");
        heard.Should().ContainSingle().Which.Origin.Should().Be(PatsPage);

        pat.At(0);
        View.Action.Should().Be(12, "still on the same slide");
        sam.At(3);
        View.Action.Should().Be(12, "a follower moving doesn't close it");
        pat.At(2);
        View.Action.Should().BeNull("it was the last slide's");

        _meetings.Open(pat, 7).Should().BeTrue();
        _meetings.Open(pat, null).Should().BeTrue();
        View.Action.Should().BeNull();

        _meetings.Open(pat, 7);
        _meetings.HandTo(pat, "robin").Should().BeTrue();
        View.Action.Should().BeNull("the new presenter starts with nothing open");
    }

    [Fact]
    public void ThePresenterShowsAProductsCard_ForTheMeeting_OneThingOpenAtATime_AndMovingOnOrHandingOffClosesIt()
    {
        var pat = Start();
        var sam = Join("sam", "Sam");
        Join("robin", "Robin", canPresent: true);
        var product = MeetingCard.ForProduct(Guid.NewGuid());

        _meetings.Show(sam, product).Should().BeFalse("only the presenter shows one to the meeting");
        _meetings.Show(pat, product).Should().BeTrue();
        View.Card.Should().Be(product);
        _meetings.Show(pat, product).Should().BeFalse("no news");

        _meetings.Open(pat, 12).Should().BeTrue();
        View.Should().Match<LiveMeetingView>(v => v.Action == 12 && v.Card == null, "an action replaces the card");
        _meetings.Show(pat, product);
        View.Should().Match<LiveMeetingView>(v => v.Action == null && v.Card == product, "and the card the action");
        _meetings.Open(pat, null).Should().BeFalse("no action to close");
        View.Card.Should().Be(product, "closing no action leaves the card");

        pat.At(0);
        View.Card.Should().Be(product, "still on the same slide");
        pat.At(2);
        View.Card.Should().BeNull("it was the last slide's");

        _meetings.Show(pat, product);
        _meetings.Show(pat, null).Should().BeTrue();
        View.Card.Should().BeNull();

        _meetings.Show(pat, product);
        _meetings.HandTo(pat, "robin").Should().BeTrue();
        View.Card.Should().BeNull("the new presenter starts with nothing open");
    }

    [Fact]
    public void WhatsOpen_StaysWhenTheSameSlideIsRenumbered_AndGoesWhenAnotherSlideTakesItsNumber()
    {
        var pat = Start();
        var product = MeetingCard.ForProduct(Guid.NewGuid());
        pat.At(4, key: "article:a");
        _meetings.Show(pat, product);

        // A colleague added the opening note: the same article, one slide later.
        pat.At(5, key: "article:a");
        View.Card.Should().Be(product, "the presenter is still on the article it was opened over");
        _meetings.Open(pat, 7);
        pat.At(4, key: "article:a");
        View.Action.Should().Be(7, "an action stays too");

        // The article left the brief: the next one is now at its number.
        pat.At(4, key: "article:b");
        View.Should().Match<LiveMeetingView>(v => v.Action == null && v.Card == null, "it was the other article's");
    }

    [Fact]
    public void APopup_FollowsThePresentersUnlessClosedHere_AndALockHoldsIt()
    {
        var popup = new MeetingPopup();
        MeetingCard a = MeetingCard.ForProduct(Guid.NewGuid()), b = MeetingCard.ForProduct(Guid.NewGuid());

        popup.Sync(a, following: true, held: false).Should().BeTrue();
        (popup.Shown, popup.Own).Should().Be((a, false));
        popup.Sync(a, following: true, held: false).Should().BeFalse("no news");

        popup.Close();
        popup.Sync(a, following: true, held: false).Should().BeFalse("closed here, it stays closed");
        popup.Shown.Should().BeNull();
        popup.Sync(b, following: true, held: false).Should().BeTrue("until the presenter opens another");
        popup.Shown.Should().Be(b);

        popup.Sync(null, following: false, held: false).Should().BeFalse("not following: what's open stays");
        popup.Shown.Should().Be(b);
        popup.Sync(null, following: true, held: false).Should().BeTrue("their closing closes it here");
        popup.Shown.Should().BeNull();

        popup.Open(a, presenting: false, inMeeting: true);
        popup.Own.Should().BeTrue("a follower's own look");
        popup.Sync(null, following: true, held: false).Should().BeFalse("the presenter's closing leaves it");
        popup.Shown.Should().Be(a);
        popup.Sync(b, following: true, held: false).Should().BeTrue("the presenter's opening replaces it");
        (popup.Shown, popup.Own).Should().Be((b, false));

        popup.Reset();
        popup.Sync(b, following: true, held: false).Should().BeTrue("after a move, the presenter's shows again");
        popup.Close();
        popup.Sync(b, following: true, held: true).Should().BeTrue("held by the lock, it shows");
        popup.Sync(b, following: true, held: true).Should().BeFalse();
        popup.Sync(null, following: true, held: true).Should().BeTrue();
        popup.Shown.Should().BeNull();

        popup.Open(a, presenting: true, inMeeting: true);
        (popup.Shown, popup.Own).Should().Be((a, false));
        popup.Sync(a, following: true, held: false).Should().BeFalse("the presenter's own is the one seen");
        popup.Open(b, presenting: false, inMeeting: false);
        popup.Own.Should().BeFalse("with no meeting there's nobody else");
    }

    [Fact]
    public void WhenThePresenterDrops_SomeoneWhoCanPresent_CanTakeOverAfterTheGrace_UnlessTheyComeBack()
    {
        var pat = Start();
        var sam = Join("sam", "Sam");
        var robin = Join("robin", "Robin", canPresent: true);
        var heard = 0;
        _meetings.Changed += _ => heard++;

        pat.Dispose();
        View.PresenterHere.Should().BeFalse();
        _meetings.TakeOver(robin).Should().BeFalse("within the grace period: a reload");

        _time.Now += LiveMeetings.Grace;
        _meetings.Sweep();
        View.CanTakeOver.Should().BeTrue();
        heard.Should().Be(2, "leaving, then the grace running out");
        _meetings.Sweep();
        heard.Should().Be(2, "said once");

        _meetings.TakeOver(sam).Should().BeFalse("Sam can't present");
        _meetings.TakeOver(robin).Should().BeTrue();
        View.Should().Match<LiveMeetingView>(v => v.PresenterId == "robin" && v.PresenterHere);
    }

    [Fact]
    public void APresenterBackInALockedMeeting_BringsBackWhoeverBrowsedOffMeanwhile()
    {
        var pat = Start();
        var bob = Join("bob", "Bob");
        pat.At(2, key: "article:a");
        _meetings.Lock(pat, true);
        _meetings.SetConnected("c-pat", false);
        bob.At(5);
        var summons = View.Summons;

        _meetings.SetConnected("c-pat", true);

        View.Summons.Should().BeGreaterThan(summons, "pages follow again");
        View.People.Single(p => p.UserId == "bob").Slide.Should().Be(2);
        View.HeldOn("bob").Should().BeTrue();
    }

    [Fact]
    public void ADroppedSeat_LeftWhenItsConnectionWent_AndItsHandGoesDown()
    {
        var pat = Start();
        var ann = Join("ann", "Ann");
        _meetings.Hand(ann, up: true);
        var dropped = _time.Now;
        _meetings.SetConnected("c-ann", false);
        View.Hands.Should().BeEmpty();
        _time.Now += TimeSpan.FromMinutes(40);

        _meetings.End(pat)!["ann"].Should().Be(dropped);
    }

    [Fact]
    public void ThePresentersOtherTab_MovesWithThem()
    {
        var laptop = Start();
        var projector = _meetings.Join(Day, "pat", "Pat", true, "c-projector", Guid.NewGuid(), 0)!;
        laptop.At(3, key: "article:a");

        View.People.Single().Slide.Should().Be(3, "one person, on the slide either tab is on");
        projector.At(3, key: "article:a");
        View.Slide.Should().Be(3);
    }

    [Fact]
    public void AHand_GoesDownWhenNoneOfItsOwnersTabsIsThere_AndTheirLeaveIsTheLatest()
    {
        var pat = Start();
        var phone = Join("ann", "Ann");
        _meetings.Join(Day, "ann", "Ann", false, "c-ann-laptop", Guid.NewGuid(), 0);
        _meetings.Hand(phone, up: true);
        _meetings.SetConnected("c-ann-laptop", false);
        View.Hands.Should().ContainSingle("the phone is still there");

        _time.Now += TimeSpan.FromMinutes(5);
        var closed = _time.Now;
        phone.Dispose();
        View.Hands.Should().BeEmpty("neither tab is there now");

        _time.Now += TimeSpan.FromMinutes(30);
        _meetings.End(pat)!["ann"].Should().Be(closed, "there until the phone closed, not since the laptop dropped");
    }

    [Fact]
    public void APresenterWhoComesBackInTime_IsStillPresenting()
    {
        var pat = Start();
        var robin = Join("robin", "Robin", canPresent: true);
        pat.Dispose();
        Join("pat", "Pat", canPresent: true);

        _time.Now += LiveMeetings.Grace;
        _meetings.Sweep();
        View.Should().Match<LiveMeetingView>(v => v.PresenterId == "pat" && v.PresenterHere && !v.CanTakeOver);
        _meetings.TakeOver(robin).Should().BeFalse();
    }

    [Fact]
    public void End_StopsTheMeeting_AndSaysWhenEachPersonLeft()
    {
        var pat = Start();
        var sam = Join("sam", "Sam");
        Join("lee", "Lee");
        var start = _time.Now;
        _time.Now = start.AddMinutes(10);
        sam.Dispose();
        _time.Now = start.AddMinutes(25);

        var leaving = Join("lee2", "Lee two");
        _meetings.End(leaving).Should().BeNull("only the presenter ends it");

        var left = _meetings.End(pat)!;
        left.Should().BeEquivalentTo(new Dictionary<string, DateTimeOffset>
        {
            ["pat"] = start.AddMinutes(25), ["sam"] = start.AddMinutes(10), ["lee"] = start.AddMinutes(25), ["lee2"] = start.AddMinutes(25),
        });
        _meetings.Get(Day).Should().BeNull();
        _meetings.End(pat).Should().BeNull("already over");
        leaving.Dispose();
        leaving.At(3);
        _meetings.Get(Day).Should().BeNull("a seat in an ended meeting changes nothing");
    }

    [Fact]
    public void SomeoneWhoCanPresent_EndsAMeetingWhosePresenterHasGone()
    {
        var pat = Start();
        var robin = Join("robin", "Robin", canPresent: true);
        pat.Dispose();
        _meetings.End(robin).Should().BeNull("within the grace period");
        _time.Now += LiveMeetings.Grace;
        _meetings.End(robin).Should().NotBeNull();
    }

    [Fact]
    public void AClosedTab_LeavesTheMeeting_AndAMeetingNobodysInIsDroppedLater()
    {
        Start();
        Join("sam", "Sam");
        _meetings.RemoveCircuit("");
        _meetings.RemoveCircuit("c-sam");
        View.People.Should().ContainSingle();

        _meetings.RemoveCircuit("c-pat");
        _meetings.Get(Day).Should().NotBeNull("kept a while: the presenter may be reloading");
        _time.Now += LiveMeetings.Abandoned;
        _meetings.Sweep();
        _meetings.Get(Day).Should().BeNull();
    }

    [Fact]
    public void AListenerThatThrows_DoesntStopTheOthersHearing()
    {
        var heard = 0;
        _meetings.Changed += _ => throw new InvalidOperationException("a page closing");
        _meetings.Changed += _ => heard++;
        Start();
        heard.Should().Be(1);
    }
}
