# 0030. "Since you last looked" is yours alone, read from what TIDE already records

- **Status:** Proposed
- **Recorded:** 2026-11-20

## Context

Analysts come back to TIDE after a meeting, a day off or a shift and have to hunt for what changed: actions handed to
them, reviews decided, their articles edited by colleagues, new KEV items. The personal digest email lists "new to you
since your last digest", but it's opt-in, by email, and about actions only.

## Decision

- **One mark per person.** TIDE keeps when each person last marked Today's changes seen, as their own preference row
  (`seen.today`). Nobody else can read it; it isn't audited and isn't shown on any page but theirs.
- **It moves when they say so.** *Mark all seen* moves it, to when the card was read; opening Today doesn't. On the
  first look there's no mark, so nothing is listed and the mark starts then.
- **Read, not written.** What changed is read from the records TIDE already keeps, by their own times (actions and
  their comments, article edits, reviews, sends, triage items). No activity table is added, and nothing new is
  recorded about what people look at. "Yours" for actions is the personal digest's rule, kept in one place.
- **Same visibility as the pages.** Each part is read only for someone the page it links to would let in. The card
  never shows something its link would refuse.
- **Bounded.** At most 7 days and 50 lines; the rest is "and N more". A fixed number of queries, whatever changed.
- **Not a notification system.** No email, no badge outside Today, never on Present's shared screen. Emails stay with
  reminders and digests ([0021](0021-escalation-is-an-administered-ladder.md), My notifications).

## Consequences

- A record's "who changed it last" says only the latest change: "edited by Sam" may stand for several edits, and a
  colleague's change followed by yours isn't listed. Counting each change would need the audit log or a table.
- Per-row "new to you" markers on lists would need a per-entity mark (a table). That's a separate decision.
- A person who never marks seen sees a rolling 7 days.
