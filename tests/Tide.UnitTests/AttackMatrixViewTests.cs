using FluentAssertions;
using Tide.Application.Mitre;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>The ATT&amp;CK picker's columns for a filter: only tactics with a match while filtering, every tactic otherwise.</summary>
public class AttackMatrixViewTests
{
    private static AttackTechnique T(string id, string name, params MitreTactic[] tactics) => new(id, name, tactics, id.Contains('.'));

    // Three tactics: Execution has T1059 (with a PowerShell sub-technique), Credential Access has T1003 and T1110, and
    // T1110 is also in Discovery only through a sub-technique there.
    private static readonly IReadOnlyList<AttackMatrixColumn> Small = AttackMatrixView.Build(
    [
        T("T1059", "Command and Scripting Interpreter", MitreTactic.Execution),
        T("T1059.001", "PowerShell", MitreTactic.Execution),
        T("T1003", "OS Credential Dumping", MitreTactic.CredentialAccess),
        T("T1110", "Brute Force", MitreTactic.CredentialAccess),
        T("T1110.001", "Password Guessing", MitreTactic.CredentialAccess, MitreTactic.Discovery),
    ]);

    private static string[] Visible(AttackMatrixView v) => v.Columns.Where(c => !c.Hidden).Select(c => AttackMatrix.TacticName(c.Tactic)).ToArray();

    [Fact]
    public void Built_matrix_has_every_tactic_in_kill_chain_order_and_a_heading_for_a_parent_listed_only_for_its_subs()
    {
        Small.Select(c => c.Tactic).Should().Equal(AttackMatrix.Tactics);
        var discovery = Small.Single(c => c.Tactic == MitreTactic.Discovery);
        discovery.Rows.Should().ContainSingle().Which.Should().Match<AttackMatrixRow>(r => r.Parent.Id == "T1110" && !r.ParentInTactic && r.Subs.Count == 1);
    }

    [Fact]
    public void An_empty_filter_shows_every_tactic_with_no_counts_line()
    {
        foreach (var filter in new[] { "", "   ", null })
        {
            var v = AttackMatrixView.For(Small, filter);
            v.IsFiltering.Should().BeFalse();
            v.Columns.Should().OnlyContain(c => !c.Hidden);
            v.Columns.Select(c => c.Tactic).Should().Equal(AttackMatrix.Tactics);
            v.VisibleTactics.Should().Be(AttackMatrix.Tactics.Count);
            v.Summary.Should().BeNull();
            v.NoMatchText.Should().BeNull();
            v.NoMatches.Should().BeFalse();
        }
    }

    [Fact]
    public void A_search_hides_the_tactics_without_a_match_and_keeps_the_others_in_order()
    {
        var v = AttackMatrixView.For(Small, "cred");
        Visible(v).Should().Equal("Credential Access");
        v.Columns.Should().HaveCount(AttackMatrix.Tactics.Count, "a hidden column stays in the layout to collapse");
        v.Columns.Single(c => !c.Hidden).Rows.Select(r => r.Row.Parent.Id).Should().Equal("T1003");

        var t1 = AttackMatrixView.For(Small, "T1");
        Visible(t1).Should().Equal("Execution", "Credential Access", "Discovery");
    }

    [Fact]
    public void Matching_is_on_id_or_name_ignoring_case()
    {
        Visible(AttackMatrixView.For(Small, "t1059")).Should().Equal("Execution");
        Visible(AttackMatrixView.For(Small, "DUMPING")).Should().Equal("Credential Access");
        AttackMatrixView.Matches(T("T1", "x"), "").Should().BeTrue();
    }

    [Fact]
    public void The_counts_line_names_the_tactics_shown_and_the_distinct_techniques_matched()
    {
        // T1110.001 is under both Credential Access and Discovery: one technique, counted once.
        AttackMatrixView.For(Small, "guess").Summary.Should().Be($"Showing 2 of {AttackMatrix.Tactics.Count} tactics with matches · 1 technique");
        AttackMatrixView.For(Small, "T1").Summary.Should().Be($"Showing 3 of {AttackMatrix.Tactics.Count} tactics with matches · 5 techniques");
        AttackMatrixView.For(Small, "T1").MatchingTechniques.Should().Be(5);
    }

    [Fact]
    public void No_match_says_so_instead_of_an_empty_matrix()
    {
        var v = AttackMatrixView.For(Small, "  zzqx ");
        v.NoMatches.Should().BeTrue();
        v.Filter.Should().Be("zzqx");
        v.NoMatchText.Should().Be("No techniques match 'zzqx'");
        v.Summary.Should().BeNull();
        v.Columns.Should().OnlyContain(c => c.Hidden);
        v.Layout.Should().BeEmpty();
    }

    [Fact]
    public void A_sub_technique_match_keeps_its_tactic_and_opens_its_parent_to_just_the_matching_subs()
    {
        var v = AttackMatrixView.For(Small, "powershell");
        Visible(v).Should().Equal("Execution");
        var row = v.Columns.Single(c => !c.Hidden).Rows.Should().ContainSingle().Subject;
        row.Row.Parent.Id.Should().Be("T1059");
        row.OpenForMatch.Should().BeTrue();
        row.ShownSubs.Select(s => s.Id).Should().Equal("T1059.001");

        // A parent that matches itself lists all its subs when opened, and isn't opened for them.
        var parent = AttackMatrixView.For(Small, "interpreter").Columns.Single(c => !c.Hidden).Rows.Single();
        parent.OpenForMatch.Should().BeFalse();
        parent.ShownSubs.Should().HaveCount(1);
    }

    [Fact]
    public void A_column_that_just_lost_its_matches_keeps_its_rows_for_that_change_only()
    {
        var all = AttackMatrixView.For(Small, "");
        var cred = AttackMatrixView.For(Small, "cred", all);
        var leaving = cred.Columns.Single(c => c.Tactic == MitreTactic.Execution);
        leaving.Hidden.Should().BeTrue();
        leaving.Rows.Select(r => r.Row.Parent.Id).Should().Equal("T1059");

        var later = AttackMatrixView.For(Small, "credx", cred);
        later.Columns.Single(c => c.Tactic == MitreTactic.Execution).Rows.Should().BeEmpty();
        // A column never shown before has nothing to fade.
        cred.Columns.Single(c => c.Tactic == MitreTactic.Impact).Rows.Should().BeEmpty();
    }

    [Fact]
    public void Layout_changes_only_when_the_columns_shown_change()
    {
        AttackMatrixView.For(Small, "cred").Layout.Should().Be(AttackMatrixView.For(Small, "credential").Layout);
        AttackMatrixView.For(Small, "cred").Layout.Should().NotBe(AttackMatrixView.For(Small, "").Layout);
    }

    [Fact]
    public void Against_the_catalogue_T1059_and_powershell_keep_execution()
    {
        AttackMatrixView.Matrix.Should().BeSameAs(AttackMatrixView.Matrix);
        var t1059 = AttackMatrixView.For("T1059");
        t1059.Columns.Where(c => !c.Hidden).Select(c => c.Tactic).Should().Contain(MitreTactic.Execution);
        t1059.Summary.Should().StartWith("Showing ");

        var ps = AttackMatrixView.For("powershell");
        var exec = ps.Columns.Single(c => c.Tactic == MitreTactic.Execution);
        exec.Hidden.Should().BeFalse();
        exec.Rows.Should().Contain(r => r.Row.Parent.Id == "T1059" && r.ShownSubs.Any(s => s.Id == "T1059.001"));

        AttackMatrixView.For("cred").Columns.Single(c => c.Tactic == MitreTactic.CredentialAccess).Hidden.Should().BeFalse();
        AttackMatrixView.For("qqzzxv").NoMatches.Should().BeTrue();
    }
}
