# 0020. External teams are tracked in TIDE and emailed as people without it

- **Status:** Accepted
- **Recorded:** 2026-11-06

## Context

A team often takes over work from another and splits it among several responsible teams, each with its own due date,
then follows it until it's done. Some of those teams don't use TIDE: a network or hosting team, a vendor, a
department with its own ticket queue. Until now such work was raised to an internal team with no members, so it sat on
nobody's list, the meeting called it "no one's", and its reminders went nowhere. A team may also want a cap on how much
it has active at once, without the work it only follows counting toward it.

## Decision

- **An external team is a team.** `Team.IsExternal` marks one tracked here and working elsewhere. It owns actions,
  appears in every team picker, filter, trend and overdue count, and can be handed work or split to. It has no members,
  so no assignee, and it's left out wherever TIDE users are assumed: My teams, Workload's holders, the member digests,
  the meeting's "no one to own".
- **Kept by the people who hand work out.** Anyone with `WorkActions` adds, edits and retires external teams (on their
  own page, External teams, linked from Teams & SLA and the Actions page); internal teams stay the administrators'.
  Every change is audited as any team's is. A limit on an external team is still an administrator's to set.
- **Reminded as someone without TIDE.** An external team's reminders go to its contact email at the daily reminder
  time, at its own cadence (business days before the due date, then every so many while overdue, or once), counted in
  the organization's working days. One email per team lists each request's title, details as written on the action, due
  date and how late, and ticket link. It has no TIDE link, number or wording only TIDE's users would follow, and
  nothing from an article that wasn't written into the action. Replies go, by Reply-To, to whoever follows the work. It
  is an email template an administrator can reword, off until an administrator turns it on, and each team and action
  can be left out. An action left out stays left out when it's handed to another team only if whoever hands it off
  leaves *Keep reminders off* ticked: the hand-off asks, rather than carrying the choice over unseen.
- **Tracking.** A new status, Tracking (stored as 6), means "handed to others, being followed". It's open (due dates,
  SLA and overdue apply) but not active, so it doesn't count toward a limit.
- **An active limit per team**, optional, with three modes: show the count, warn and ask why (kept in the history), or
  refuse. A change goes over it when an action is created, moved into an active status, or handed to the team.

## Consequences

- Work handed outside TIDE stays visible and counted, and its owner is nudged without being given an account.
- The external email is TIDE's first to people outside the organization's TIDE users: it carries the marking of any
  article an action came from, as the other reminders do, and its wording is kept free of links that would fail for its
  readers. Administrators should review the template before turning the reminders on
  ([integrations](../operations/integrations.md)).
- An action owner's permission grows: they can add a team. It can own actions but no one can be added to it, so it
  grants nobody any access.
