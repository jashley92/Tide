# Screenshot capture

`capture.mjs` produces every image in `docs/screenshots/`. It seeds a demo database with fictional data, starts TIDE in
Development on it, signs in with the dev sign-in, drives each page with Playwright and writes `docs/screenshots/<name>.png`.
Rerun it whenever the UI changes. It takes about a minute.

## Prerequisites

- The .NET SDK the solution builds with.
- Node.js 20 or later.
- Once, from this folder: `npm install`, then `npx playwright install chromium`.

## Run it

From the repository root:

```bash
node tools/screenshots/capture.mjs
```

What happens:

1. `tools/screenshots/.demo/` is deleted and recreated. It holds the demo database, files, sent emails and `tide.log`, and
   is git-ignored. Your own dev database in `src/Tide.Web/App_Data` isn't touched.
2. `src/Tide.Web` is built, then started on `http://localhost:5112` with `Demo:Seed=true`. That switch only works in
   Development. With it, `DemoSeeder` (`src/Tide.Web/Services/DemoSeeder.cs`) adds its data on top of the app's usual dev
   samples:
   - six months of briefed articles with scorecard tags, so Trends has history
   - a year of opening metrics
   - actions in every state, one with a hunt query and one with a checklist and a comment
   - three adversaries, extra sources, and triage items with their text kept for the reading pane
   - three API tokens
   - the previous working day's brief, presented and sent in both editions through the app's own send

   The seeder writes `.demo/demo.json`, which names the days and records that routes refer to.
3. Each shot in the manifest is captured, and TIDE is stopped.

Options, as environment variables:

| Variable | Effect |
| --- | --- |
| `ONLY=name1,name2` | Capture only these shots. |
| `REUSE=1` | Keep the demo database from the last run. The seed runs once per database. |
| `PORT=5113` | Use a different port. On Windows, avoid the ranges `netsh interface ipv4 show excludedportrange protocol=tcp` lists. |
| `BASE_URL=http://localhost:5112` | Use a TIDE that is already running on the demo database. It isn't built or started. |

The run exits non-zero if any shot fails. A shot fails when its page errors, or when a callout's selector matches nothing.
In that case it prints `annotation target(s) not found` with the selectors, and that shot's image is left as it was.

## Keeping images stable

Every shot uses the same settings:

- a fixed viewport and a device scale of 1
- the light theme and an expanded sidebar
- reduced motion, with animations off and the caret hidden
- en-US, America/New_York
- the browser's clock frozen at the start of the run

The seed draws its random choices from a fixed seed, so two runs on the same day give the same data.

The server's clock isn't faked. "Today" is the real day, so dates and times in the images follow the day you run the tool.
Two runs on the same day differ only in the minutes shown on timestamps.

## Add a shot

Add an entry to the `shots` array in `capture.mjs`:

```js
{ name: 'doc-stories', route: '/stories', viewport: { width: 1440, height: 1000 },
  setup: async page => { await page.getByRole('button', { name: 'Active' }).click(); },
  clip: '.tide-card',
  annotate: [['.tide-tabbar-page', 1, 'l'], ['.tide-card h2', 2]] },
```

| Field | Meaning |
| --- | --- |
| `name` | The file name, `docs/screenshots/<name>.png`. Shots made for the docs start with `doc-`. |
| `route` | The page. `{today}`, `{sent}`, `{huntAction}`, `{detailAction}`, `{vpnArticle}` and `{sentArticle}` are filled from `demo.json`. |
| `viewport` | `{ width, height }`. Add `fullPage: true` to capture the whole page at that width. |
| `setup` | Optional. `async (page, demo) => {}` runs after the page settles: clicks, keys, scrolling. `scrollTo(selector, top)` scrolls a match to `top` px from the top of the window. |
| `clip` | Optional. A selector: the image is cut to the box around its matches, plus `pad` pixels (default 16). |
| `annotate` | Optional. Numbered callouts, described below. |

If the shot needs data the seed doesn't have, add it in `DemoSeeder`. If a route needs a new id, add it to
`DemoSeeder.Seeded`.

## Callouts

`annotate: [[selector, n], [selector, n, corner], ...]` puts a numbered violet circle on each element. The docs then
explain each number in a legend under the image.

- `selector` is a Playwright selector. That means CSS, plus extensions such as `:has-text("…")` and `:text-is("…")`.
  The first visible match is used. Prefer the app's `tide-*` classes and visible text over position.
- `n` is the number shown in the circle.
- `corner` says where the circle goes:
  - `tl` is the top-left corner (the default). `tr`, `bl` and `br` are the other corners.
  - `l` is just outside the left edge, at mid-height, and `r` is just outside the right edge.
  - Use `l` for wide elements, so the circle sits in the margin instead of on the text.

The circles are drawn into the page just before the capture, so they sit over the UI exactly where the element is. A
selector that matches nothing fails the run. A callout can't silently disappear when the UI changes.

## Layout checks

`layout.mjs` uses the same demo database and server (`tide.mjs` starts both, for either script) to check the layout in a
real browser, which the HTML tests can't: v1.10.0's meeting quick add had all five controls in its markup, stacked one
per line. CI runs it on every pull request (the `layout` job).

```bash
node tools/screenshots/layout.mjs
```

`ONLY`, `REUSE`, `PORT` and `BASE_URL` work as above. Each page in its `pages` list has a `route`, an optional `setup`,
the `widths` to check at (900px tall) and its `rules`:

| Rule | Holds when |
| --- | --- |
| `oneRow(selector)` | Every match lays its visible children out on one line. |
| `lineUp(selectorA, selectorB, ...)` | The first visible match of each starts at the same x, within 2px. |
| `fits(selector)` | Nothing inside a match spills out of it sideways. |
| `noSidewaysScroll` | The page isn't wider than the window. |
| `visible(selector)` | At least one match is shown. |

`[minWidth, rule]` applies a rule only at that width or wider (the slide's rows wrap below 1001px, as designed). A
failure names the page, the width and the rule: `slide-article at 1440px: .tide-quick-add #1: its 5 parts are on more
than one line (135px tall)`. When you add a screen with a row or a column that has to hold, add its rule here.
