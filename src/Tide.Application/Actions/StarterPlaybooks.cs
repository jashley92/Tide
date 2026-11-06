using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>One playbook of the starter set, as it's added: its kind of action and its steps, in order.</summary>
public sealed record StarterPlaybook(string Name, string Description, ActionKind Kind, IReadOnlyList<string> Steps);

/// <summary>
/// The playbooks a new install starts with: one short checklist for each kind of action (Other has none: it's whatever
/// the others aren't), pointing at the parts of TIDE each step happens in. Added once, to an empty library
/// (DatabaseInitializer); a team that deletes or retires them doesn't get them back.
/// </summary>
public static class StarterPlaybooks
{
    public static readonly IReadOnlyList<StarterPlaybook> All =
    [
        new("IOC sweep", "Search our logs for an article's indicators and record what came back.", ActionKind.IocSweep,
        [
            "Check the indicators on the Hunt queries card, and add any the article missed",
            "Generate the query, or add a saved sweep for each indicator kind, over the last 30 days",
            "Run it in the SIEM and note anything the query says it left out",
            "Record hits or no hits, with a screenshot of the result",
            "On a hit, raise a threat hunt or hand it to incident response, and link it here",
            "Close with an outcome",
        ]),
        new("Threat hunt", "A hypothesis-led search for the activity a report describes, beyond its indicators.", ActionKind.ThreatHunt,
        [
            "Write the hypothesis in a line: what the attacker would do here, and where it would show",
            "List the data sources it needs and confirm we collect them for the window",
            "Build the queries on the Hunt queries card, starting from a saved query where one fits",
            "Run them, and review what comes back with the system owners where needed",
            "Record findings, including what was ruled out and any gaps in our logging",
            "Save a query worth keeping for the team, or propose it as a detection",
            "Close with an outcome",
        ]),
        new("Block indicators", "Block an article's indicators at the controls that can stop them.", ActionKind.Block,
        [
            "Check each indicator isn't shared infrastructure or one of our own (a CDN, a cloud provider, a partner)",
            "Choose the controls: proxy, firewall, DNS, EDR or mail gateway",
            "Raise the change for each control, with an expiry date for addresses",
            "Confirm each block is live, and test one where it's safe to",
            "Record what was blocked where, with the change references",
            "Close with an outcome",
        ]),
        new("Patch / remediate", "Get a vulnerable product patched or mitigated across our estate.", ActionKind.PatchRemediate,
        [
            "Confirm the affected versions against the versions Our stack lists for the product",
            "Find every affected host with the product's owning team",
            "Agree the fix or the vendor's workaround, and a date, with the owner",
            "Confirm the fixed version is deployed on every host Our stack lists",
            "Update the product's versions in Our stack",
            "If it was exploited before the fix, raise an IOC sweep for the article's indicators",
            "Close with an outcome",
        ]),
        new("Config change", "Change a configuration or a detection in response to a report.", ActionKind.ConfigChange,
        [
            "Write down the change, the system it's on and why",
            "Raise it through change management, with a rollback plan",
            "Test it outside production where we can",
            "Confirm it's live and working as intended",
            "Record the change reference and close with an outcome",
        ]),
        new("Awareness", "Tell the people who need to know: a user notice or a help desk heads-up.", ActionKind.Awareness,
        [
            "Decide who needs to know: all staff, a team, or the help desk",
            "Draft a short notice: what to look for, and what to do and who to tell if they see it",
            "Keep it within the article's TLP, and leave out indicators users don't need",
            "Get it approved and sent through the usual channel",
            "Record when it went and to whom, and close with an outcome",
        ]),
        new("Outreach", "Ask a vendor, partner, ISAC or internal team for information by email.", ActionKind.Outreach,
        [
            "Pick the template that fits, and confirm the recipient",
            "Write the email, check what TIDE filled in and anything it left out, and fix the markers",
            "Send it from your own mail, then Mark sent",
            "Chase with the follow-up template if there's no reply in two business days",
            "Record the reply, and raise any action it calls for",
            "Close with an outcome",
        ]),
    ];

    /// <summary>A starter as a row, active, in the list's order.</summary>
    public static Playbook ToEntity(StarterPlaybook s, int order)
    {
        var p = new Playbook { Name = s.Name, Description = s.Description, Kind = s.Kind, IsActive = true, SortOrder = order };
        p.Steps.AddRange(s.Steps.Select((text, i) => new PlaybookStep { PlaybookId = p.Id, Order = i, Text = text }));
        return p;
    }
}
