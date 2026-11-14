using AwesomeAssertions;
using Tide.Application.Operations;
using Tide.Application.Preferences;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>
/// What's new: who reads which highlights, how a release's highlights and full notes are read, and when they're due.
/// </summary>
public class WhatsNewTests
{
    private static ReleaseVersion V(string s) => ReleaseVersion.Parse(s)!;

    private const string Highlights = """
        # What's new in v2.0.0

        Pages are new.

        ## Analysts

        - Triage changed.

        ## Presenters

        - Present changed.

        ## Administrators

        - Settings changed.

        ## Viewers and action owners

        - Actions changed.
        """;

    [Theory]
    [InlineData(new[] { AppRole.Analyst }, new[] { RoleGroup.Analysts, RoleGroup.Presenters })]
    [InlineData(new[] { AppRole.Admin }, new[] { RoleGroup.Analysts, RoleGroup.Presenters, RoleGroup.Administrators })]
    [InlineData(new[] { AppRole.Analyst, AppRole.Admin }, new[] { RoleGroup.Analysts, RoleGroup.Presenters, RoleGroup.Administrators })]
    [InlineData(new[] { AppRole.Viewer }, new[] { RoleGroup.ViewersAndActionOwners })]
    [InlineData(new[] { AppRole.ActionOwner }, new[] { RoleGroup.ViewersAndActionOwners })]
    [InlineData(new[] { AppRole.Viewer, AppRole.Analyst }, new[] { RoleGroup.Analysts, RoleGroup.Presenters })]
    [InlineData(new[] { AppRole.Reader }, new RoleGroup[0])]
    [InlineData(new[] { AppRole.Visitor }, new RoleGroup[0])]
    [InlineData(new AppRole[0], new RoleGroup[0])]
    public void EachRole_ReadsItsGroups_AndReadersAndVisitorsReadNone(AppRole[] roles, RoleGroup[] expected) =>
        RoleGroups.For(roles).Should().Equal(expected);

    [Fact]
    public void AGroupsTitle_SaysWhoItsFor() =>
        RoleGroups.Title(RoleGroup.ViewersAndActionOwners).Should().Be("For viewers and action owners");

    [Fact]
    public void AHighlightsFile_IsReadAsAnIntroAndASectionPerGroup()
    {
        var h = ReleaseHighlights.Parse(V("2.0.0"), Highlights)!;
        h.Intro.Should().Be("Pages are new.");
        h.Sections.Should().HaveCount(4);
        h.Sections[RoleGroup.Analysts].Should().Be("- Triage changed.");
        h.For([RoleGroup.ViewersAndActionOwners]).Should().ContainSingle().Which.Markdown.Should().Be("- Actions changed.");
    }

    [Fact]
    public void ASectionThatIsntAGroups_OrIsEmpty_IsLeftOut_AndAFileWithNoneIsNoHighlights()
    {
        var h = ReleaseHighlights.Parse(V("2.0.0"), "## Analysts\n\n- One.\n\n## Operators\n\n- Not shown.\n\n## Viewers and action owners\n\n")!;
        h.Sections.Keys.Should().Equal(RoleGroup.Analysts);
        h.Sections[RoleGroup.Analysts].Should().NotContain("Not shown");
        ReleaseHighlights.Parse(V("2.0.0"), "# Title\n\nJust an intro.\n").Should().BeNull("a release without highlights shows nothing");
    }

    [Fact]
    public void TheFullNotes_AreEachVersionsSection_AsTheReleaseScriptReadsThem()
    {
        var notes = ReleaseNotesCatalog.NotesSections("""
            ## How releases ship

            ### Versions

            Not a release.

            ## Notes

            ### v2.0.0

            Upgrade as usual.

            **New**
            - A thing.

            ### v1.0.1

            A fix.

            ---

            Not the fix's.
            ### v1.0.0
            """);
        notes.Keys.Should().BeEquivalentTo([V("2.0.0"), V("1.0.1")], "a heading that isn't a version, and an empty section, aren't releases");
        notes[V("2.0.0")].Should().Be("Upgrade as usual.\n\n**New**\n- A thing.");
        notes[V("1.0.1")].Should().Be("A fix.");
    }

    private static ReleaseNotesCatalog Catalog(params string[] withHighlights) => new(
        withHighlights.ToDictionary(V, _ => Highlights),
        "## Notes\n\n### v2.1.0\n\nPatch-like.\n\n### v2.0.0\n\nBig.\n\n### v1.0.0\n\nFirst.\n");

    [Fact]
    public void ACatalog_ListsEveryRelease_NewestFirst_WithItsNotesAndHighlights()
    {
        var c = Catalog("2.0.0", "1.0.0");
        c.Releases.Select(r => r.Version.ToString()).Should().Equal("2.1.0", "2.0.0", "1.0.0");
        c.Releases[0].Highlights.Should().BeNull();
        c.Releases[1].Notes.Should().Be("Big.");
        c.Newest.Should().Be(V("2.1.0"));
        c.Running(null).Should().Be(V("2.1.0"), "a development build runs the newest");
        c.Running(V("2.0.0")).Should().Be(V("2.0.0"));
        new ReleaseNotesCatalog(new Dictionary<ReleaseVersion, string>(), null).Newest.Should().BeNull();
    }

    [Fact]
    public void WhatsNew_IsDue_ForAReleaseWithHighlights_NotYetSeen()
    {
        var c = Catalog("2.0.0");
        var analyst = new[] { RoleGroup.Analysts };
        c.Due(V("2.0.0"), V("1.0.0"), analyst).Select(h => h.Version).Should().Equal(V("2.0.0"));
        c.Due(V("2.0.0"), null, analyst).Should().ContainSingle("someone who's seen none is shown the newest");
        c.Due(V("2.0.0"), V("2.0.0"), analyst).Should().BeEmpty("it's shown once");
        c.Due(V("1.0.0"), null, analyst).Should().BeEmpty("a release later than the one running isn't due");

        // Whether any is built in for the groups at all, without listing them: as Due with nothing seen says.
        c.AnyFor(V("2.0.0"), analyst).Should().BeTrue();
        c.AnyFor(V("1.0.0"), analyst).Should().BeFalse();
        c.AnyFor(V("2.0.0"), []).Should().BeFalse("no group, no highlights");
    }

    [Fact]
    public void AReleaseWithoutHighlights_ShowsNothing()
    {
        var c = Catalog("2.0.0");
        c.Due(V("2.1.0"), V("2.0.0"), [RoleGroup.Analysts]).Should().BeEmpty("2.1.0 is a patch: its notes have no highlights");
        c.Due(V("2.1.0"), V("1.0.0"), [RoleGroup.Analysts]).Select(h => h.Version).Should().Equal([V("2.0.0")],
            "an upgrade straight past 2.0.0 still shows 2.0.0's highlights, which weren't seen");
    }

    [Fact]
    public void SeveralUnseenReleases_AreShownNewestFirst_AndNoneToSomeoneWithNoGroup()
    {
        var c = Catalog("2.0.0", "1.0.0");
        c.Due(V("2.1.0"), V("0.9.0"), [RoleGroup.Analysts]).Select(h => h.Version).Should().Equal(V("2.0.0"), V("1.0.0"));
        c.Due(V("2.1.0"), V("0.9.0"), []).Should().BeEmpty("a Reader or a Visitor reads no highlights");
    }

    [Fact]
    public void AReleaseWithHighlightsForOtherGroupsOnly_IsntDueForThisOne()
    {
        var c = new ReleaseNotesCatalog(new Dictionary<ReleaseVersion, string> { [V("2.0.0")] = "## Analysts\n\n- Only theirs." }, null);
        c.Due(V("2.0.0"), null, [RoleGroup.ViewersAndActionOwners]).Should().BeEmpty();
        c.Due(V("2.0.0"), null, [RoleGroup.Analysts]).Should().ContainSingle();
    }

    [Fact]
    public void SomeoneWhoHasSeenNone_GetsEveryReleaseSinceTheVersionTheServerRanBefore()
    {
        var c = Catalog("2.1.0", "2.0.0", "1.0.0");
        var analyst = new[] { RoleGroup.Analysts };
        c.Due(V("2.1.0"), null, analyst, upgradedFrom: V("1.0.0")).Select(h => h.Version).Should().Equal([V("2.1.0"), V("2.0.0")],
            "the server ran 1.0.0 before: what came after it, newest first");
        c.Due(V("2.1.0"), null, analyst, upgradedFrom: ReleaseHistory.BeforeRecorded).Select(h => h.Version)
            .Should().Equal([V("2.1.0"), V("2.0.0"), V("1.0.0")], "a server from before the version was recorded: every release");
        c.Due(V("2.1.0"), null, analyst, upgradedFrom: null).Select(h => h.Version).Should().Equal([V("2.1.0")],
            "not known (a new install): only the newest");
        c.Due(V("2.1.0"), null, analyst, upgradedFrom: V("2.1.0")).Select(h => h.Version).Should().Equal([V("2.1.0")],
            "nothing after it (a downgrade, say): the newest, never nothing");
        c.Due(V("2.1.0"), null, [], upgradedFrom: V("1.0.0")).Should().BeEmpty("a Reader or a Visitor reads no highlights");
    }

    [Fact]
    public void SomeoneWhoHasSeenSome_GetsEverythingAfterIt_WhateverTheServerRanBefore()
    {
        var c = Catalog("2.1.0", "2.0.0", "1.0.0");
        c.Due(V("2.1.0"), V("2.0.0"), [RoleGroup.Analysts], upgradedFrom: V("1.0.0")).Select(h => h.Version).Should().Equal(V("2.1.0"));
        c.Due(V("2.1.0"), V("0.9.0"), [RoleGroup.Analysts], upgradedFrom: V("2.0.0")).Should().HaveCount(3);
        c.Due(V("2.1.0"), V("2.1.0"), [RoleGroup.Analysts], upgradedFrom: ReleaseHistory.BeforeRecorded).Should().BeEmpty();
    }

    [Theory]
    // A new install: nothing recorded, nobody signed in yet. Not known.
    [InlineData(null, null, false, "", true)]
    // An existing install from before the version was recorded: earlier than any recorded.
    [InlineData(null, null, true, "0.0.0", true)]
    // A restart on the same version keeps what it was upgraded from, and writes nothing.
    [InlineData("2.0.0", "1.0.0", true, "1.0.0", false)]
    [InlineData("2.0.0", "", true, "", false)]
    // An upgrade: from the version recorded.
    [InlineData("1.0.0", "0.0.0", true, "1.0.0", true)]
    [InlineData("1.0.0", "", false, "1.0.0", true)]
    // A record that isn't a version: not known.
    [InlineData("garbage", "1.0.0", true, "", true)]
    public void TheVersionUpgradedFrom_IsReadFromTheRecordBeforeItsUpdated(string? version, string? from, bool hadUsers, string expected, bool writes)
    {
        var (upgradedFrom, write) = ReleaseHistory.Decide(V("2.0.0"), version, from, hadUsers);
        (upgradedFrom?.ToString() ?? "").Should().Be(expected);
        write.Should().Be(writes);
    }

    [Fact]
    public void TheBuiltInCatalog_HasThisReleasesHighlightsForEveryGroup_AndItsFullNotes()
    {
        var v = ReleaseNotesCatalog.Embedded.Releases.Single(r => r.Version == V("1.13.0"));
        v.Notes.Should().Contain("context rail");
        v.Highlights.Should().NotBeNull();
        v.Highlights!.Sections.Keys.Should().BeEquivalentTo(RoleGroups.Headings.Select(h => h.Group));
        v.Highlights.Sections[RoleGroup.Analysts].Should().Contain("`]`").And.Contain("Guides").And.Contain("reading pane");
        // Everyone reads about the context rail and guides: the analysts' section (which analysts and administrators
        // read) and the viewers' and action owners' both open with them.
        v.Highlights.Sections[RoleGroup.ViewersAndActionOwners].Should().Contain("context rail").And.Contain("Guides").And.NotContain("Triage");
        v.Highlights.Sections[RoleGroup.Administrators].Should().Contain("Organization");
        v.Highlights.Sections[RoleGroup.Presenters].Should().NotContain("Organization", "only administrators read about their settings");
        ReleaseNotesCatalog.Embedded.Releases.Should().Contain(r => r.Version == V("1.0.0") && r.Notes != null,
            "the full notes of every release are built in");
    }

    [Fact]
    public void TheBuiltInCatalog_HasV114sHighlights_ForEveryGroup_AndItsFullNotes()
    {
        var v = ReleaseNotesCatalog.Embedded.Releases.Single(r => r.Version == V("1.14.0"));
        v.Notes.Should().Contain("BriefExcuses").And.Contain("Upgrade-Tide.ps1 -Version latest");
        v.Highlights.Should().NotBeNull();
        v.Highlights!.Sections.Keys.Should().BeEquivalentTo(RoleGroups.Headings.Select(h => h.Group));
        v.Highlights.Sections[RoleGroup.Analysts].Should().Contain("New article on this story").And.Contain("Brief timeliness");
        v.Highlights.Sections[RoleGroup.Administrators].Should().Contain("Manager").And.Contain("Role mappings");
        v.Highlights.Sections[RoleGroup.ViewersAndActionOwners].Should().Contain("By person");
        ReleaseNotesCatalog.Embedded.Newest.Should().Be(V("1.14.0"));
    }

    [Fact]
    public void EveryHighlightsFile_HasOnlyTheGroupsHeadings()
    {
        var dir = Path.Combine(DeployScriptTests.RepoRoot(), "docs", "whats-new");
        var files = Directory.GetFiles(dir, "v*.md");
        files.Should().NotBeEmpty();
        var headings = RoleGroups.Headings.Select(h => "## " + h.Heading).ToHashSet();
        foreach (var file in files)
        {
            ReleaseVersion.Parse(Path.GetFileNameWithoutExtension(file)).Should().NotBeNull($"{file} is named for its release");
            var lines = File.ReadAllLines(file).Where(l => l.StartsWith("## ", StringComparison.Ordinal));
            lines.Should().OnlyContain(l => headings.Contains(l), $"{Path.GetFileName(file)} has a section per role group, named as RoleGroups names it");
        }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("1.13.0", true)]
    [InlineData("1.13.0-rc.1", true)]
    [InlineData("v1.13.0", false)]
    [InlineData("board", false)]
    [InlineData("1.13", false)]
    public void TheSeenPreference_HoldsAVersionOrNone(string value, bool allowed) =>
        UserPreferences.For(UserPreferences.WhatsNewSeen).Allows(value).Should().Be(allowed);
}
