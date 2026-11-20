# 0029. The layout check runs in slices side by side, and waits for the page to be still

- **Status:** Accepted
- **Recorded:** 2026-11-12
- **Accepted:** 2026-11-12, in #212 and #215.

## Context

`tools/screenshots/layout.mjs` checks the layout in a real browser on every pull request. It had grown to 586 page
widths, about 28 minutes one after another, and it paused for fixed times after every load, click and key (around 110
pauses of 25ms to 1.5s), which was both slow and, on a slow runner, not always long enough.

## Decision

- **Slices.** `SHARD=i/n` runs one of n slices of the pages, in order, each on its own fresh demo; CI runs four side by
  side and one `layout` check is green only when every slice is. Pages that change the demo for those after them are
  marked (`CHANGES_THE_DEMO`) and stay together at the end of the last slice.
- **Waits.** After a load, click or key the scripts wait with `idle(page)` (`tide.mjs`): Blazor's circuit is up and has
  drawn, the server has no event in hand (the Development-only `/demo/activity`), and nothing in the page has changed
  for 200ms. A fixed delay is kept only for a timer of the app's own (a search's pause after typing), saying whose.
- The screenshot tool (`capture.mjs`) and the baseline tool wait the same way.

## Consequences

- A new page that changes the demo goes after the marker; any other before it.
- `/demo/activity` exists only in Development with `Demo:Seed`, so it never reaches an installed site.

## Follow-up

- **2026-11-19: slices balanced by measured time.** Cutting the pages in order by a guess at their cost (a width, twice
  that on a Present page) left the slices far apart: in CI the third took about 9.5 minutes of checks and the first about
  4. The pages before the marker are now placed longest first, each on the slice with least so far, by what each took in
  CI (`tools/screenshots/layout-weights.json`, refreshed from green runs' logs with `layout-weights.mjs`; a page not yet
  timed is counted by its widths). Every sharded run checks that the slices hold every page exactly once. By the weights,
  the slices went from about 246, 317, 556 and 432 seconds to 373, 373, 373 and 432: the last slice, the pages that change
  the demo, is now the longest, and a fifth slice wouldn't shorten it. Splitting those pages is what would help next.
