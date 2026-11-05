using FluentAssertions;
using Tide.Application.Metrics;
using Tide.Domain.Entities;

namespace Tide.UnitTests;

/// <summary>Bug round 1 on the numbers form and the metric history: the history's columns keep a group together.</summary>
public class EntryRound1BugTests
{
    private static MetricGroup Group(string name, string key) => new() { Id = Guid.NewGuid(), Name = name, Key = key };

    private static MetricDefinition Metric(MetricGroup g, string name) => new() { Id = Guid.NewGuid(), Name = name, GroupId = g.Id, Group = g };

    [Fact]
    public void HistoryColumns_KeepAGroupTogether_WhenTheOrderInterleavesTwoGroups()
    {
        // Two groups with the same place and name (added at once, or renamed alike): the history's order sorts their
        // metrics by their own place, so it interleaves them. Each group still gets one heading and one chip.
        var a = Group("Email", "email");
        var b = Group("Email", "email-2");
        var a1 = Metric(a, "Reported");
        var b1 = Metric(b, "Clicked");
        var a2 = Metric(a, "Blocked");
        var b2 = Metric(b, "Open");

        var runs = MetricHistoryColumns.Group([a1, b1, a2, b2]);

        runs.Select(r => r.Key).Should().Equal("email", "email-2");
        runs[0].Metrics.Should().Equal(a1, a2);
        runs[1].Metrics.Should().Equal(b1, b2);
        var picked = MetricHistoryColumns.Toggle(runs, MetricHistoryColumns.Toggle(runs, new HashSet<string>(), "email"), "email-2");
        picked.Should().BeEmpty("both groups picked is all of them");
    }
}
