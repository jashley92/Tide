# 0021. Escalation is an administered ladder of steps, run by the reminder sweep

- **Status:** Accepted
- **Recorded:** 2026-11-07

## Context

Reminders tell an action's owner it's due or late, once. When work keeps slipping, a team escalates: it reminds the
owners again, copies someone with authority over the owning team, raises the priority, and puts the action in front of
the daily meeting. Done by hand, that depends on someone noticing, happens differently each time, and is easy to
forget for a team that works outside TIDE. Organizations escalate differently, so the steps can't be built in.

## Decision

- **An administrator defines a short ladder.** On Teams & SLA: a switch (off by default), which actions it applies to
  (all, only parts of a split, or only external teams' actions), and up to 8 ordered steps. Each step is a when (so
  many business days before the due date, the due day, or so many after it, 0 to 60) and what it does, from a fixed
  menu: remind the owning team, copy the team's escalation contact, raise the priority one level (never above P1), flag
  it escalated. The menu is fixed so every step is something TIDE can explain and undo by moving the date; nothing is
  scripted. The ladder is three settings rows (`Notifications:Escalation:*`), the steps as one short text, so it's
  saved, audited and carried in the configuration bundle as every operational setting is.
- **It runs in the reminder sweep**, at the reminder time on business days, counting the organization's working days
  with its holidays, as the external teams' reminders do (ADR [0018](0018-holidays-are-days-off-like-weekends.md),
  [0020](0020-external-teams-are-emailed-without-tide.md)). No new job, schedule or calendar.
- **Once per action and due date.** Each step is marked (`NotificationMark`) and written in the action's history. A new
  due date is a new commitment, so its ladder starts again. Only the most recent step that has come runs: one passed
  over (the ladder turned on with work already late) never does, so turning it on doesn't email every old action at
  once.
- **Emails reuse what each team already gets.** Remind is the team's own reminder: an external team's no-TIDE email, an
  internal team's due-soon or overdue email to its assignee, mailbox or members, with their opt-outs. The escalation
  contact (a name and email on each team, kept by whoever keeps that team) gets its own template, written for someone
  who may not use TIDE: no TIDE link or number for an external team, a link to each request for an internal one.
- **Escalated is derived, never cleared.** `ActionItem.EscalatedForDue` records the due date it was flagged for; it's
  escalated while it's open and still due then: a badge, Today's Escalated card for those who chase it, and the
  meeting's overdue list, first. Closing it or moving its date ends it with no code at every place that does those,
  in step with the marks, which are per due date too.

## Consequences

- Slipping work is chased the same way every time, and the history says what was done and when.
- A step can email outside TIDE's users (an external team, an escalation contact), so its wording is an administrator's
  to review before turning it on, as the external reminders' is ([integrations](../operations/integrations.md)).
- A raised priority isn't lowered when the date moves; the history says what it was, and whoever works the action sets
  it back if it should be.
- A missed sweep (the server down on a step's day) runs that step the next business day unless a later step has come
  by then, in which case the earlier one is passed over.
- New kinds of step need code: the menu is deliberately closed.
