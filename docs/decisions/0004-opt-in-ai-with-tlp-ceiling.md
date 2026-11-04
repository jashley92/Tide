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

## Consequences

The deterministic helpers (the indicator finder, the CVE parser, the readiness checks) must stay good on their own,
since many installs will never turn AI on.
