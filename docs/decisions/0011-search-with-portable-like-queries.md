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
- Case is ignored for every letter on both databases, accents still count (SQL Server's default collation). SQLite's
  own LIKE folds A to Z only, so on SQLite `EF.Functions.Like` also runs a .NET matcher, but only for a term with a
  letter beyond ASCII (`SqliteLike`). Such a search scans several times slower there than an ASCII one (about 0.4 s
  against 0.08 s in the scale harness); an ASCII search runs as SQLite's own LIKE did.
- **Amended 2026-11-21: one searched text per article, and indexed indicator keys.** The decision stands (substring
  `LIKE`, every term, exact indicators); how it's run changed so its cost stays small as the corpus grows:
  - An article's searched parts are kept together in `ArticleSearch.Text`, one row per article, the parts apart by a
    control character no term holds, so a term still can't match across two parts. A search is one `LIKE` per spelling
    over that one column instead of seven columns and six child tables.
  - A save that changes a part marks the row in the same transaction (`SearchDirtyInterceptor`); a background job
    (Search index) rebuilds marked rows within seconds and, after an upgrade or a new builder version, every row.
  - Until an article's row is current, the search matches that article part by part as before, so an edit is found at
    once and nothing is missed while rows are built.
  - Bulk `ExecuteUpdate`/`ExecuteDelete` statements bypass the interceptor, so a test (`SearchIndexBypassTests`) lists each
    one on an article or a searched part with why it's safe.
  - Indicator lookups find the indicator itself by a hash of its value (`ValueKey`) and its host's other indicators by the
    host reversed by label (`HostRev`), both indexed; rows saved before these were kept are matched by value until a
    background job keys them.
  - The prose scan for an indicator query (a text search with its live and defanged forms) stays, through the same text.
  - SQL Server full-text search stays an optional extra, never a prerequisite: it would change the matching to words and
    needs its own ADR.

