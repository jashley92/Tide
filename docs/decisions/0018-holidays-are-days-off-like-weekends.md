# 0018. Holidays are dated days off, kept by an administrator, and count as a weekend does

- **Status:** Accepted
- **Recorded:** 2026-11-03

## Context

TIDE's working days were Monday to Friday, so a public holiday was a working day. On one, Today expected a brief and
the brief-not-ready email went out. An action's SLA counted the day, so a deadline could land on it and read as overdue
the next morning. The rota showed someone presenting, and the brief before it carried its articles over to a meeting
nobody would hold. The owner chose Option A of EP-82 over rules that repeat each year (Option B).

## Decision

- An administrator keeps a list of dated holidays (date and name) on Organization settings: added one at a time,
  imported from a calendar file (.ics, each all-day event), or copied from a year to the next and then checked. A
  repeating calendar event is skipped and said so: its dates aren't listed.
- A holiday is a day off exactly as a weekend is. `WorkingDays` (Monday to Friday, less the holidays) is the one rule,
  and the org clock holds the current one (`OrgClock.Days`), loaded at start-up and replaced on each change. Brief days,
  carry-over, the rota, SLA due dates and states, urgency groups, reminders and the brief-not-ready nudge, and the
  metrics' sparklines and missed-day counts all read it. A brief can't be started on a holiday.
- Metric values entered for a holiday are kept (a SOC's numbers don't stop), but a holiday isn't expected to have one.
- A weekend can't be a holiday: it's a day off already.
- Holidays travel in the configuration bundle, matched by date.
- In December, once holidays are kept, Administration's overview says when next year has none.

## Consequences

- Someone adds each year's dates; the December reminder and the copy prompt it.
- A day that's a holiday for some of the team but not all isn't modelled: it's a holiday or it isn't.
- An action's due date is fixed when it's raised, so a holiday added later doesn't move it; the SLA states read the
  calendar as it is.
