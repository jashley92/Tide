# Decision records

Short records of decisions that shape TIDE, and why they were made. Add one when a choice would surprise a new
contributor; supersede one rather than editing it once it's accepted.

| # | Decision |
|---|---|
| [0001](0001-blazor-server-on-prem.md) | Blazor Server, hosted on-premises in IIS |
| [0002](0002-permissions-as-code.md) | Roles are fixed bundles of permission atoms; AD groups grant roles |
| [0003](0003-files-on-disk.md) | Screenshots live on disk, not in the database |
| [0004](0004-opt-in-ai-with-tlp-ceiling.md) | AI help is opt-in, capped by TLP, and always accepted by a person |
| [0005](0005-editions-from-one-model.md) | Every edition of a brief is cut from one model by TLP |
| [0006](0006-query-dialects-are-templates.md) | Hunt queries are generated text; TIDE never signs in to a SIEM |
| [0007](0007-frozen-sends-and-cid-email.md) | A send freezes what went; email embeds images; Readers see only what was published |
| [0008](0008-teams-own-actions.md) | Teams own actions; being on the team is what lets you work one |
| [0009](0009-intake-reaches-out-only-when-asked.md) | Intake reaches out only when asked, keeps one item per address, and never renders feed HTML |
| [0010](0010-rotation-changes-approved-by-the-colleague.md) | Brief rotation swaps and cover are approved by the colleague, and the manager is told |
| [0011](0011-search-with-portable-like-queries.md) | Search uses portable LIKE queries, not a full-text index, and is open to the team |
| [0012](0012-exports-carry-tlp-and-stable-ids.md) | Exports carry each article's TLP, leave out what's above the chosen ceiling, and use stable ids |
| [0013](0013-trends-count-articles-by-first-brief.md) | Trends count an article once, in the period it was first briefed, and every count opens to what it counted |
| [0014](0014-metric-values-from-csv.md) | Metric values can be read from CSV, by key and date; an import never overwrites a person's value or a sent day |
| [0015](0015-corrections-after-send.md) | A correction after a send is a short, frozen email of its own in a table of its own; a TLP slip is withdrawn, not recalled |
| [0016](0016-api-tokens-scopes-and-signed-webhooks.md) | The API: scoped, expiring tokens through the pages' own services, and signed webhooks |
| [0017](0017-detection-rules-are-referenced-and-tagged.md) | Detection rules are referenced, never run, and "covered" means tagged |
| [0018](0018-holidays-are-days-off-like-weekends.md) | Holidays are dated days off, kept by an administrator, and count as a weekend does |
