using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

public class ActionSlaTests
{
    private static readonly DateOnly Thu = new(2026, 10, 22), Fri = new(2026, 10, 23), Mon = new(2026, 10, 26), Tue = new(2026, 10, 27);

    [Fact]
    public void DueDate_CountsBusinessDays_SkippingTheWeekend()
    {
        ActionSla.DueDate(Thu, 1).Should().Be(Fri);
        ActionSla.DueDate(Fri, 1).Should().Be(Mon);
        ActionSla.DueDate(Fri, 2).Should().Be(Tue);
        ActionSla.DueDate(Fri, null).Should().BeNull();
        ActionSla.DueDate(Fri, 0).Should().BeNull();
    }

    [Fact]
    public void Targets_AKindsTargetWinsOverThePrioritys()
    {
        var targets = new SlaTargets(new Dictionary<Priority, int> { [Priority.P3] = 10 }, new Dictionary<ActionKind, int> { [ActionKind.IocSweep] = 1 });
        targets.DaysFor(Priority.P3, ActionKind.IocSweep).Should().Be(1);
        targets.DaysFor(Priority.P3, ActionKind.Block).Should().Be(10);
        targets.DaysFor(Priority.P1, ActionKind.Block).Should().BeNull();
    }

    [Theory]
    [InlineData(ActionStatus.Open, "2026-10-23", null, SlaState.Overdue)]      // due Friday, it's Monday
    [InlineData(ActionStatus.Open, "2026-10-26", null, SlaState.DueSoon)]      // due today
    [InlineData(ActionStatus.InProgress, "2026-10-27", null, SlaState.DueSoon)] // due the next business day
    [InlineData(ActionStatus.Blocked, "2026-10-28", null, SlaState.OnTrack)]
    [InlineData(ActionStatus.Done, "2026-10-23", "2026-10-23", SlaState.Met)]
    [InlineData(ActionStatus.Done, "2026-10-23", "2026-10-26", SlaState.Missed)]
    [InlineData(ActionStatus.Cancelled, "2026-10-23", "2026-10-26", SlaState.NoDue)]
    public void StateOf_ReadsTheDueDateAgainstToday(ActionStatus status, string due, string? closed, SlaState expected) =>
        ActionSla.StateOf(status, DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture),
            closed is null ? null : DateOnly.Parse(closed, System.Globalization.CultureInfo.InvariantCulture), Mon).Should().Be(expected);

    [Fact]
    public void StateOf_NoDueDate_IsNoDue() => ActionSla.StateOf(ActionStatus.Open, null, null, Mon).Should().Be(SlaState.NoDue);

    [Fact]
    public void BusinessDaysUntil_IsSignedAndSkipsWeekends()
    {
        ActionSla.BusinessDaysUntil(Fri, Mon).Should().Be(1);
        ActionSla.BusinessDaysUntil(Mon, Thu).Should().Be(-2);
        ActionSla.BusinessDaysUntil(Mon, Mon).Should().Be(0);
    }

    [Theory]
    [InlineData("2026-10-26", "2026-10-30")] // Monday: that Friday
    [InlineData("2026-10-30", "2026-10-30")] // Friday: today
    [InlineData("2026-10-31", "2026-11-06")] // Saturday: the coming week's Friday, as the brief day is Monday
    [InlineData("2026-11-01", "2026-11-06")] // Sunday
    public void EndOfBusinessWeek_IsTheWeeksLastBusinessDay(string today, string expected) =>
        ActionSla.EndOfBusinessWeek(Day(today)).Should().Be(Day(expected));

    [Theory]
    [InlineData(ActionStatus.Open, "2026-10-27", ActionUrgency.Overdue)]      // due yesterday (it's Wednesday)
    [InlineData(ActionStatus.InProgress, "2026-10-28", ActionUrgency.DueToday)]
    [InlineData(ActionStatus.Open, "2026-10-29", ActionUrgency.ThisWeek)]     // tomorrow
    [InlineData(ActionStatus.Open, "2026-10-30", ActionUrgency.ThisWeek)]     // Friday, the end of the week
    [InlineData(ActionStatus.Open, "2026-10-31", ActionUrgency.Later)]        // Saturday
    [InlineData(ActionStatus.Open, "2026-11-02", ActionUrgency.Later)]        // next week
    [InlineData(ActionStatus.Open, null, ActionUrgency.Later)]                // no due date
    [InlineData(ActionStatus.Blocked, "2026-10-20", ActionUrgency.Blocked)]   // blocked and overdue: blocked wins
    [InlineData(ActionStatus.Blocked, "2026-10-28", ActionUrgency.Blocked)]
    [InlineData(ActionStatus.Blocked, null, ActionUrgency.Blocked)]
    public void UrgencyOf_GroupsByDueDate_AndBlockedWins(ActionStatus status, string? due, ActionUrgency expected) =>
        ActionSla.UrgencyOf(status, due is null ? null : Day(due), Day("2026-10-28")).Should().Be(expected);

    [Fact]
    public void UrgencyOf_OnAFriday_ThisWeekIsEmpty_AndOnAWeekend_ItIsTheComingWeek()
    {
        ActionSla.UrgencyOf(ActionStatus.Open, Fri, Fri).Should().Be(ActionUrgency.DueToday);
        ActionSla.UrgencyOf(ActionStatus.Open, Mon, Fri).Should().Be(ActionUrgency.Later);
        var sat = Fri.AddDays(1);
        ActionSla.UrgencyOf(ActionStatus.Open, Fri, sat).Should().Be(ActionUrgency.Overdue);
        ActionSla.UrgencyOf(ActionStatus.Open, Mon, sat).Should().Be(ActionUrgency.ThisWeek);
    }

    [Fact]
    public void UrgencyName_IsTheHeading_InTheListsOrder() =>
        Enum.GetValues<ActionUrgency>().Select(ActionSla.UrgencyName).Should().Equal("Overdue", "Due today", "This week", "Later", "Blocked");

    [Fact]
    public void LateText_CountsTheDaysPastDue_AndIsNullWhenItIsntLate()
    {
        ActionSla.LateText(Thu, Mon).Should().Be("4 days late");
        ActionSla.LateText(Fri, Fri.AddDays(1)).Should().Be("1 day late");
        ActionSla.LateText(Mon, Mon).Should().BeNull("due today isn't late");
        ActionSla.LateText(Tue, Mon).Should().BeNull();
        ActionSla.LateText(null, Mon).Should().BeNull();
        ActionSla.DaysLate(Thu, Tue).Should().Be(5);
    }

    private static DateOnly Day(string s) => DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Today's team queue: mine first, then nobody's, each by due date; a colleague's and closed ones left out.</summary>
public class TeamQueueTests
{
    private static ActionRow Row(int number, string? assignee, DateOnly? due, ActionStatus status = ActionStatus.Open, Priority priority = Priority.P3) =>
        new(Guid.NewGuid(), number, "A" + number, ActionKind.Block, priority, Guid.Empty, "Cyber Defense", assignee, assignee, due, status,
            ActionOutcome.None, null, null, null, null, 0, DateTimeOffset.UtcNow);

    private static readonly DateOnly Mon = new(2026, 10, 26), Tue = new(2026, 10, 27), Wed = new(2026, 10, 28);

    [Fact]
    public void From_PutsMineFirst_ThenTheUnassigned_EachByDueDate_TheUndatedLast()
    {
        var rows = new[]
        {
            Row(1, null, Wed), Row(2, "me", null), Row(3, "colleague", Mon), Row(4, "me", Tue), Row(5, null, Mon),
            Row(6, null, null), Row(7, "me", Mon, ActionStatus.Done), Row(8, null, Tue, priority: Priority.P1), Row(9, null, Tue),
        };

        var q = TeamQueue.From(rows, "me", onATeam: true, take: 10);

        q.Rows.Select(r => r.Number).Should().Equal(4, 2, 5, 8, 9, 1, 6);
        (q.Mine, q.Unassigned, q.Total, q.OnATeam).Should().Be((2, 5, 7, true));
    }

    [Fact]
    public void From_ShowsAtMostTake_ButCountsThemAll()
    {
        var rows = Enumerable.Range(1, 12).Select(n => Row(n, n % 3 == 0 ? "me" : null, Mon.AddDays(n))).ToList();

        var q = TeamQueue.From(rows, "me", true, ActionService.TeamQueueSize);

        q.Rows.Should().HaveCount(8);
        q.Rows.Take(4).Should().OnlyContain(r => r.AssigneeId == "me");
        q.Total.Should().Be(12);
    }
}

/// <summary>
/// Each dialect against a golden file per indicator type (tests/Tide.UnitTests/Golden). To accept a deliberate change,
/// run the tests with TIDE_UPDATE_GOLDEN=1 and review the diff.
/// </summary>
public class HuntQueryTests
{
    private static readonly ArticleIndicator[] Fixture =
    [
        new() { Type = IndicatorType.IpAddress, Value = "203.0.113.45" },
        new() { Type = IndicatorType.IpAddress, Value = "2001:db8::7" },
        new() { Type = IndicatorType.Domain, Value = "update-check.example.net" },
        new() { Type = IndicatorType.Url, Value = "https://login.example.com/owa?x=1" },
        new() { Type = IndicatorType.FileHash, Value = "44d88612fea8a8f36de82e1278abb02f" },
        new() { Type = IndicatorType.FileHash, Value = "275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f" },
        new() { Type = IndicatorType.FileName, Value = "invoice_viewer.exe" },
        new() { Type = IndicatorType.EmailAddress, Value = "billing@example.org" },
        new() { Type = IndicatorType.Host, Value = "WS-0142" },
        new() { Type = IndicatorType.RegistryKey, Value = @"HKCU\Software\Example\Run" },
    ];

    public static TheoryData<string> Dialects() => new(QueryDialects.All.Select(d => d.Key));

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Build_MatchesTheGoldenFile(string dialect)
    {
        var actual = HuntQueryBuilder.Build(QueryDialects.Find(dialect)!, Fixture);
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", dialect + ".txt");
        if (Environment.GetEnvironmentVariable("TIDE_UPDATE_GOLDEN") == "1")
        {
            var source = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden", dialect + ".txt");
            File.WriteAllText(source, actual);
            return;
        }
        actual.Should().Be(File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    [Fact]
    public void Build_EscapesQuotesAndBackslashes_SoAValueCantBreakOutOfItsString()
    {
        var evil = new ArticleIndicator { Type = IndicatorType.Domain, Value = "a\") or 1==1 // \\\"x\nnext" };
        foreach (var d in QueryDialects.All)
        {
            var q = HuntQueryBuilder.Build(d, [evil]);
            q.Should().Contain("\"a\\\") or 1==1 // \\\\\\\"xnext\"");
        }
    }

    [Fact]
    public void AnInternationalDomain_IsSearchedAsLogsRecordIt_InPunycode_AsWellAsAsWritten()
    {
        ArticleIndicator[] idn = [new() { Type = IndicatorType.Domain, Value = "bücher.example" }, new() { Type = IndicatorType.Domain, Value = "plain.example" }];
        foreach (var d in QueryDialects.All)
        {
            var q = HuntQueryBuilder.Build(d, idn);
            q.Should().Contain("\"bücher.example\", \"xn--bcher-kva.example\", \"plain.example\"", d.Key);
            q.Should().Contain("from 2 indicator(s)", "the punycode form is the same indicator");
        }
        var saved = HuntQueryBuilder.FromSaved(QueryDialects.Find("kql")!, "Names", "x has_any ({{values:domain}})", 30, idn);
        saved.Should().Contain("x has_any (\"bücher.example\", \"xn--bcher-kva.example\", \"plain.example\")").And.Contain("with 2 indicator(s)");
    }

    [Fact]
    public void Build_NamesTypesWithoutATemplate_AndUsesASavedTemplateFirst()
    {
        var fql = QueryDialects.Find("fql")!;
        var q = HuntQueryBuilder.Build(fql, [new() { Type = IndicatorType.Url, Value = "https://x.example" }]);
        q.Should().Contain("// URLs: no CrowdStrike Falcon / Next-Gen SIEM template; 1 value(s) not included.");

        var saved = new Dictionary<HuntValueKind, string> { [HuntValueKind.Url] = "my_urls | where url in ({{values}})" };
        HuntQueryBuilder.Build(fql, [new() { Type = IndicatorType.Url, Value = "https://x.example" }], saved)
            .Should().Contain("my_urls | where url in (\"https://x.example\")");
    }

    [Theory]
    [InlineData(IndicatorType.IpAddress, "203.0.113.45", HuntValueKind.Ipv4)]
    [InlineData(IndicatorType.IpAddress, "2001:db8::7", HuntValueKind.Ipv6)]
    [InlineData(IndicatorType.FileHash, "44d88612fea8a8f36de82e1278abb02f", HuntValueKind.Md5)]
    [InlineData(IndicatorType.FileHash, "3395856ce81f2b7382dee72602f798b642f14140", HuntValueKind.Sha1)]
    [InlineData(IndicatorType.FileHash, "275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f", HuntValueKind.Sha256)]
    [InlineData(IndicatorType.FileHash, "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e", HuntValueKind.Sha512)]
    [InlineData(IndicatorType.FileHash, "abc123", HuntValueKind.OtherHash)]
    [InlineData(IndicatorType.FileHash, "zz88612fea8a8f36de82e1278abb02f0", HuntValueKind.OtherHash)]
    [InlineData(IndicatorType.Domain, "bad.example.net", HuntValueKind.Domain)]
    [InlineData(IndicatorType.CommandLine, "powershell -enc x", HuntValueKind.CommandLine)]
    public void KindOf_SplitsAddressesByFamily_AndHashesByAlgorithm(IndicatorType type, string value, HuntValueKind expected) =>
        HuntValues.KindOf(new ArticleIndicator { Type = type, Value = value }).Should().Be(expected);

    [Fact]
    public void Build_SendsEachHashAndAddressFamilyToItsOwnTemplate()
    {
        var kql = QueryDialects.Find("kql")!;
        var q = HuntQueryBuilder.Build(kql, Fixture);
        q.Should().Contain("| where MD5 in~ (\"44d88612fea8a8f36de82e1278abb02f\")")
            .And.Contain("| where SHA256 in~ (\"275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f\")")
            .And.NotContain("MD5 in~ (\"275a");
    }

    [Fact]
    public void Build_ATurnedOffKind_IsNamedAndLeftOut()
    {
        var saved = new Dictionary<HuntValueKind, string> { [HuntValueKind.Ipv6] = "" };
        var q = HuntQueryBuilder.Build(QueryDialects.Find("kql")!, Fixture, saved);
        q.Should().Contain("// IPv6 addresses: turned off in the query templates; 1 value(s) not included.").And.NotContain("2001:db8::7\"");
        q.Should().Contain("\"203.0.113.45\"", "IPv4 still has its template");
    }

    [Fact]
    public void Build_DeduplicatesAndSplitsLongLists()
    {
        var many = Enumerable.Range(0, HuntQueryBuilder.MaxValuesPerBlock + 3)
            .Select(i => new ArticleIndicator { Type = IndicatorType.IpAddress, Value = $"198.51.100.{i % 256}-{i}" })
            .Append(new ArticleIndicator { Type = IndicatorType.IpAddress, Value = "198.51.100.0-0" })
            .ToList();
        var q = HuntQueryBuilder.Build(QueryDialects.Find("kql")!, many);
        q.Should().Contain($"// IPv4 addresses ({HuntQueryBuilder.MaxValuesPerBlock})").And.Contain("// IPv4 addresses (3)");
    }
}

/// <summary>A saved query's values past <see cref="HuntQueryBuilder.MaxValuesPerBlock"/>: the query repeated, a block each.</summary>
public class SavedQueryBlockTests
{
    private static readonly IQueryDialect Kql = QueryDialects.Find("kql")!;

    private static IEnumerable<ArticleIndicator> Ips(int n) =>
        Enumerable.Range(0, n).Select(i => new ArticleIndicator { Type = IndicatorType.IpAddress, Value = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}" });

    private static IEnumerable<ArticleIndicator> Domains(int n) =>
        Enumerable.Range(0, n).Select(i => new ArticleIndicator { Type = IndicatorType.Domain, Value = $"d{i}.example" });

    [Fact]
    public void FromSaved_UpToABlock_IsOneQuery_AsBefore()
    {
        var q = HuntQueryBuilder.FromSaved(Kql, "Sweep", "x in ({{values}}) or y in ({{values}})", 30, Ips(HuntQueryBuilder.MaxValuesPerBlock));
        q.Should().StartWith($"// From the saved query \"Sweep\" with {HuntQueryBuilder.MaxValuesPerBlock} indicator(s).\nx in (\"10.0.0.0\"").And.NotContain("Part ");
        q.Split('\n').Should().HaveCount(3, "the header, the query, and the closing newline");
    }

    [Fact]
    public void FromSaved_PastABlock_RepeatsTheQuery_OneBlockEach_AndCountsEveryValue()
    {
        var n = HuntQueryBuilder.MaxValuesPerBlock + 3;
        var q = HuntQueryBuilder.FromSaved(Kql, "Sweep", "T\n| where Timestamp > ago({{days}}d)\n| where x in ({{values}})", 45, Ips(n));

        q.Should().StartWith($"// From the saved query \"Sweep\" with {n} indicator(s), looking back 45 days.\n// Part 1 of 2\nT\n");
        q.Should().Contain("\n\n// Part 2 of 2\nT\n| where Timestamp > ago(45d)\n| where x in (\"10.0.1.244\", \"10.0.1.245\", \"10.0.1.246\")\n");
        var lists = q.Split('\n').Where(l => l.StartsWith("| where x in (", StringComparison.Ordinal)).ToList();
        lists.Select(l => l.Split(", ").Length).Should().Equal(HuntQueryBuilder.MaxValuesPerBlock, 3);
        HuntQueryBuilder.WindowOf(q).Should().Be(45);
        HuntQueryBuilder.LeftOut(q).Should().Be(0, "every value is searched");
    }

    [Fact]
    public void FromSaved_TwoKindsPastABlock_RepeatsItForEachPairOfBlocks_AndOneUnderIsInEveryCopy()
    {
        var limit = HuntQueryBuilder.MaxValuesPerBlock;
        var both = HuntQueryBuilder.FromSaved(Kql, "Pairs", "a in ({{values:ipv4}}) and b in ({{values:domain}})", 30, Ips(limit + 1).Concat(Domains(limit + 2)));
        both.Should().Contain("// Part 4 of 4").And.NotContain("Part 5");
        var copies = both.Split('\n').Where(l => l.StartsWith("a in (", StringComparison.Ordinal)).ToList();
        copies.Should().HaveCount(4).And.OnlyHaveUniqueItems("each pairing of an address block with a domain block once");
        copies.Should().Contain(c => c.Contains("(\"10.0.1.244\") and") && c.Contains("\"d501.example\"") && !c.Contains("\"d0.example\""));

        var one = HuntQueryBuilder.FromSaved(Kql, "Pairs", "a in ({{values:ipv4}}) and b in ({{values:domain}})", 30, Ips(limit + 1).Concat(Domains(2)));
        var halves = one.Split('\n').Where(l => l.StartsWith("a in (", StringComparison.Ordinal)).ToList();
        halves.Should().HaveCount(2).And.OnlyContain(c => c.EndsWith("b in (\"d0.example\", \"d1.example\")", StringComparison.Ordinal));
    }
}

public class HuntWindowTests
{
    [Fact]
    public void Build_AValueThatContainsTheDaysPlaceholder_IsLeftAsItIs()
    {
        var q = HuntQueryBuilder.Build(QueryDialects.Find("kql")!,
            [new ArticleIndicator { Type = IndicatorType.Url, Value = "https://evil.example/{{days}}" }], days: 45);
        q.Should().Contain("ago(45d)").And.Contain("https://evil.example/{{days}}");
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, 30)]
    [InlineData(2, 30)]
    [InlineData(60, 67)]
    [InlineData(400, 180)]
    public void WindowDays_IsAWeekBeforeTheEarliestReport_Between30And180(int? daysAgo, int expected) =>
        HuntQueryBuilder.WindowDays(daysAgo is { } d ? Now.AddDays(-d) : null, Now).Should().Be(expected);
}
