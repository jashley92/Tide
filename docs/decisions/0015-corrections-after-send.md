# 0015. A correction after a send is a short, frozen email of its own; a TLP slip is withdrawn, not recalled

- **Status:** Accepted
- **Recorded:** 2026-10-30
- **Accepted:** 2026-10-30. The maintainer approved option (b), a separate `BriefCorrection` table (migration
  `BriefCorrections` in both sets), and every proposed default:
  - a correction goes to the original send's recipients, never the edition's list as it is now;
  - the "now" side of the diff is cut at the lower of the original send's ceiling and the edition's current ceiling;
  - a withdrawn TLP slip is hidden from Readers only: it stays in the team's send log, and team members on
    `/published` get a link to the frozen send;
  - there is no test send for corrections.

## Context

A send is frozen (ADR 0007): `BriefSend` keeps the email HTML, a semantic web copy and copies of the images, and one
real send of an edition runs at a time. Articles stay editable afterwards, but nothing tells the people who got the
email. A wrong indicator, a factual error or a part that went out above the edition's ceiling (a TLP slip) has one path
today: send the whole edition again, to everyone, with nothing saying what changed. Email can't be recalled, so for a
slip TIDE can only tell the recipients, stop showing it, and keep a record.

## Decision

- **A correction is a short email about one send.** It names the edition and the brief's date, the article or articles
  it is about, and what changed. What changed has two parts: a note the writer enters (required), and a diff of each
  article as sent against the article now. The subject is the original's with "Correction:" in front.
- **The diff is cut like a send.** The "as sent" side is the article's section of the original's frozen semantic HTML,
  so it can only hold what the recipients already had. The "now" side is the article cut by `BriefEditions.Build` at
  the original send's `Ceiling`, or the edition's current ceiling if that is lower, and rendered by
  `BriefRenderer.Semantic`. Both are compared as plain text, line by line. The correction carries no images: a changed
  screenshot is described in the note, and the archive keeps the original's.
- **Sections are matched by article id**, from a `data-article` attribute the semantic rendering gains on each heading
  (a renderer change, not a schema one). Older sends are matched by headline, or the writer picks the article.
- **A correction is frozen and logged like a send.** Its email HTML and web copy are stored before the relay is asked,
  its outcome is recorded the same way (`Sending`, `Sent`, `Failed`, `Unknown`), and it is never re-rendered. It goes to
  the original send's recipients. If the edition's current list differs, the confirmation says so, and the writer can't
  add anyone: a new reader gets the next edition, not a correction of one they never had.
- **Who sends one.** The same permission as a send (`PresentBriefs`). Only a send the relay took (`Sent`) can be
  corrected; a test send, a failed one and one whose outcome is `Unknown` can't, since nobody is known to have it.
- **The confirmation mirrors `SendPlan`.** A `CorrectionPlan` carries the original send's id, the marking, the subject,
  every recipient and a fingerprint of the rendered correction email; sending is refused if any of that changed since
  the writer confirmed. The rendered correction shows beside the same tick ("I've checked the marking and the
  recipients"), and the button waits for it. One correction of a send goes at a time, and the confirmation says when
  and by whom an earlier one went.
- **The correction's marking** is the highest of the original send's marking and its own content. It never exceeds the
  ceiling it was cut at, and the note is held to the same rule: the confirm step refuses a note on the company-wide
  edition that the writer marks above the public ceiling.

### TLP slips

- **A slip is a kind of correction the writer chooses**, for content that went to an audience it shouldn't have. It
  carries no diff. Its email says that something in the edition of that date was sent above the audience's marking,
  names the article by number and headline only if the headline itself is not the slipped content, and asks recipients
  to delete the email and any copies or forwards. It never repeats, quotes or summarizes what leaked. The writer's note
  is optional, shown with a warning not to restate the content, and cut at the edition's ceiling like everything else.
- **It says it can't be recalled.** The wording is fixed: the email was not and cannot be recalled, and deleting it is
  the recipient's job. A second tick on the confirm step acknowledges this.
- **The original is withdrawn from the archive.** This happens as soon as a slip correction of a company-wide send is
  confirmed and frozen, whatever becomes of its email: still with the relay, refused, or of unknown outcome. The team
  has confirmed that the content slipped, and what Readers see doesn't wait on an email. A refused slip can be sent
  again to reach the inboxes. From then on, `/published` stops showing the send's content and its words stop matching
  the filter. The inline image endpoint stops serving its images to Readers. The send's other corrections go with it:
  their "Was" and "Now" lines quote its words, so Readers see only the slip's notice and the filter doesn't match them.
  The send can't be corrected after that, since the "as sent" side would repeat what slipped; it can only be slipped
  again. The frozen row itself is kept, unchanged, for the team's record and any review.
- **Which sends a slip withdraws.** A slip withdraws every company-wide send of its day made before the slip itself:
  the send it names, earlier ones, and later ones made before the slip. A later one may be a re-send of unchanged
  content for late recipients, or one that was put right. A slip doesn't say which line slipped, so the two can't be
  told apart for certain, and a re-send that changed something else but kept the slipped line would leak. So the slip
  withdraws a put-right re-send too. A send made after the slip is never withdrawn by it. That is how the corrected
  edition is published: send the edition again after the slip, and the send log says so under each withdrawn send.
  The day's latest send that isn't withdrawn is its edition. A day whose every send is withdrawn shows only the slip's
  notice. The notice shows whatever became of the slip's email, and a refused slip that was sent again shows as one
  notice.
- **Only the original recipients are told.** For forwarding beyond them, the confirmation points the writer to the
  organization's incident process.

### Data model

Both options need a new migration in both migration sets (SQLite in `Tide.Infrastructure` and SQL Server in
`Tide.Migrations.SqlServer`). Neither is to be implemented until the maintainer approves the schema change.

- **(a) A `Kind` on `BriefSend`** (`Edition`, `Test`, `Correction`) replacing `IsTest`, plus a nullable
  `CorrectsSendId` and `Note`, and an `IsTlpSlip` flag. It reuses the freeze, the outcome recording and the log table.
  But every query that tells a real send from a test (`Published`, the in-flight check, the latest-send check on the
  plan, `BriefProgress`, the inline image rule) treats a correction as a send unless it is changed to say otherwise.
  A missed one would replace a day's edition in `/published` with its correction (`PublishedAsync` takes the latest
  send of each date). It also needs a data migration from `IsTest` to `Kind`.
- **(b) A `BriefCorrection` table** with its own frozen fields: `CorrectsSendId` (required), `BriefId`, `BriefDate`,
  `Edition`, `IsTlpSlip`, `Note`, the article ids, `Ceiling`, `Marking`, `Subject`, `Recipients`, `RecipientCount`,
  sent at and by, `Status`, `Error`, `EmailHtml` and `WebHtml`. No images table. The freeze and outcome code is shared
  through a small helper.

**Recommended: (b).** Every existing query over `BriefSend` stays correct without change, which is where a leak or a
wrong archive would come from. The only existing query that learns about corrections is `Published`, which leaves out
a send a slip correction withdrew, and that change is the one the slip rule needs anyway. `BriefSend` keeps no
new column.

### What shows

- **The send log** in `DistributePanel` lists corrections under the send they correct: "Correction" or "TLP slip"
  badge, sender, time, recipient count, outcome, and "View as sent" for the frozen email. Each real send that went
  offers "Send a correction".
- **`/published`** marks a corrected day ("Corrected 30 Oct") and its page shows the corrections' web copies above the
  edition, newest first, each linked to the edition. A slip shows only its notice. Corrections are listed on the day
  they correct, not as days of their own, and their words join the day's for the filter.
- **Trends are unaffected.** They count articles by the brief they were first in (ADR 0013), not by sends, so neither
  a correction nor a withdrawal moves a count.
- **Exports are unaffected.** They read articles as they are now at the ceiling chosen (ADR 0012), so the corrected
  indicator is what the next export carries. A correction doesn't produce an export or a revocation.

### Tests required

- Leak tests, alongside `BriefEditions`'s:
  - a company-wide correction's email and web copy never hold a bullet, caption, "why it matters", title slide line or team opening note
    above the public ceiling, on either side of the diff, including a part that was CLEAR at send time and is AMBER now;
  - the "now" side uses the lower of the send's ceiling and the current one when an administrator has lowered it;
  - a slip correction's email and web copy hold none of the slipped text, in the note's default or in the headline
    when the headline is what slipped;
  - once a slip is confirmed, whether or not its email goes, `/published`, the published filter and
    `/files/{id}/inline` give a Reader nothing from the withdrawn sends or their other corrections, the team still
    sees the frozen rows, and a correction of a withdrawn send is refused; a re-send made before the slip is withdrawn
    too, and the edition sent after it is published and can be corrected.
- Confirmation tests: refused when the fingerprint, marking, recipients or article changed after confirming; refused
  for a test, failed or `Unknown` send; one correction of a send at a time; refused without `PresentBriefs`.
- Archive tests: a correction never replaces a day's edition in `/published`, never counts as the edition's latest
  send on the plan, and never blocks the next send.
- Diff tests (by `data-article`, by headline, an article since removed), render tests for the send log, the dialog
  and a corrected and a withdrawn `/published` day, and both migration sets.

## Consequences

- A mistake is put right with a short email, and the archive says what changed.
- A slip can't be taken back; TIDE says so and stops showing it.
- The schema grows by one table, in both migration sets, once the maintainer approves.
- The note is free text on a path where a careless writer could restate a leak; the warning and the confirm step are
  the only guard against that.
