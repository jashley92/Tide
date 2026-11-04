# 0001. Blazor Server, hosted on-premises in IIS

- **Status:** Accepted
- **Recorded:** 2026-10-25

## Context

Threat-intel teams handle content under TLP and vendor licences (ISAC reports, CrowdStrike and Rapid7 portals) that
often may not leave the organization. Most already run Windows Server, IIS and SQL Server, and sign in with Active
Directory.

## Decision

TIDE is an ASP.NET Core 10 Blazor Web App with interactive server rendering throughout. It runs in-process under IIS
with Windows (Negotiate) authentication and keeps its data in SQL Server.

## Consequences

- There's no separate API or SPA build to deploy. The UI runs on the server over a SignalR circuit, so data never
  sits in the browser beyond what's on screen.
- A circuit holds one DI scope, so services open a short-lived database context per operation
  (`IAppDbContextFactory`).
- An app-pool recycle drops open circuits. The installer turns off scheduled recycling, and Data Protection keys
  persist so sign-ins survive a restart.
- TIDE gets a site and host name of its own by default. Where a host name is hard to get, it can optionally run as an
  IIS application under another site's path instead (`/tide`, still in its own pool, folder and database). Nothing in
  the app assumes the root: `<base href>` follows the request's path base. The cost is one origin shared with that
  site, so the option is documented as trusting the other app as much as TIDE.
