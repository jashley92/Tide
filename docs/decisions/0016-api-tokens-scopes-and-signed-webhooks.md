# 0016. The API: scoped, expiring tokens through the pages' own services, and signed webhooks

- **Status:** Accepted
- **Recorded:** 2026-11-02
- **Accepted:** 2026-11-02. The owner approved EP-75 Option A (reads, narrow writes and webhooks) and its tables.

## Context

Work raised in TIDE is done in other tools (Sentinel, XSOAR, ServiceNow), and someone has to come back and close the
action by hand; leadership numbers leave as CSV. TIDE had file and CSV endpoints for a signed-in person, and nothing a
machine could drive. Every new way in is attack surface, and TLP has to hold exactly as it does on the pages.

## Decision

- **One versioned API, `/api/v1`, described by a hand-kept OpenAPI document** at `/api/v1/openapi.json`. Reads: a brief,
  actions, Our stack, metrics. Writes, each narrow: an action's status and a note; a day's metric value; a proposed
  triage item (an analyst still decides it).
- **Every endpoint goes through the service the pages use**, so the same permission checks and the same audit apply. A
  brief is cut by `BriefEditions.Build`, the team edition's code, at the lower of the team edition's ceiling and the
  caller's. Our stack's notes (internal, AMBER) go only to a caller whose ceiling allows AMBER.
- **Two ways to sign in.** A person or a server's gMSA signs in with Windows as anywhere else, held to its roles. Or an
  API token, `Authorization: Bearer tide_pat_…`: issued by an administrator under Administration > API access, shown
  once, stored only as its SHA-256, with an expiry (at most 366 days), a TLP ceiling (GREEN by default) and scopes. A
  bearer header picks the token scheme; the AD-group transformer never gives a token a role.
- **Scopes are checked at the endpoint, permissions in the service.** Each scope grants the permissions its endpoints'
  services check (`actions:write` works an action as an analyst would), but a token reaches nothing outside `/api/v1`,
  and each endpoint refuses a token without its scope (403, naming it). The scopes: `briefs:read`, `actions:read`,
  `actions:write`, `metrics:read`, `metrics:write`, `stack:read`, `triage:propose`, and (EP-79, ADR 0017) `detections:write`.
- **Every call is counted on its token** (calls and last used), and every write is in the audit log under the token
  (`api:<id>`) and its caller's name. Calls are rate-limited per caller (120 a minute).
- **Webhooks are polled from the database, not raised by the services.** Once a minute the job posts, per webhook, the
  actions raised and briefs sent since its last successful run, and once a day the actions that became overdue. A
  failed post stops that webhook's run and is tried again next time, so nothing is lost and nothing in the services
  changed. Each post is signed: `X-Tide-Signature: sha256=` the HMAC-SHA256, with the webhook's secret, of the
  `X-Tide-Timestamp` value, a dot and the body. Each event has a stable id (`id` in the body, `X-Tide-Event-Id`),
  the same when it's posted again, so a receiver drops repeats. The secret is shown once and kept encrypted with the server's data
  protection keys. Addresses must be https; redirects aren't followed. A webhook is held to the team edition's
  ceiling, as its receiver (a SOAR, a ticket queue) is at least as wide as that edition's list: an action raised from
  an article above it is never posted, and each action posted carries its article's TLP as `marking` (null when it
  has no article), as the reminder emails mark their lists.

## Consequences

- v1 is a promise: a field or endpoint can be added, never changed or removed, without a v2.
- Losing the data protection keys makes webhook secrets unreadable; the page says so, and the webhook is added again.
- A proposed triage item comes in under a source named after its caller ("Sentinel automation (API)"), of kind Api,
  which is never polled.
- Not done: per-team limits on a token, and a read of articles outside a brief. Both can be added within v1.
