# 0028. Pull all pulls one source at a time, and stops with its page

- **Status:** Accepted
- **Recorded:** 2026-11-12
- **Accepted:** 2026-11-12, in #214. Builds on [0009](0009-intake-reaches-out-only-when-asked.md).

## Context

When the morning's scheduled polls haven't run (a server restarted, polling off overnight), an analyst wants every
feed fresh before triage. Pulling each source by hand is slow; pulling them all at once would hit many publishers and
the outbound proxy together, and could leave several pulls half done if something failed.

## Decision

- **Pull all** (Triage's Sources tab, and Today's word on feeds not pulled since `Intake:PulledBy`) pulls every polled
  source outside the pull-now window, by name, **one at a time**, each through Pull now's own checks and guard.
- One run at a time, whoever starts it (`SourcePollGuard.TryStartAll`); a second is refused.
- The run belongs to the page that started it: leaving the page or losing the connection stops it after the source
  under way. A poll is never cut short.

## Consequences

- A long list of sources takes as long as its pulls added up, and the page says which it's on.
- Sources a stopped run didn't reach stay as they were; a second Pull all takes them.
- Nothing pulls feeds nobody is waiting for.
