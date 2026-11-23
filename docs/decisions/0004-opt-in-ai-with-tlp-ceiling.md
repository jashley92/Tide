# 0004. AI help is opt-in, capped by TLP, and always accepted by a person

- **Status:** Accepted. Built in November 2026 (EP-70 Option A): drafting help in the article editor.
- **Recorded:** 2026-10-25

## Context

Drafting bullets, suggesting ATT&CK techniques and scorecard tags, and pulling out indicators are where an LLM saves
an analyst the most time. But much of what TIDE holds is TLP:AMBER or licensed vendor content, which may only go to
a model the organization has approved, if at all.

## Decision

- **Pluggable, off by default.** An `IThreatIntelAssistant` sits behind the `Ai:Provider` setting. Azure OpenAI is
  the first implementation. With no provider set, nothing is ever sent.
- **TLP ceiling.** `Ai:MaxTlp`, GREEN by default, caps what's sent: content above it is never sent, and the button
  says why.
- **Validated suggestions.** Every suggestion is checked before it's shown. Techniques must exist in the ATT&CK
  catalog, tags must be configured ones, and indicators are cross-checked by the deterministic extractor. The analyst
  then accepts or rejects each one.
- **Recorded.** Every suggestion is stored with its provider, model, prompt version and the input's hash and TLP.
- **Bring your own.** For deployments with no approved API: copy a generated prompt, run it in an approved tool,
  and paste the JSON back. It goes through the same validation.

## As built (November 2026)

- **The provider.** `IThreatIntelAssistant`, with Azure OpenAI's v1 Responses API as the first implementation. A draft
  is one request with a strict JSON schema and no tools, so it can't reach the web; `store: false`.
- **The text to draft from.** TIDE keeps only a feed item's summary, so the editor gets the article's text in one of
  three ways:
  - **Pasted:** always available.
  - **Fetched by TIDE:** through intake's guarded client, the body found by `ArticleText` (a source's selector, the
    page's published `articleBody`, its `<article>` markup, then reader-mode scoring). Each source says whether its
    articles may be fetched: TIDE only, Open, or Never.
  - **Looked up by the provider:** only when TIDE's fetch fails, the source is Open, the article is TLP:CLEAR and
    `Ai:WebGrounding` is on. It's a separate request with only the web search tool, limited to the article's site, and
    sent only the address and title, so a hostile page never shares a request with our notes. Its text is used only
    when the provider cites that page. Azure has no direct page fetch, so this is a Bing-grounded search.
- **Our notes are AMBER.** Our stack's notes go with a draft only when `Ai:MaxTlp` allows AMBER, and the draft then
  counts as AMBER. Without them, no "why it matters" is suggested.
- **The checks** (`DraftChecks`) also reduce Markdown links to their text, so a page can't plant a link in a brief.
  Indicators are marked as in the text or not, and those that aren't are never added in bulk.
- **Cited sources (prompt v2, EP-77).** A draft can be made from several reports (an article's other links, their text
  read in triage or fetched). Each is fenced and numbered, the feed summary last, within a total of 60,000 characters
  shared equally. Every bullet must name its sources and quote a phrase from the first; `DraftChecks` keeps only source
  numbers that exist and looks for the quote in the cited text, so an unsupported or misquoted claim is flagged.
  Disagreements between sources are returned separately, never resolved by the model. The draft goes at the highest
  TLP among the sources' feeds.
- **The record.** Each draft is stored in `AiDrafts` with its provider, model, prompt version, input hash and TLP,
  what it was based on, and how many suggestions were offered, accepted and rejected. The source text isn't kept.

## Amendment (November 2026): the report's assessments and the title slide line

AI help now covers two more pieces of prose written on a schedule. Everything above holds for them: off without a
provider (and *Copy a prompt* then works as in the editor), capped by TLP, checked, recorded, and used only when a person
chooses to.

- **The uses.**
  - *Draft an assessment*, beside each section's assessment on Reports. It's drafted from the section's numbers as the
    report shows them (with the period before and the change, so the model needn't work one out), and for Headlines and
    Topics and techniques the titles and bottom lines of the articles counted in the period, newest 40.
  - *Suggest a title slide line*, in the brief's meeting card where the line is edited. It's drafted from the titles and
    bottom lines of the brief's articles. A used line takes the highest TLP among them.
- **The cut.** The articles are read at the lower of `Ai:MaxTlp` and the team edition's ceiling, through the same cut
  the editions use (`BriefEditions`), so nothing above either is read. Only titles and bottom lines go, never bullets,
  "why it matters" or our notes. The report's counts are the team's own aggregate numbers, not anyone's content, and go
  without a TLP of their own; the draft's recorded `InputTlp` is the highest among the articles sent.
- **The numbers check.** `ReportDraftChecks.Numbers` flags every number the answer states that the material doesn't
  (compared by value, so "1,204" matches "1204"). The draft is still shown, with the flagged numbers named, for the person
  to check before using it. A pasted answer goes through the same checks.
- **Who may draft.** Whoever may write the text: a report's assessments, `ReportService.CanAssess` (`EnterMetrics` or
  `WriteArticles`); the title slide line, `WriteArticles`, until the brief is sent. Neither is widened.
- **Nothing is saved by the draft.** *Use it* fills the box as an unsaved edit; the person edits it and saves as before.
- **The record.** `AiDrafts.Kind` says what a draft was for (`Article`, `ReportAssessment`, `TitleLine`); `ArticleId` is
  null for the new kinds, and `Subject` holds the report section (`2026-10/headlines`) or the brief's id. Each kind's
  prompt has its own version (`ProsePrompt`).

## Consequences

The deterministic helpers (the indicator finder, the CVE parser, the readiness checks) must stay good on their own,
since many installs will never turn AI on.
