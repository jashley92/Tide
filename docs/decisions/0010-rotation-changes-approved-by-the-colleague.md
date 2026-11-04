# 0010. Brief rotation swaps and cover are approved by the colleague, and the manager is told

- **Status:** Accepted
- **Recorded:** 2026-10-26

## Context

One analyst presents the daily brief each week; the brief rotation says who. It isn't the team's on-call schedule
(that's kept elsewhere and runs on its own terms). People trade days and weeks, or ask someone to cover, often at
short notice. A manager wants to know who's presenting but shouldn't be a bottleneck for a trade two analysts have
agreed.

## Decision

- **A weekly rota plus day-level changes.** An administrator orders the roster and fills weeks in that order
  (`RotationWeek`). An approved change writes `RotationOverride`s for the days it moves; who presents on a day is the
  override, else the week's analyst.
- **Cover and swap.** A cover hands some of the requester's days to a colleague. A swap also hands some of the
  colleague's days back (days or whole weeks, up to 20 business days each way). The requester must hold every day they
  give, and for a swap the colleague every day they give back, both when asked and again when approved.
- **Only the colleague approves.** The person taking on the days approves or declines; nobody else can. An
  administrator can arrange cover on someone's behalf, but the colleague still approves it, and only an administrator
  can undo an approved change (its days go back to what they were before it).
- **The manager is told, not asked.** Every approved change emails the configured manager addresses; the requester is
  told the outcome either way.
- **The rotation feeds the brief.** A new brief is presented by whoever the rotation names for that day, the
  brief-not-ready nudge goes to them when there's no brief yet, and an optional reminder emails whoever presents the
  next business day. Each person can download their brief days as an iCalendar file.
- **A past day is recorded, not requested.** When a past day's brief was presented by someone other than the analyst
  the rotation named (a brief written up after the fact), the brief's presenter, the rota's analyst or an
  administrator can record it from the brief page as a cover that's approved at once. The brief is the evidence that
  it happened, so there's nothing to approve; the other analyst and the manager are told as for any change. A new
  brief for a past day is presented by whoever starts it, not the rota's analyst.
- **Named for what it is.** It's the *brief rotation* and the *presenter* throughout (pages, emails, the calendar file,
  configuration, code), never "on call", so it isn't mistaken for the on-call schedule.

## Consequences

- Trades happen without waiting on a manager, and the manager still has a record of each one (and the history on the
  rotation page).
- A change made on top of another (covering a day you were covering for someone) replaces the earlier override for
  that day. Undoing the later one gives the day back to the earlier change if it still stands, else to the rota.
  (Until October 2026 it went straight back to the rota, which left the earlier change "approved" in the history
  but with no effect.)
