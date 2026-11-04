# 0011. Search uses portable LIKE queries, not a full-text index, and is open to the team

- **Status:** Accepted
- **Recorded:** 2026-10-26

## Context

Analysts need to find past coverage quickly: "have we seen this IP before?", "what did we say about that actor?",
"which action was the firewall patch?". TIDE runs on SQLite in development and SQL Server in production, installed by
people who aren't DBAs. A team's corpus is small: a few articles a day, so tens of thousands of rows after years.
Indicators are stored live but often written defanged in prose, and an indicator search has to be exact enough to
trust ("not seen" must mean not seen).

## Decision

- **LIKE, not full-text.** Every term is matched with `LIKE '%term%'` across an entity's fields, wildcards escaped.
  It behaves the same on both databases, needs no full-text catalog or extra install step, and is fast enough at a
  team's scale. Every term must match (AND across terms, OR across fields); there's no ranking beyond putting title
  matches first and newest next.
- **Indicators are matched exactly.** When the query is one indicator, the lookup matches the stored live value in its
  type family (an account and an email address are one identity), case-insensitively. Related indicators (the same
  host) are listed apart and labelled, never mixed in as if exact.
- **Both forms of a network indicator.** A term that reads as an IP address, domain, URL or email address is looked for
  live and defanged, so "evil[.]com" finds an indicator stored as "evil.com" and prose that wrote it defanged.
- **Open to the team, not Readers.** Search shows every TLP, as the article pages do, so it needs the team-edition
  permission. Readers keep the published archive, which holds only what was sent to them.

## Consequences

- No stemming or relevance scoring: "patching" doesn't find "patch". If a corpus outgrows this, a full-text index
  can replace the matching inside `SearchService` without changing the page.
- Leading-wildcard LIKE scans the tables it searches; at a team's scale that's milliseconds, and hits are capped at 50
  per kind.
