using System.Security.Cryptography;
using System.Text;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>One outreach template of the starter set, as it's added (no fixed To address: each goes to whoever the action names).</summary>
public sealed record StarterOutreachTemplate(string Name, string Description, string Subject, string Body, TlpLevel Ceiling);

/// <summary>
/// The emails a new install's Outreach templates page starts with: a vendor asked whether we're affected, an owner asked
/// to act, an ISAC or partner told what we've seen, a nudge on an email with no reply, and a team asked whether we're
/// impacted. Internal ones are AMBER; anything leaving the organization is GREEN at most, and a vendor's is CLEAR, so
/// nothing from a partner's report goes to a vendor. Added once, to an empty library (DatabaseInitializer); a team that
/// deletes or retires them doesn't get them back.
/// </summary>
public static class StarterOutreachTemplates
{
    /// <summary>Who the page says saved them.</summary>
    public const string SavedBy = "TIDE";

    private const string SignOff = "Thanks,\n{{sender.name}}\n{{sender.team}}";

    public static readonly IReadOnlyList<StarterOutreachTemplate> All =
    [
        new("Vendor: are we affected?", "Ask a vendor's support or PSIRT whether the versions we run are affected by a CVE, and when a fix is due.",
            "Are we affected? {{product.name}} and {{article.title}}",
            "Hello,\n\nWe run {{product.name}}, version {{product.versions}}, and are reviewing this report:\n\n{{article.title}}\n{{article.link?}}\n{{article.other-links?}}\n\n"
            + "{{#article.cves}}\nCVEs:\n{{article.cves}}\n\n{{/article.cves}}\nCould you tell us:\n- whether the versions we run are affected;\n- whether a fix or workaround is available;\n"
            + "- if not, when you expect one.\n\nOur reference is {{action.number}}.\n\n" + SignOff,
            TlpLevel.Clear),
        new("Internal: action required", "Ask an asset or system owner to check, patch and look for the indicators by a date.",
            "{{action.number}} - action required by {{today:+2}}",
            "Hello,\n\nWe've had a report that affects {{product.name}} (we run {{product.versions}}):\n\n{{article.title}}\n{{article.bottom-line}}\n\n"
            + "{{#article.cves}}\nCVEs:\n{{article.cves}}\n\n{{/article.cves}}\nPlease, by {{today:+2}}:\n- check whether your systems run an affected version;\n"
            + "- apply the vendor's fix or workaround;\n- look for the indicators below in your logs, and tell us at once if you find any.\n\n"
            + "Indicators:\n{{indicators}}\n\nReply to this email when it's done, or if you can't make the date.\n\n" + SignOff,
            TlpLevel.Amber),
        new("ISAC / partner: share and ask", "Share what we've seen with an ISAC or trusted partner, and ask whether they've seen it too.",
            "[{{tlp}}] Have you seen this? {{article.title}}",
            "Hello,\n\nSharing what we've seen, in case it's useful to you:\n\n{{article.title}}\n{{article.bottom-line}}\n\n"
            + "{{#article.cves}}\nCVEs:\n{{article.cves}}\n\n{{/article.cves}}\n{{#indicators}}\nIndicators:\n{{indicators}}\n\n{{/indicators}}\n"
            + "Have you seen the same activity, or anything like it? Anything you can share back would help.\n\n" + SignOff,
            TlpLevel.Green),
        new("Follow-up / chase", "A short nudge on an email that hasn't had a reply.",
            "Following up on {{action.number}}",
            "Hello,\n\nFollowing up on my earlier email about {{action.number}}, which hasn't had a reply yet. "
            + "Could you let me know where this stands, or who would be best to ask?\n\n" + SignOff,
            TlpLevel.Clear),
        new("Internal inquiry: are we impacted?", "Ask an internal team or system owner whether they run the affected product or have seen the activity.",
            "{{action.number}} - are we impacted? Reply by {{today:+2}}",
            "Hello,\n\nWe're checking whether this affects us:\n\n{{article.title}}\n{{article.bottom-line}}\n\n"
            + "Could you tell us, by {{today:+2}}:\n- whether you run {{product.name}}, and which versions;\n"
            + "- whether you've seen any of the activity or the indicators below.\n\nIndicators:\n{{indicators}}\n\n"
            + "A short reply is fine, including \"no\".\n\n" + SignOff,
            TlpLevel.Amber),
    ];

    /// <summary>What a newer starter does better, as the page offers it.</summary>
    public const string NewerWhy = "It leaves out a heading, link or line when there's nothing to fill.";

    // The bodies starters shipped with up to v1.15, before they had optional placeholders and sections, by SHA-256 of the
    // body, with the starter each became. A template TIDE saved with one of these, its subject unchanged, hasn't been
    // edited since, so the newer body can take its place without losing anyone's work.
    private static readonly Dictionary<string, string> Earlier = new(StringComparer.Ordinal)
    {
        ["b8117b5a38a4c516b0cdebbe0067fd399786a397c3a76c5427a354d8a08dbcad"] = "Vendor: are we affected?",
        ["d1e9e14871569b1aa6a53e1b071c099b99ae78f07764f313e8926bee89e17c69"] = "Internal: action required",
        ["ad5a572fa2905c9f878481d698102514646331b5ab851a97c4ba04ca0f4e331b"] = "ISAC / partner: share and ask",
    };

    /// <summary>
    /// The newer starter <paramref name="t"/> is an older copy of, when TIDE saved it and it hasn't been changed since
    /// (its subject and body are as an earlier starter's were); otherwise null.
    /// </summary>
    public static StarterOutreachTemplate? NewerFor(OutreachTemplate t) =>
        t.CreatedByName == SavedBy
        && Earlier.TryGetValue(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(t.Body))), out var name)
        && All.FirstOrDefault(s => s.Name == name) is { } newer && newer.Subject == t.Subject
            ? newer : null;

    /// <summary>A starter as a row, saved by TIDE.</summary>
    public static OutreachTemplate ToEntity(StarterOutreachTemplate s) => new()
    {
        Name = s.Name, Description = s.Description, Subject = s.Subject, Body = s.Body, Ceiling = s.Ceiling, CreatedByName = SavedBy,
    };
}
