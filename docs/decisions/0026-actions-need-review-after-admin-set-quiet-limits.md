# 0026. Actions need review when overdue, escalated, or quiet past limits an administrator sets

- **Status:** Accepted
- **Recorded:** 2026-11-12
- **Accepted:** 2026-11-12, in #211.

## Context

The meeting's open-actions slide listed overdue and escalated actions. By then the work is already late. Work that has
simply gone quiet is the earlier warning, but how long is too long differs between teams, and blocked work usually
waits on someone else.

## Decision

- The slide is **Actions that need review**: open actions that are overdue, escalated, or have had no update by a
  person for too long, each once, most pressing first, at most 8, each saying why and one thing the room can do.
- "Too long" is **2 business days** for Open or In progress and **5** for Blocked, set by an administrator in
  Organization settings (`Org:StaleOpenDays`, `Org:StaleBlockedDays`, 1 to 30). Business days are the organization's:
  its time zone, weekends and holidays, as due dates count them.
- An update is anything a person did (a comment, a status change, a checklist tick, a new holder or due date). TIDE's
  own reminders, escalation lines and emails don't count.

## Consequences

- The team email's "Still open from earlier briefs" keeps to overdue and escalated actions; only the slide changed.
- An action raised from an article above the meeting's ceiling is neither named nor counted.
