using FluentAssertions;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>DataTable's filtering, sorting, grouping and count (TableView), without a page.</summary>
public class TableViewTests
{
    private sealed record Row(int N, string Name, string Team, DateOnly? Due);

    private static readonly IReadOnlyList<TableColumn<Row>> Columns =
    [
        new("n", "#") { SortBy = r => r.N },
        new("name", "Name") { SortBy = r => r.Name, FilterBy = r => r.Name },
        new("team", "Team") { SortBy = r => r.Team, FilterBy = r => r.Team },
        new("due", "Due") { SortBy = r => r.Due, DescendingFirst = true },
    ];

    private static readonly IReadOnlyList<Row> Rows =
    [
        new(1, "Block the domain", "SOC", new DateOnly(2026, 11, 2)),
        new(2, "patch the VPN", "Infra", null),
        new(3, "Hunt for the hash", "SOC", new DateOnly(2026, 10, 30)),
        new(4, "Brief the board", "CTI", new DateOnly(2026, 11, 2)),
    ];

    [Fact]
    public void Filter_NeedsEveryWord_InAnySearchedColumn_IgnoringCase()
    {
        TableView.Filter(Rows, Columns, "soc").Select(r => r.N).Should().Equal(1, 3);
        TableView.Filter(Rows, Columns, "  THE   soc ").Select(r => r.N).Should().Equal(1, 3);
        TableView.Filter(Rows, Columns, "hash soc").Select(r => r.N).Should().Equal(3);
        TableView.Filter(Rows, Columns, "2").Should().BeEmpty("the number column isn't searched");
        TableView.Filter(Rows, Columns, "").Should().HaveCount(4);
        TableView.Filter(Rows, Columns, null).Should().HaveCount(4);
    }

    [Fact]
    public void Filter_DoesNotMatchAWordAcrossTwoColumns()
    {
        TableView.Filter(Rows, Columns, "boardCTI").Should().BeEmpty();
        TableView.Words("a  b\tc").Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Sort_IsStable_IgnoresCase_AndPutsBlanksLastEitherWay()
    {
        TableView.Sort(Rows, Columns, new TableSort("name", false)).Select(r => r.N).Should().Equal(1, 4, 3, 2);
        TableView.Sort(Rows, Columns, new TableSort("due", false)).Select(r => r.N).Should().Equal(3, 1, 4, 2);
        TableView.Sort(Rows, Columns, new TableSort("due", true)).Select(r => r.N).Should().Equal([1, 4, 3, 2], "ties keep their order; no date is last");
        TableView.Sort(Rows, Columns, new TableSort("team", true)).Select(r => r.N).Should().Equal(1, 3, 2, 4);
    }

    [Fact]
    public void Sort_WithNoSortOrAnUnknownColumn_KeepsTheOrderGiven()
    {
        TableView.Sort(Rows, Columns, null).Select(r => r.N).Should().Equal(1, 2, 3, 4);
        TableView.Sort(Rows, Columns, new TableSort("nope", false)).Select(r => r.N).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void Toggle_FlipsTheSortedColumn_AndStartsAnotherItsOwnWay()
    {
        var name = Columns[1];
        var due = Columns[3];
        TableView.Toggle(null, name).Should().Be(new TableSort("name", false));
        TableView.Toggle(new TableSort("name", false), name).Should().Be(new TableSort("name", true));
        TableView.Toggle(new TableSort("name", true), due).Should().Be(new TableSort("due", true), "a date sorts newest first first");
    }

    [Fact]
    public void AriaSort_IsOnlyOnTheSortedColumn()
    {
        TableView.AriaSort(new TableSort("name", false), "name").Should().Be("ascending");
        TableView.AriaSort(new TableSort("name", true), "name").Should().Be("descending");
        TableView.AriaSort(new TableSort("name", true), "team").Should().BeNull();
        TableView.AriaSort(null, "name").Should().BeNull();
    }

    [Fact]
    public void Group_FollowsTheOrderGiven_ThenFirstSeen_AndLeavesOutEmptyGroups()
    {
        var groups = TableView.Group(Rows, r => r.Team, ["CTI", "Overdue", "SOC"]);
        groups.Select(g => g.Name).Should().Equal("CTI", "SOC", "Infra");
        groups[1].Rows.Select(r => r.N).Should().Equal(1, 3);
        TableView.Group(Rows, r => r.Team).Select(g => g.Name).Should().Equal("SOC", "Infra", "CTI");
    }

    [Fact]
    public void Group_OfTheRowsShown_CountsEveryMatchingRow_AndKeepsAGroupWithNoneShownYet()
    {
        var shown = Rows.Take(2).ToList();   // SOC 1 and Infra 2 on this page; SOC 3 and CTI 4 on the next

        var groups = TableView.Group(shown, Rows, r => r.Team, ["CTI", "SOC"]);

        groups.Select(g => (g.Name, g.Rows.Count, g.Total, g.Hidden)).Should().Equal(("CTI", 0, 1, 1), ("SOC", 1, 2, 1), ("Infra", 1, 1, 0));
        groups[1].Rows.Select(r => r.N).Should().Equal(1);
        TableView.Group(Rows, r => r.Team).Should().OnlyContain(g => g.Total == g.Rows.Count && g.Hidden == 0, "everything is shown");
        TableView.Group(shown, [], r => r.Team).Should().BeEmpty("a group is drawn only for matching rows");
    }

    [Theory]
    [InlineData(1, 12, 12, 12, false, "12 articles")]
    [InlineData(1, 1, 1, 1, false, "1 article")]
    [InlineData(1, 50, 1200, 1200, false, "1–50 of 1,200 articles")]
    [InlineData(51, 50, 1200, 1200, false, "51–100 of 1,200 articles")]
    [InlineData(1, 8, 8, 12, true, "Showing 8 of 12 articles")]
    [InlineData(1, 50, 90, 1200, true, "1–50 of 90 matching, 1,200 articles in all")]
    [InlineData(1, 3, 3, null, true, "3 matching articles")]
    [InlineData(1, 1, 1, null, true, "1 matching article")]
    [InlineData(101, 32, 132, null, true, "101–132 of 132 matching articles")]
    [InlineData(1, 50, 132, null, false, "1–50 of 132 articles")]
    public void Count_SaysWhichRowsThisPageShows_OfHowMany(int first, int shown, int matching, int? all, bool filtered, string expected) =>
        TableView.Count(first, shown, matching, all, filtered, "article", "articles").Should().Be(expected);

    [Theory]
    [InlineData(1, 1, "1")]
    [InlineData(3, 7, "1 2 3 4 5 6 7")]
    [InlineData(1, 12, "1 2 3 4 5 … 12")]
    [InlineData(4, 12, "1 2 3 4 5 … 12")]
    [InlineData(6, 12, "1 … 5 6 7 … 12")]
    [InlineData(9, 12, "1 … 8 9 10 11 12")]
    [InlineData(12, 12, "1 … 8 9 10 11 12")]
    [InlineData(40, 12, "1 … 8 9 10 11 12")]
    public void PageNumbers_ShowTheEndsAndThePagesAround_WithAGapBetween(int page, int pages, string expected) =>
        string.Join(' ', TableView.PageNumbers(page, pages).Select(n => n?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "…")).Should().Be(expected);

    [Theory]
    [InlineData(0, 100, 1)]
    [InlineData(100, 100, 1)]
    [InlineData(101, 100, 2)]
    [InlineData(1200, 50, 24)]
    public void Pages_CountsWholeAndPartPages_AtLeastOne(int rows, int size, int expected) =>
        TableView.Pages(rows, size).Should().Be(expected);

    [Theory]
    [InlineData(null, 1)]
    [InlineData("3", 3)]
    [InlineData("0", 1)]
    [InlineData("-2", 1)]
    [InlineData("abc", 1)]
    [InlineData("2.5", 1)]
    [InlineData("99999999999", 1)]
    [InlineData("2147483647", TableView.MaxPage)]
    public void PageFrom_TakesAWholePageNumber_ElseTheFirst_AndCapsIt(string? text, int expected) =>
        TableView.PageFrom(text).Should().Be(expected);
}
