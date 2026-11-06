# Maintaining the documentation

The docs describe what the code does today. They stay useful only while that's true, so they change in the same
pull request as the code.

## The rule

> If a change alters how another developer, operator, integrator or analyst would **understand, configure,
> troubleshoot, deploy, call or use** TIDE, the relevant documentation is updated **in the same change**.

A pull request that changes behavior without touching the docs should say why no doc applies ("internal refactor,
no behavior change").

## What to update when

| You changed… | Update |
|---|---|
| An entity, column, index or enum (any migration) | [architecture/data-model.md](architecture/data-model.md) (table, ERD, enum), and [UPGRADE.md](UPGRADE.md) if the upgrade has a visible effect (data moved, rows added or dropped) |
| What an edition, export, webhook, API response or outreach email may carry | [architecture/editions-and-tlp.md](architecture/editions-and-tlp.md), [reference/business-rules.md](reference/business-rules.md), and a leak test |
| A rule in a service (what's allowed, what's refused) | [reference/business-rules.md](reference/business-rules.md) and the workflow page that covers it |
| A page, tab, dialog or route | [architecture/frontend.md](architecture/frontend.md) (route table), the matching [workflows/](workflows/README.md) page, [product/screens.md](product/screens.md), and the screenshot manifest (below) |
| A permission, role or capability | [architecture/security.md](architecture/security.md) (matrix), the role table in [product/overview.md](product/overview.md), and the "What each role can do" table in [INSTALL.md](INSTALL.md) |
| A service's responsibilities | [architecture/backend.md](architecture/backend.md) (service catalog) and [architecture/codebase-map.md](architecture/codebase-map.md) |
| An HTTP endpoint (download, export, CSV, health) | [architecture/backend.md](architecture/backend.md) endpoint table |
| Anything under `/api/v1`, a scope, or a webhook event or payload | [API.md](API.md), and the OpenAPI document in `src/Tide.Web/Api/OpenApi.cs` |
| A `ChangeFeed` topic, presence or live-meeting behavior | [architecture/collaboration.md](architecture/collaboration.md) |
| A configuration key (`appsettings.json`, an `*Options` class, `SettingsCatalog`) | [operations/configuration.md](operations/configuration.md), the key's `"//"` line in `appsettings.json`, `deploy/appsettings.Production.template.json` if production needs it, and [UPGRADE.md](UPGRADE.md) if existing installs must add it |
| A background service, its cadence or what it touches | [architecture/backend.md](architecture/backend.md#background-jobs) and [operations/backup-and-recovery.md](operations/backup-and-recovery.md) (what runs when) |
| An external system, or what data goes to one | [operations/integrations.md](operations/integrations.md) |
| A deploy script or its parameters | The script's comment help, `deploy/README.md`, [INSTALL.md](INSTALL.md) / [UPGRADE.md](UPGRADE.md), and every command line that calls it (search `docs/` for the script's name) |
| CI, test projects or the test harness | [development/testing.md](development/testing.md) |
| A failure mode you diagnosed | A *Symptom → Cause → Where to look → Fix* entry in [operations/troubleshooting.md](operations/troubleshooting.md) |
| A deliberate design choice someone might reverse | A new record in [decisions/](decisions/README.md) |
| A known limit, workaround or piece of debt (found or fixed) | [reference/known-issues.md](reference/known-issues.md) |
| A new domain word | [reference/glossary.md](reference/glossary.md) |

## Screenshots

The images in `docs/screenshots/` are meant to be generated, not hand-edited. A capture tool
(`tools/screenshots/capture.mjs`, Playwright, against a seeded dev database) is being added. Once it's in, its
`README.md` says how to run all shots or a subset (`ONLY=name1,name2`). Numbered callouts are defined in the
tool's manifest, and the doc that embeds an image holds the legend: renumber a callout and you update the legend
too. Refresh the affected shots after any visible UI change, and review the PNG diff before committing.

## Diagrams

Diagrams are Mermaid blocks inside the Markdown, so they're edited as text and render on GitHub. Keep them at the
level of the page they're on (no class-level detail in the architecture overview), and keep each to about 25 nodes;
split one that grows. When you rename a class, table or route that a diagram names, search the docs for it.

Mermaid on GitHub is strict. Quote labels that hold punctuation (`A["Send (frozen)"]`), use `<br/>` for line
breaks, and don't put parentheses or semicolons in `erDiagram` attribute comments.

## Style

- Describe what exists. Planned work goes in issues, not here. If behavior differs from what was intended, describe
  the behavior and note the difference.
- Cite code as `src/Project/Folder/File.cs` and a member name (`BriefEditions.Build`), not line numbers, which rot.
- Mark anything you couldn't confirm as **Needs verification:**, with what would confirm it.
- Plain, direct sentences, present tense, sentence-case headings, as the UI's own copy is written.
- Don't duplicate. Link to the one place a fact lives; the [index](README.md) says where that is.

## Periodic review

Before each release tag, skim the [index](README.md) questions and check that each still leads to a correct answer.
In particular:

1. Regenerate the screenshots and recapture anything that changed.
2. Check [operations/configuration.md](operations/configuration.md) against `appsettings.json`, the production
   template and `SettingsCatalog`.
3. Check the route table against the `@page` directives (`git grep -n "@page" src/Tide.Web`).
4. Check [API.md](API.md) against `/api/v1/openapi.json` from a running instance.
5. Check the permission matrix in [architecture/security.md](architecture/security.md) against
   `src/Tide.Application/Security`.
6. Move fixed items out of [reference/known-issues.md](reference/known-issues.md).
