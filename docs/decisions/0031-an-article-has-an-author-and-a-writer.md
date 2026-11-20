# 0031. An article has an author and a writer

- **Status:** Proposed
- **Recorded:** 2026-11-20

## Context

An article's author is set when it's made and never changes. Drafts outlive shifts, sick days and rotations: a
half-written P1 sits under someone who isn't there, today's presenter can't see it on Today (*Not on a brief yet* lists
only Ready articles), and "Only mine" means "started by me", not "mine to finish".

## Decision

- **Author is credit; writer is who has it now.** `AuthorId` stays the person who started the article and is what the
  archive, "By …" in the editor and the reports credit. `WriterId` is who is finishing it; empty means the author.
- **Anyone who may edit the article may hand it on**, while it's a Draft or Ready, to anyone who can write articles,
  themselves included (taking it over). Drafts are the team's: any analyst can already edit any article. A hand-off
  records who, to whom, when and why, in the history and the audit log. It needs no acceptance (unlike rotation swaps,
  ADR [0010](0010-rotation-changes-approved-by-the-colleague.md)), because it changes no one's commitments outside
  TIDE. It leaves the edit version alone, so an editor open on the article isn't made stale.
- **"Mine" means the writer** in the brief picker's *Only mine* and on Today's *Drafts in progress*.
- **The new writer is told** on Today (the card, and a toast while it's open), and by email unless they turned it off in
  My notifications, as the rotation's requests are. The email is marked with the article's TLP and doesn't name an
  article above the team edition's ceiling.
- **Reviews still go by the author** for now: the author can't review their own article, and the author or whoever
  asked takes a request back.

## Consequences

- One migration on each provider adds four nullable columns (`WriterId`, `WriterName`, `WriterSinceUtc`,
  `HandoffNote`) and fills the writer with the author, since the article was made.
- Reports that count by person must say whether they count authors or writers. None does today: Trends, brief
  timeliness and the team scorecard count articles and actions, not people.
- A new email template (`article-handoff`) and notification preference (`notify.articleHandoffs`), both appended.
