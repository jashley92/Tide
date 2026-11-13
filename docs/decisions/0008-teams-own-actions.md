# 0008. Teams own actions; being on the team is what lets you work one

- **Status:** Accepted
- **Recorded:** 2026-10-26

## Context

M3 tracks the follow-up work a brief creates, often done by teams outside CTI (vulnerability management, the SOC).
Those people hold the Action owner role, which grants `WorkActions` to everyone in it. Without a narrower rule, any
action owner could close any team's action. SLA targets are a matter of days in practice, and the meeting is held on
business days. Reminders go to people who didn't ask for them, so repeats are costly.

## Decision

- **Ownership.** Every action belongs to one team, and optionally to one assignee. Teams and their members are
  administered in the app, with the retire-don't-delete pattern.
- **Who works an action.** Analysts (`WriteArticles`) create and edit any action. Someone with `WorkActions` works an
  action only if they're on its team: status, outcome, assignee, due date, ticket, comments and hunts. Reading is
  open to anyone with the team edition or `WorkActions`. The service checks this; pages only hide buttons.
- **Closing.** A Done action needs an outcome; a hunt or sweep needs one that says what it found. Each change also
  writes a readable line to the action's history, beside the audit log.
- **SLA.** Targets are in the organization's business days, per priority with optional per-kind overrides that win,
  and only set the default due date: the due date is what's tracked. States are on track, due soon (today or the
  next business day), overdue, and met or missed once Done.
- **Reminders.** Due-soon, overdue, the weekly team digest and the brief-not-ready nudge are scans, each off until
  enabled. They send one email per recipient (batched), go to the assignee, else the team mailbox, else its members,
  and write a mark (`NotificationMark`) so a thing is sent once. A due date that moves earns a new reminder. A send
  that fails isn't marked, so the next scan tries again.
- **Hunts** follow ADR 0006: TIDE writes the query from templates and records the result; it never runs one. Templates
  are per kind of value rather than per indicator type: IPv4 and IPv6 addresses, and each hash algorithm, are split
  out, so a template can query exactly the fields that hold them. Administrators can add a template for any kind and
  turn any kind off.

## Consequences

- An action owner sees every action but changes only their own team's, so membership has to be kept up to date
  (people appear once they've signed in).
- A team without a mailbox or members gets no reminders for its unassigned actions; the Teams page shows who's on each.
- Marks grow by a few rows a day; they're small and indexed by time if they ever need pruning.

## Amended 2026-10-30: anyone who reads the team edition may comment

The workflow review found that action owners on other teams need to ask questions on an action and add what they found
(a host they saw the indicator on, a ticket of their own), and the only way to do that was outside TIDE.
- **Comments.** Anyone with `ReadTeamEdition` may comment, with text, on any action they can see, whichever team owns
  it. An action owner on another team gets the comment box but not the status, the edit form or the checklist.
- **Editing and deleting a comment** stay with its author, on whichever team, and administrators.
- **Files on a comment** (screenshots and evidence) stay with whoever works the action: the owning team and analysts.
- Status, outcome, assignee, due date, ticket, checklist and hunts are unchanged: the owning team and analysts only.

## Amended 2026-11-12: an assignee is a member of the action's team

The meeting's assign already required a member of the action's team, but the action editor offered, and saved, anyone.
An action then sat with someone its team didn't know, outside the team's lists and reminders.
- **An assignee is a member of the owning team**, whichever way it's given: the editor (create and save), the
  meeting's assign, and the meeting's quick add (whoever raises it holds it only when they're on the team). The service
  checks it; the editor offers only the team's members. An external team has no members, so its actions are nobody's.
- **What's there stays.** An action whose holder isn't on its team (given it before this, or an analyst who took it on
  before Take it was held to the same rule)
  still shows them, and an edit that leaves the holder alone saves. Only a change of holder is checked.
- **A move to another team** keeps the holder only if they're on the new team; otherwise the action is the new team's.
- **Taking an action on (*Take it*) is for a member of its team too.** The service refuses anyone else, by the same
  rule as giving it, and Take it is shown only to a member: on the action page, the Actions list and board and their
  rail, a lookup's sweeps, Today's queues (which list only the person's own teams) and the meeting's rail. Where it
  isn't shown, nothing takes its place. An analyst who isn't on the team still works the action (status, comments,
  hand-off) as before. An external team has no members, so nobody takes its actions.
