# 0025. An action beside the slide is a setting, off by default

- **Status:** Accepted
- **Recorded:** 2026-11-12
- **Accepted:** 2026-11-09, by the owner: in scope, behind a setting, built last and turned on after a live rehearsal.
  Built in #209.

## Context

Opening an action next to the slide lets the room read the two together. But five cards share the overlay, followers
mirror what the presenter has open, and the shared scroll depends on the slide's geometry. A change that narrows the
slide for everyone is the riskiest in present mode, and how it looks on a meeting room's screen can't be judged from a
desk.

## Decision

- `Org:ActionBesideSlide` (Organization settings, *In a meeting, an action opens beside the slide*) is **off by
  default**. Off, an action opens over the slide as the other cards do.
- On, from 1200px wide the action is a column between the slide and the presenter's rail, the same width for the
  presenter and everyone following, so their slides fit and scroll alike. Narrower, it opens over the slide.
- Only the action does; a product, the indicators, the CVEs and the tags still open over the slide. A saved change
  counts at once on every page in the meeting.

## Consequences

- An installation turns it on after trying it in its own meeting room. Upgrading changes nothing until then.
