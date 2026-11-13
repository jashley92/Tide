# 0027. An external team's unheld action is named by its ticket wherever a holder would be

- **Status:** Accepted
- **Recorded:** 2026-11-12
- **Accepted:** 2026-11-12, in #213. Builds on [0020](0020-external-teams-are-emailed-without-tide.md).

## Context

An external team has no members in TIDE, so its actions have no holder and read "unassigned" or "nobody yet". The
meeting then suggested reassigning work that was in fact in another team's queue, and the board looked as if nobody
had it.

## Decision

- Where a holder is named (board, list, action page and rails, Today, the meeting's slides and wrap-up, the editions,
  emails, search, Our stack), an external team's action that nobody in TIDE holds shows its **ticket** ("INC-4411",
  linked when the ticket link is http or https), or "With *team* · no ticket yet".
- One rule decides the words (`HolderLabel`). A holder in TIDE still wins; internal teams read as before.
- Counting and filtering still go by the assignee. Emails name the ticket and never link it; a Visitor gets words only.
- In the meeting, such an action is never suggested for reassignment: the room asks for an update on its ticket.

## Consequences

- "No owner" and the unassigned counts still include these actions, since nobody in TIDE holds them.
