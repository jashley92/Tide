# 0017. Detection rules are referenced, never run, and "covered" means tagged

- **Status:** Accepted
- **Recorded:** 2026-11-02
- **Accepted:** 2026-11-02. The owner approved EP-79 Option A (the Coverage view with actions) and its tables.

## Context

Articles and adversary profiles carry ATT&CK techniques, but TIDE couldn't say whether the SOC has a detection for any
of them. The rules live in the SIEM (Sentinel analytics rules), in a Sigma repository, or in a spreadsheet. Signing in
to a SIEM is something TIDE has avoided (ADR 0006), and a rule's ATT&CK tag is the only machine-readable statement of
what it's for.

## Decision

- **TIDE keeps a reference to each rule, not the rule.** An import reads a Sentinel analytics-rules export (ARM
  template JSON), Sigma YAML (one file or a zip of a folder) or a CSV, and keeps each rule's id in its source, name,
  technique tags, platform and enabled state. Queries are never read, stored or run. Another tool can push its full set
  through the API with the `detections:write` scope; each push replaces the caller's last one.
- **Each import refreshes its source.** Known rules (by id) are updated, new ones added; a rule missing from the latest
  import of its source is kept, marked "not in the last import", and stops counting.
- **"Covered" means a current, enabled rule is tagged with the technique**, its parent or one of its sub-techniques.
  Every page says "tagged", because a tag isn't proof a rule fires on that behaviour.
- **Coverage lists what was briefed with no rule**: techniques on articles in a brief in the last 90 days, the watched
  adversaries whose tagged articles show each, ranked gaps first. A gap becomes an ordinary action (kind Config change)
  for the team chosen, linked to the latest article, and the row shows it until it's closed.
- **Rules are internal.** Their names and counts appear only to analysts: in Coverage, on the editor's technique chips,
  on adversary profiles and in the Navigator layer's colours. They're never in an edition, an export or a webhook.

## Consequences

- The list goes stale unless it's re-imported or pushed; each page shows when each source was last read.
- Uneven tagging in the SOC's rules shows up as gaps that aren't, or coverage that isn't. The wording keeps that
  visible rather than hiding it.
- A zip upload is bounded (8 MB packed, 5,000 files, 1 MB a file, 32 MB unpacked, a 100:1 ratio) so it can't exhaust
  the server.
