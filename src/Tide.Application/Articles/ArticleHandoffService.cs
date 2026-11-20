using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Application.Preferences;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using static Tide.Application.Distribution.EmailComposer;

namespace Tide.Application.Articles;

/// <summary>Someone an article can be handed to: a colleague who can write articles.</summary>
/// <param name="Me">It's the signed-in person: handing it to themselves takes it over.</param>
public sealed record ArticleWriterChoice(string Id, string Name, bool Me);

/// <summary>
/// Handing an article to a colleague to finish (ADR 0031). The author stays the credit; the writer is who has it now.
/// Anyone who may edit the article may hand it on, to anyone who can write articles, themselves included (taking it
/// over). The hand-off is the article's own save, so the audit trail and its history record it ("Handed from Pat to
/// Sam"), and open pages hear it; it leaves the edit version alone, so an editor open on the article can still save.
/// The new writer sees it on Today, and is emailed unless they turned it off in My notifications.
/// </summary>
public sealed class ArticleHandoffService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock, IEmailSender email,
    IOptions<AppOptions> app, Settings.DistributionSettings distribution, EmailTemplateStore? templates = null)
{
    private readonly EmailTemplateStore _templates = templates ?? EmailTemplateStore.Defaults;

    /// <summary>Why a hand-off is refused to someone who can't write articles.</summary>
    public const string Refusal = "Only analysts can hand an article on.";

    /// <summary>What the email says in place of the note for an article above the team edition's ceiling.</summary>
    public const string WithheldNote = "A note came with it: open TIDE to read it.";

    /// <summary>Who <paramref name="articleId"/> can be handed to: everyone who can write articles but its writer now, by
    /// name, the signed-in person (taking it over) among them unless they're the writer.</summary>
    public async Task<IReadOnlyList<ArticleWriterChoice>> ChoicesAsync(Guid articleId, CancellationToken ct = default)
    {
        Require();
        await using var db = await dbs.CreateAsync(ct);
        var writer = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => a.WriterId ?? a.AuthorId).FirstOrDefaultAsync(ct)
                     ?? throw new KeyNotFoundException("That article no longer exists.");
        return await WritersAsync(db, writer, ct);
    }

    // Everyone whose roles (as last seen at sign-in) let them write articles, but the writer now.
    private async Task<IReadOnlyList<ArticleWriterChoice>> WritersAsync(IAppDbContext db, string writer, CancellationToken ct)
    {
        var people = new Dictionary<string, AppUser>();
        foreach (var role in WriterRoles)
            foreach (var u in await db.Users.AsNoTracking().Where(u => u.RolesCsv.Contains(role)).ToListAsync(ct))
                people.TryAdd(u.Sid, u);
        return [.. people.Values.Where(u => u.Sid != writer && u.DisplayName.Length > 0 && StoredRoles.Has(u.RolesCsv, Permission.WriteArticles))
            .Select(u => new ArticleWriterChoice(u.Sid, u.DisplayName, u.Sid == user.UserId))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    // The roles that can write articles: the first cut of who may be handed one.
    private static readonly string[] WriterRoles = RoleDefinitions.SystemRoles
        .Where(r => r.Value.Contains(Permission.WriteArticles)).Select(r => r.Key.ToString()).ToArray();

    /// <summary>
    /// Hands <paramref name="articleId"/> to <paramref name="toUserId"/> with an optional <paramref name="note"/>. Refused
    /// to someone who can't write articles, for a target who can't, for an article that's been presented (there's nothing
    /// left to finish), and when <paramref name="expectedWriterId"/> (the writer the page showed) is no longer the writer:
    /// a colleague handed it on meanwhile. Returns whether the new writer was emailed (or may have been).
    /// </summary>
    public async Task<bool> HandToAsync(Guid articleId, string toUserId, string? note, string expectedWriterId, CancellationToken ct = default)
    {
        Require();
        var cleanNote = ArticleService.CleanOrNull(note, Article.MaxHandoffNote);
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Articles.FirstOrDefaultAsync(x => x.Id == articleId, ct) ?? throw new KeyNotFoundException("That article no longer exists.");
        if (a.Status is not (ArticleStatus.Draft or ArticleStatus.Ready))
            throw new InvalidOperationException("This article has been presented, so there's nothing left to hand on.");
        if (a.CurrentWriterId != expectedWriterId)
            throw new StaleEditException($"{a.CurrentWriterName} has this article now. Reload it to see who has it.");
        var to = (await WritersAsync(db, a.CurrentWriterId, ct)).FirstOrDefault(c => c.Id == toUserId)
                 ?? throw new ArgumentException(toUserId == a.CurrentWriterId ? $"{a.CurrentWriterName} already has it." : "Choose a colleague who can write articles.");

        var from = a.CurrentWriterName;
        var since = clock.Now.ToUniversalTime();
        // The hand-off doesn't move the edit version (nothing anyone wrote changed, so an editor open on the article can
        // still save), so that can't guard it. It's claimed in the database instead, only from the writer the page showed:
        // of two hand-offs at once, the second matches nothing and is refused, before anyone is told.
        var claimed = await db.Articles
            .Where(x => x.Id == articleId && (x.WriterId == expectedWriterId || (x.WriterId == null && x.AuthorId == expectedWriterId)))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.WriterId, to.Id).SetProperty(x => x.WriterName, to.Name)
                .SetProperty(x => x.WriterSinceUtc, since).SetProperty(x => x.HandoffNote, cleanNote), ct);
        if (claimed == 0) throw new StaleEditException("Someone else handed this article on just now. Reload it to see who has it.");
        // The same values again through the tracked article, so the audit trail, the history and open pages hear it.
        (a.WriterId, a.WriterName, a.WriterSinceUtc, a.HandoffNote) = (to.Id, to.Name, since, cleanNote);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new StaleEditException("Someone else saved this article just now. Reload it and try again.");
        }
        return to.Me ? false : await NotifyAsync(db, a, from, to, ct);
    }

    // Emails the new writer, unless they turned it off. Sent after the save, so a relay that's down leaves the hand-off made.
    // The email leaves TIDE, so it's marked with the article's TLP, and an article above the team edition's ceiling isn't
    // named in it, nor is the note, which may say as much as the title.
    private async Task<bool> NotifyAsync(IAppDbContext db, Article a, string from, ArticleWriterChoice to, CancellationToken ct)
    {
        var recipients = (await RecipientResolver.ResolveAsync(db, NotificationKinds.ArticleHandoffs, [to.Id], ct)).Reachable();
        if (recipients.Count == 0) return false;
        var (_, team) = await distribution.ReadAsync(ct);
        var named = a.Tlp <= team;
        var t = _templates.Render(EmailTemplateCatalog.ArticleHandoff, new Dictionary<string, string>
        {
            ["Writer"] = to.Name, ["By"] = user.DisplayName, ["Before"] = from,
            ["Article"] = named ? a.Title : $"a draft marked {Tlp.Label(a.Tlp)}",
            ["Note"] = a.HandoffNote is not { } n ? ""
                : named ? $"<p style=\"margin:0 0 12px;\">“{Esc(n)}”</p>"
                : $"<p style=\"margin:0 0 12px;\">{Esc(WithheldNote)}</p>",
            ["ArticleLink"] = Link($"articles/{a.Id}"),
        });
        var marking = $"<p style=\"margin:0 0 12px;font-size:13px;color:{Palette.Muted};\">{TlpLabel(a.Tlp)} {Esc(Tlp.Meaning(a.Tlp))}</p>";
        var html = Shell(t.Heading, clock.TeamName, marking + t.BodyHtml, t.Footer);
        var result = await email.SendAsync(new OutgoingEmail(recipients, $"{t.Subject} [{Tlp.Label(a.Tlp)}]", html, ToText(html), []) { Template = t.TemplateId }, ct);
        return result.MayHaveGone;
    }

    private string Link(string path) => string.IsNullOrWhiteSpace(app.Value.BaseUrl) ? ""
        : $" (<a style=\"color:{Palette.Link};\" href=\"{Esc(app.Value.BaseUrl.TrimEnd('/') + "/" + path)}\">open it</a>)";

    private void Require()
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException(Refusal);
    }
}
