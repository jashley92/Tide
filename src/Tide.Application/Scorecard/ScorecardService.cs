using System.Text;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Entities;

namespace Tide.Application.Scorecard;

/// <summary>
/// The board-scorecard taxonomy: categories and the tags under them. Admin-managed; a category or tag something uses is
/// retired, not deleted, so past articles keep their tags and the scorecard's history holds.
/// </summary>
public sealed class ScorecardService(IAppDbContextFactory dbs, ICurrentUser user)
{
    /// <summary>
    /// The starter set a new install gets: generic enough for any organization, and meant to be edited. Each category's
    /// tags follow it.
    /// </summary>
    public static readonly IReadOnlyList<(string Category, string Description, string[] Tags)> StarterSet =
    [
        ("Ransomware & extortion", "Ransomware groups, data-theft extortion, and the access brokers behind them.",
            ["Ransomware", "Data extortion", "Initial access brokers"]),
        ("Vulnerabilities", "Flaws in software the organization runs, and their exploitation.",
            ["Actively exploited", "Zero-day", "Patch released", "Unpatched disclosure"]),
        ("Third-party & supply chain", "Risk that reaches us through vendors, suppliers and their software.",
            ["Vendor breach", "Software supply chain", "Open source", "Cloud / SaaS provider", "PKI & certificate trust"]),
        ("Phishing & fraud", "Social engineering, business email compromise and payment fraud.",
            ["Phishing", "Business email compromise", "Payment fraud"]),
        ("Threat actors", "Who is behind it.", ["Nation-state", "Cybercrime", "Hacktivism", "Insider"]),
        ("Our sector", "Activity aimed at organizations like ours.", ["Financial sector", "Peer organization targeted"]),
        ("Regulatory & policy", "Rules, guidance and enforcement that change what we must do.",
            ["Regulation", "Government advisory"]),
    ];

    /// <summary>Every category with its tags, in order; retired ones too when <paramref name="includeRetired"/>.</summary>
    public async Task<IReadOnlyList<ScorecardCategory>> ListAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        // The tags are read on the article pages (team edition); a Reader has no page that needs them.
        if (!user.Has(Domain.Enums.Permission.ReadTeamEdition)) throw new ForbiddenException("You don't have access to the scorecard tags.");
        await using var db = await dbs.CreateAsync(ct);
        var cats = await db.ScorecardCategories.AsNoTracking().Include(c => c.Tags)
            .Where(c => includeRetired || !c.IsRetired)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        foreach (var c in cats)
            c.Tags = c.Tags.Where(t => includeRetired || !t.IsRetired).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToList();
        return cats;
    }

    /// <summary>Adds the starter set when there are no categories at all (first run).</summary>
    public async Task SeedStarterSetAsync(CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        if (await db.ScorecardCategories.AnyAsync(ct)) return;
        var order = 0;
        foreach (var (name, description, tags) in StarterSet)
        {
            var cat = new ScorecardCategory { Key = KeyFor(name), Name = name, Description = description, SortOrder = order++ };
            var tagOrder = 0;
            foreach (var t in tags) cat.Tags.Add(new ScorecardTag { Key = KeyFor(t), Name = t, SortOrder = tagOrder++ });
            db.ScorecardCategories.Add(cat);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> AddCategoryAsync(string name, string? description, CancellationToken ct = default)
    {
        RequireAdmin();
        var clean = AdminLists.Name(name);
        await using var db = await dbs.CreateAsync(ct);
        var key = await AdminLists.UniqueKeyAsync(KeyFor(clean), k => db.ScorecardCategories.AnyAsync(c => c.Key == k, ct));
        var order = await db.ScorecardCategories.Select(c => (int?)c.SortOrder).MaxAsync(ct) ?? -1;
        var cat = new ScorecardCategory { Key = key, Name = clean, Description = AdminLists.Description(description), SortOrder = order + 1 };
        db.ScorecardCategories.Add(cat);
        await db.SaveChangesAsync(ct);
        return cat.Id;
    }

    public async Task<Guid> AddTagAsync(Guid categoryId, string name, string? description, CancellationToken ct = default)
    {
        RequireAdmin();
        var clean = AdminLists.Name(name);
        await using var db = await dbs.CreateAsync(ct);
        if (!await db.ScorecardCategories.AnyAsync(c => c.Id == categoryId, ct)) throw new KeyNotFoundException("That category no longer exists.");
        var key = await AdminLists.UniqueKeyAsync(KeyFor(clean), k => db.ScorecardTags.AnyAsync(t => t.Key == k, ct));
        var order = await db.ScorecardTags.Where(t => t.CategoryId == categoryId).Select(t => (int?)t.SortOrder).MaxAsync(ct) ?? -1;
        var tag = new ScorecardTag { CategoryId = categoryId, Key = key, Name = clean, Description = AdminLists.Description(description), SortOrder = order + 1 };
        db.ScorecardTags.Add(tag);
        await db.SaveChangesAsync(ct);
        return tag.Id;
    }

    /// <summary>Renames a category; its key (and every article's tag history) stays.</summary>
    public async Task UpdateCategoryAsync(Guid id, string name, string? description, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var cat = await db.ScorecardCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new KeyNotFoundException("That category no longer exists.");
        cat.Name = AdminLists.Name(name);
        cat.Description = AdminLists.Description(description);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateTagAsync(Guid id, string name, string? description, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var tag = await db.ScorecardTags.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException("That tag no longer exists.");
        tag.Name = AdminLists.Name(name);
        tag.Description = AdminLists.Description(description);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Retires (or brings back) a category. Its tags stay on past articles but can't be chosen while it's retired.</summary>
    public async Task SetCategoryRetiredAsync(Guid id, bool retired, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var cat = await db.ScorecardCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new KeyNotFoundException("That category no longer exists.");
        cat.IsRetired = retired;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Retires (or brings back) a tag. A tag no article ever used is deleted instead of retired.</summary>
    public async Task SetTagRetiredAsync(Guid id, bool retired, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var tag = await db.ScorecardTags.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException("That tag no longer exists.");
        if (retired && !await db.ArticleTags.AnyAsync(a => a.TagId == id, ct)) db.ScorecardTags.Remove(tag);
        else tag.IsRetired = retired;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Moves a category up (negative) or down (positive).</summary>
    public async Task MoveCategoryAsync(Guid id, int by, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var all = await db.ScorecardCategories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        AdminLists.Move(all, all.FindIndex(c => c.Id == id), by, (c, i) => c.SortOrder = i);
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveTagAsync(Guid id, int by, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var tag = await db.ScorecardTags.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException("That tag no longer exists.");
        var siblings = await db.ScorecardTags.Where(t => t.CategoryId == tag.CategoryId).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        AdminLists.Move(siblings, siblings.FindIndex(t => t.Id == id), by, (t, i) => t.SortOrder = i);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A stable key from a name: lower-case letters and digits joined by hyphens ("Third-party &amp; supply
    /// chain" is "third-party-supply-chain").</summary>
    public static string KeyFor(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var key = sb.ToString().Trim('-');
        return key.Length == 0 ? "tag" : key.Length > 60 ? key[..60].TrimEnd('-') : key;
    }

    private void RequireAdmin()
    {
        if (!user.Has(Domain.Enums.Permission.Administer)) throw new ForbiddenException("Only an administrator can change the scorecard.");
    }
}
