# 0006. Hunt queries are generated text; TIDE never signs in to a SIEM

- **Status:** Accepted (implemented in M3)
- **Recorded:** 2026-10-25

## Context

A threat's first action is often "check our logs for these indicators". Teams run different SIEMs: Cortex XSIAM,
Microsoft Sentinel or Defender, and CrowdStrike are common. Each environment names its datasets and fields
differently, so no built-in query fits every install.

## Decision

- **Dialects.** An `IQueryDialect` per query language (XQL, KQL, FQL to start) builds a query from an article's
  indicators.
- **Templates.** Each dialect works from admin-editable templates per indicator type, with placeholders for the
  values and for the environment's dataset and field names. A new dialect (SPL, LEQL) is a class plus its default
  templates.
- **The story's indicators, a hand-written query, and a window.** (Added October 2026.) A hunt can be built from every
  article in the action's article's story, since a later report often has the indicators the first lacked. An
  analyst can also add a query they wrote, such as a behavioural one; it's kept and recorded like a generated one,
  marked hand-written. `{{days}}` in a template is how far back to look: a week before the earliest article hunted
  from, at least 30 days and at most 180. The KQL defaults use it.
- **Saved queries and an action's own indicators.** (Added November 2026.) The team keeps hunt queries to reuse: a
  tuned sweep, or a behavioural hunt with no indicators. Anyone who works actions keeps the library. A saved query may
  use `{{days}}`, and `{{values}}` (or one kind's, `{{values:sha256}}`), filled from the action when it's added there,
  so it is still generated text with nothing run. An action with no article can carry indicators of its own for this;
  one raised from an article adds them to the article, so every edition and export sees the same indicators.
- **No SIEM credentials.** The analyst copies the query, runs it in their SIEM, and records the result (hits or no
  hits, a count, notes, a screenshot) on the action. TIDE holds no SIEM credentials and makes no SIEM API calls in
  v1.

## Consequences

Running a hunt is a copy and paste. In exchange there's no credential to protect and nothing to break when a SIEM
API changes, and every open-source user can adapt the templates to their own schema.
