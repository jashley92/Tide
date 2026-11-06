# TIDE documentation

Start with the question you have.

## Where do I go to learn…

| I want to know… | Read |
|---|---|
| What TIDE is, who uses it, and what it deliberately doesn't do | [product/overview.md](product/overview.md) |
| What the screens look like and what each control does | [product/screens.md](product/screens.md) |
| What a term means (edition, ceiling, carry-over, outreach…) | [reference/glossary.md](reference/glossary.md) |
| How the system is put together | [architecture/overview.md](architecture/overview.md) |
| Where a piece of code lives | [architecture/codebase-map.md](architecture/codebase-map.md) |
| What's stored, in which table, and how it relates (the schema) | [architecture/data-model.md](architecture/data-model.md) |
| How TLP decides what each audience gets | [architecture/editions-and-tlp.md](architecture/editions-and-tlp.md) |
| Who can see and do what, and where it's enforced | [architecture/security.md](architecture/security.md) |
| How analysts see each other's changes live, and the meeting | [architecture/collaboration.md](architecture/collaboration.md) |
| How the UI is structured, the routes, the JS modules | [architecture/frontend.md](architecture/frontend.md) |
| Services, HTTP endpoints, background jobs, outbound calls | [architecture/backend.md](architecture/backend.md) |
| How to call the API, or receive webhooks | [API.md](API.md) |
| What happens when a user does X | [workflows/](workflows/README.md) |
| Which rules I must not break, and why | [reference/business-rules.md](reference/business-rules.md) |
| Why something was built the way it was | [decisions/](decisions/README.md) |
| What's fragile, limited or missing | [reference/known-issues.md](reference/known-issues.md) |
| Performance limits and bounds | [reference/performance.md](reference/performance.md) |
| How to get it running on my machine | [development/getting-started.md](development/getting-started.md) |
| How to add a field, page, permission, setting, API endpoint… | [development/recipes.md](development/recipes.md) |
| How tests and CI work, and what isn't covered | [development/testing.md](development/testing.md) |
| How to install a production instance | [INSTALL.md](INSTALL.md) |
| How to upgrade one | [UPGRADE.md](UPGRADE.md) |
| Every configuration setting, and which are changed in the app | [operations/configuration.md](operations/configuration.md) |
| How each external system is connected | [operations/integrations.md](operations/integrations.md) |
| Backups, restores, and what runs when | [operations/backup-and-recovery.md](operations/backup-and-recovery.md) |
| What to do when something breaks | [operations/troubleshooting.md](operations/troubleshooting.md) |
| The deploy scripts | [../deploy/README.md](../deploy/README.md) |
| How to keep these docs current | [MAINTAINING-DOCS.md](MAINTAINING-DOCS.md) |

## Reading paths

- **New developer:** [product/overview.md](product/overview.md) → [product/screens.md](product/screens.md) →
  [architecture/overview.md](architecture/overview.md) → [development/getting-started.md](development/getting-started.md)
  → [architecture/editions-and-tlp.md](architecture/editions-and-tlp.md) →
  [reference/business-rules.md](reference/business-rules.md) → [architecture/codebase-map.md](architecture/codebase-map.md).
- **Operator / system administrator:** [architecture/overview.md](architecture/overview.md#runtime-topology-production)
  → [INSTALL.md](INSTALL.md) → [operations/configuration.md](operations/configuration.md) →
  [operations/backup-and-recovery.md](operations/backup-and-recovery.md) → [UPGRADE.md](UPGRADE.md) →
  [operations/troubleshooting.md](operations/troubleshooting.md).
- **Security reviewer:** [architecture/security.md](architecture/security.md) →
  [architecture/editions-and-tlp.md](architecture/editions-and-tlp.md) →
  [operations/integrations.md](operations/integrations.md) → [reference/known-issues.md](reference/known-issues.md).
- **Integrator (SOAR, Power BI, SIEM, detection pipeline):** [API.md](API.md) →
  [architecture/editions-and-tlp.md](architecture/editions-and-tlp.md) →
  [workflows/actions-and-hunting.md](workflows/actions-and-hunting.md).
- **Analyst or team lead using the app:** [product/screens.md](product/screens.md) → [workflows/](workflows/README.md).

## Layout

```
docs/
  README.md               this index
  product/                what it is; the screens
  architecture/           how it's built: overview, code map, data model, editions and TLP, security,
                          collaboration, frontend, backend
  workflows/              what users do and what happens underneath
  development/            getting started, recipes, testing
  operations/             configuration, integrations, backup and recovery, troubleshooting
  reference/              business rules, glossary, known issues, performance
  decisions/              architecture decision records
  screenshots/            generated images (tools/screenshots/capture.mjs; see MAINTAINING-DOCS.md)
  API.md, INSTALL.md, UPGRADE.md
                          kept at these paths because the app, the deploy scripts and tests link to them
  MAINTAINING-DOCS.md     how to keep this current
```

Code comments sometimes refer to internal planning items by tag (`EP-84`, `ADR 0019`…). ADRs are in
[decisions/](decisions/README.md); the other tags are history and you don't need them to understand the code.
