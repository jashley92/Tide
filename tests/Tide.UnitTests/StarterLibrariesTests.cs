using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>
/// The starter saved queries, outreach templates and playbooks a new install gets: each passes the checks a save applies
/// unchanged, and each fills or runs as the team would use it.
/// </summary>
public class StarterLibrariesTests
{
    private static readonly ArticleIndicator[] Indicators =
    [
        new() { Type = IndicatorType.IpAddress, Value = "198.51.100.7" },
        new() { Type = IndicatorType.Domain, Value = "evil.example" },
        new() { Type = IndicatorType.Url, Value = "https://evil.example/login" },
        new() { Type = IndicatorType.FileHash, Value = new string('a', 64) },
    ];

    // --- saved queries ----------------------------------------------------------------------------------------------

    [Fact]
    public void EachSavedQuery_PassesTheSaveChecks_Unchanged()
    {
        foreach (var s in StarterSavedQueries.All)
        {
            var (dialect, name, description, text) = SavedQueryService.Checked(new SavedQueryForm { Name = s.Name, Dialect = s.Dialect, Description = s.Description, Text = s.Text });
            (dialect.Key, name, description, text).Should().Be((s.Dialect, s.Name, s.Description, s.Text), s.Name);
        }
    }

    [Fact]
    public void SavedQueries_HaveUniqueNames_SayingTheirLanguage_AndCoverEachLanguage()
    {
        StarterSavedQueries.All.Select(s => s.Name).Should().OnlyHaveUniqueItems(n => n.ToLowerInvariant());
        StarterSavedQueries.All.Should().OnlyContain(s => s.Name.EndsWith($" ({s.Dialect.ToUpperInvariant()})", StringComparison.Ordinal));
        StarterSavedQueries.All.Select(s => s.Dialect).Distinct().Should().BeEquivalentTo(QueryDialects.All.Select(d => d.Key));
        foreach (var d in QueryDialects.All)
        {
            var mine = StarterSavedQueries.All.Where(s => s.Dialect == d.Key).ToList();
            mine.Should().Contain(s => s.Name.StartsWith("IP sweep", StringComparison.Ordinal))
                .And.Contain(s => s.Name.StartsWith("Domain sweep", StringComparison.Ordinal))
                .And.Contain(s => s.Name.StartsWith("File hash sweep", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EachSavedQuery_AddedToAnAction_FillsEveryPlaceholder_AndClaimsAWindowOnlyWhereItSetsOne()
    {
        foreach (var s in StarterSavedQueries.All)
        {
            var d = QueryDialects.Find(s.Dialect)!;
            var text = HuntQueryBuilder.FromSaved(d, s.Name, s.Text, 45, Indicators);
            text.Should().NotContain("{{", s.Name).And.StartWith(d.Comment($"From the saved query \"{s.Name}\""));
            HuntQueryBuilder.WindowOf(text).Should().Be(s.Text.Contains(QueryDialects.DaysPlaceholder) ? 45 : null, s.Name);
            // A one-kind sweep names the others as left out, so No hits, close asks for them.
            if (!s.Text.Contains(QueryDialects.Placeholder)) HuntQueryBuilder.LeftOut(text).Should().BePositive(s.Name);
        }
        StarterSavedQueries.All.Where(s => s.Dialect != "fql").Should().OnlyContain(s => s.Text.Contains(QueryDialects.DaysPlaceholder),
            "only Falcon's queries run over the range picked in the console");
    }

    // --- outreach templates -----------------------------------------------------------------------------------------

    [Fact]
    public void EachTemplate_PassesTheSaveChecks_Unchanged_AndHasNoFixedRecipient()
    {
        foreach (var s in StarterOutreachTemplates.All)
        {
            var kept = OutreachTemplateService.Checked(new OutreachTemplateForm { Name = s.Name, Description = s.Description, Subject = s.Subject, Body = s.Body, Ceiling = s.Ceiling });
            kept.Should().Be((s.Name, s.Description, s.Subject, s.Body, (string?)null, s.Ceiling), s.Name);
        }
        StarterOutreachTemplates.All.Select(s => s.Name).Should().OnlyHaveUniqueItems(n => n.ToLowerInvariant());
        StarterOutreachTemplates.All.Should().OnlyContain(s => s.Body.EndsWith("{{sender.name}}\n{{sender.team}}", StringComparison.Ordinal));
    }

    [Fact]
    public void EachTemplate_FilledFromAnActionWithEverything_LeavesNoMarker()
    {
        foreach (var s in StarterOutreachTemplates.All)
        {
            var draft = OutreachFill.Fill(s.Subject, s.Body, Facts(s.Ceiling));
            draft.Unfilled.Should().BeEmpty(s.Name);
            draft.LeftOut.Should().BeEmpty(s.Name);
            OutreachFill.HasMarker(draft.Subject + draft.Body).Should().BeFalse(s.Name);
            (draft.Subject + draft.Body).Should().NotContain("{{", s.Name);
            draft.Body.Should().EndWith("Robin\nCyber Threat Intelligence\n", s.Name);
        }
    }

    [Fact]
    public void TheTemplatesCeilings_KeepInternalOnesAmber_AndNothingAboveGreenLeavesUs()
    {
        StarterOutreachTemplates.All.Single(s => s.Name.StartsWith("Vendor", StringComparison.Ordinal)).Ceiling.Should().Be(TlpLevel.Clear);
        StarterOutreachTemplates.All.Single(s => s.Name.StartsWith("ISAC", StringComparison.Ordinal)).Ceiling.Should().Be(TlpLevel.Green);
        StarterOutreachTemplates.All.Where(s => s.Name.StartsWith("Internal", StringComparison.Ordinal)).Should().HaveCount(2).And.OnlyContain(s => s.Ceiling == TlpLevel.Amber);
        StarterOutreachTemplates.All.Single(s => s.Name == "Internal: action required").Subject.Should().Be("{{action.number}} - action required by {{today:+2}}");
    }

    // An action on a CLEAR article with everything a template can name: nothing to hold back, nothing missing.
    private static OutreachFacts Facts(TlpLevel ceiling)
    {
        var article = new Article
        {
            Title = "Edge VPN zero-day exploited", Bluf = "A pre-auth flaw in the VPN gateway is exploited in the wild.",
            SourceUrl = "https://vendor.example/advisory", Tlp = TlpLevel.Clear,
            Indicators = [.. Indicators], Cves = [new ArticleCve { CveId = "CVE-2026-1234" }],
        };
        var cut = OutreachFill.Cut(article, 0, ceiling);
        return new OutreachFacts
        {
            Ceiling = ceiling, FromArticle = true, Article = cut.Article, Indicators = cut.Article!.Indicators, IndicatorsTlp = cut.IndicatorsTlp,
            Product = new StackProduct { Name = "Edge VPN", Versions = "22.7R2", ContactName = "Vendor PSIRT", Team = new Team { Name = "Network" } },
            ActionNumber = 12, ActionLink = "https://tide.example.org/actions/12", Due = new DateOnly(2026, 11, 10),
            SenderName = "Robin", SenderTeam = "Cyber Threat Intelligence", Today = new DateOnly(2026, 11, 6), LeftOut = cut.LeftOut,
        };
    }

    // --- playbooks --------------------------------------------------------------------------------------------------

    [Fact]
    public void EachPlaybook_PassesTheSaveChecks_Unchanged_WithAShortChecklist()
    {
        foreach (var s in StarterPlaybooks.All)
        {
            var (name, description, steps) = PlaybookService.Checked(new PlaybookForm { Name = s.Name, Description = s.Description, Kind = s.Kind, Steps = [.. s.Steps] });
            (name, description).Should().Be((s.Name, s.Description), s.Name);
            steps.Should().Equal(s.Steps, s.Name);
            steps.Count.Should().BeInRange(4, 8, s.Name);
        }
        StarterPlaybooks.All.Select(s => s.Name).Should().OnlyHaveUniqueItems(n => n.ToLowerInvariant());
    }

    [Fact]
    public void EveryKindOfAction_ButOther_HasOnePlaybook()
    {
        StarterPlaybooks.All.Select(s => s.Kind).Should().BeEquivalentTo(Enum.GetValues<ActionKind>().Where(k => k != ActionKind.Other));
        var p = StarterPlaybooks.ToEntity(StarterPlaybooks.All[1], 1);
        (p.Kind, p.IsRetired, p.SortOrder, p.CreatedByName).Should().Be((ActionKind.ThreatHunt, false, 1, "TIDE"));
        p.Steps.Should().OnlyContain(x => x.PlaybookId == p.Id).And.BeInAscendingOrder(x => x.Order);
    }
}
