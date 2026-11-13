# 0024. The presenter's controls are in a rail only the presenter's page draws

- **Status:** Accepted
- **Recorded:** 2026-11-12
- **Accepted:** 2026-11-12, in #206.

## Context

Present mode shows the same slides to the presenter and to everyone following, on the shared screen and on their own
machines. The presenter also needs controls (Mark discussed, raise and take actions, hands, lock, end the meeting)
that the room shouldn't see, and that a follower mustn't be able to use.

## Decision

- What the meeting needs (slide content, actions, indicators, who is here, whose hand is up) is on the slide for
  everyone. What only the presenter does is in **the presenter's rail**, beside the slide from 1500px wide and over it
  below that, as the context rail is ([0023](0023-the-context-rail-docks-from-1500px.md)).
- The rail is drawn only for the page presenting the live meeting (or, with no meeting, someone who could present). A
  follower's page has none of its markup, an analyst included; a test asserts this on every slide.
- Each of the rail's steps checks again where it's handled, and the services check permissions behind that.
- Raising and working actions during the meeting is the presenter's; someone following uses the action pages.

## Consequences

- A follower's key press or forged event changes nothing.
- The slide's fit and shared scroll are worked out without the rail, so the room's slide is wider than the
  presenter's by the rail.
