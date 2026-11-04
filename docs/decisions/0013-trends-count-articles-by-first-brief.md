# 0013. Trends count an article once, in the period it was first briefed, and every count opens to what it counted

- **Status:** Accepted
- **Recorded:** 2026-10-26

## Context

The board scorecard and the monthly and quarterly threat trends reports need counts by period: how many articles
carried each scorecard tag and each ATT&CK technique, and how much follow-up work the brief generated. An article has
several dates (the source's publication, the day it was written, every brief that carried it), and an update can
carry it onto later briefs. The analyst writing the report also has to get from a number to the articles behind it,
and the number has to match that list exactly, or the report can't be checked.

## Decision

- **First brief wins.** An article counts in the period of the first brief that carried it: that's when the
  organization heard about it. One never briefed counts in the period it was written, unless it's still a draft.
  Carrying an article onto a later brief doesn't count it again.
- **Once per line.** Each article counts once per period on a line. A line that sums others (a scorecard category, an
  ATT&CK tactic, a technique with its sub-techniques rolled up) counts an article once however many of its lines the
  article is on, so a category isn't the sum of its tags.
- **Counts are lists.** The report keeps the ids behind every count, not just the number. Clicking a count lists
  exactly those articles or actions, and the articles CSV for a line has exactly those rows.
- **Calendar periods.** Months and calendar quarters, in the organization's time zone. A report shows six months or five
  quarters (so the same quarter a year earlier is in it), and opens on the last complete period.
- **Actions by their own dates.** An action counts as raised on the day it was created and closed on its closed date.
  "On time" means closed on or before its due date (or it had none). "Open at period end" is worked out from those
  dates, so past periods don't change as actions are closed now.
- **An action raised for a past day counts on that day.** (Added October 2026.) An action raised while writing up a
  past day's brief records that day (`ActionItem.RaisedOn`) and counts as raised then, like the brief's articles. Its
  SLA still runs from when it was recorded, which is when the team got it, so it isn't overdue at once or missed
  when closed.
- **Computed, not stored.** Trends are worked out on request from the articles and actions. There's no snapshot table.

## Consequences

- Retagging an article, or fixing its techniques, changes past periods' counts. That's intended: the report should
  reflect the best current tagging, not a frozen mistake. A board pack that must not change is the downloaded CSV.
- A story that runs for weeks counts once, in the month it first appeared, not every month it's updated. A future
  "updates" line could count carried-over articles separately.
- The report reads every article's brief dates and every action on each request. That's fine for a team's volume
  (thousands of articles a year); a snapshot table can come later if it isn't.
