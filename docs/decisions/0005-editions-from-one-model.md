# 0005. Every edition of a brief is cut from one model by TLP

- **Status:** Accepted
- **Recorded:** 2026-10-25

## Context

The brief goes to the security teams in full, and to the whole company (SharePoint) without restricted content.
Keeping two hand-edited copies invites a restricted bullet leaking into the public one.

## Decision

- Each article, bullet and screenshot carries a TLP marking, and a part counts at least at its article's.
- `BriefEditions.Build` returns only what an edition's ceiling allows. Restricted parts are left out of the result,
  not hidden, and that includes captions.
- Every rendering uses it: the preview, present mode, the email and the SharePoint copy (ADR 0007).
- "Why it matters to us" has a TLP of its own like a bullet, since it often holds internal context. A part marked
  above its article shows in the running order and on its slide ("holds TLP:AMBER"), so an article that looks CLEAR
  can't hide an AMBER part from the presenter.
- The team edition also carries each article's indicators (defanged, up to 15, the rest pointing at the export) and the
  follow-up actions raised from it. The company-wide edition never carries either.
- The brief's opening note has a TLP of its own (added October 2026; CLEAR for briefs written before). An edition
  whose ceiling is below it leaves the note out, and one that carries it is marked at least that high.
- The ceilings are administered in the app. The public one defaults to GREEN and the team one to AMBER+STRICT.

## Consequences

- One function decides what an audience sees, and its tests are the leak tests.
- An analyst marks content once, where it's written, and never edits a copy.
