# Screenshot capture

`capture.mjs` produces every image in `docs/screenshots/`. It seeds a demo database with fictional data, starts TIDE in
Development on it, signs in with the dev sign-in, drives each page with Playwright and writes `docs/screenshots/<name>.png`.
Rerun it whenever the UI changes. It takes about a minute.

## Prerequisites

- The .NET SDK the solution builds with.
- Node.js 20 or later.
- Once, from this folder: `npm ci --ignore-scripts`, then `./node_modules/.bin/playwright install chromium` (the locked
  Playwright, as CI runs it).

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
   - the dev sign-in on every team, since only a team's members take its actions on
   - three adversaries, extra sources, and triage items with their text kept for the reading pane
   - three API tokens
   - five products on Our stack: one tagged on the edge VPN article, one a triage item names, one not checked for over a
     year, one with no owning team and one never checked
   - a fourth metric group, Exposed services, with six weeks of numbers and one left to enter today
   - the brief before the previous one, with two actions from its article still open: one escalated, one overdue
   - Jordan, a second person, to follow a live meeting (see "Two people in a meeting")
   - the previous working day's brief, presented and sent in both editions through the app's own send

   The seeder writes `.demo/demo.json`, which names the days and records that routes refer to.

   `capture.mjs` also turns on `Demo:StaleFeeds`, with polling on and `Intake:PulledBy=08:00` (see "Layout checks"
   below), so Today says the feeds aren't pulled on a working day after 8:00 New York time, and the Sources tab has
   feeds to Pull all. Those feeds are never fetched.
3. Each shot in the manifest is captured, and TIDE is stopped.

Options, as environment variables:

| Variable | Effect |
| --- | --- |
| `ONLY=name1,name2` | Capture only these shots. |
| `REUSE=1` | Keep the demo database from the last run. The seed runs once per database, for the day it ran: TIDE won't start on one seeded for an earlier day, as its "today" has gone. |
| `PORT=5113` | Use a different port. On Windows, avoid the ranges `netsh interface ipv4 show excludedportrange protocol=tcp` lists. |
| `BASE_URL=http://localhost:5112` | Use a TIDE that is already running on the demo database. It isn't built or started. |
| `DEMO_DIR=path` | Keep the demo database, its files and `tide.log` there instead of `.demo`, so runs side by side don't share one. |
| `NO_BUILD=1` | Start the last build instead of building first, for runs side by side that one build served. |

The run exits non-zero if any shot fails. A shot fails when its page errors, or when a callout's selector matches nothing.
In that case it prints `annotation target(s) not found` with the selectors, and that shot's image is left as it was.

## Keeping images stable

Every shot uses the same settings:

- a fixed viewport and a device scale of 1: 1440×1000 for most, 1920×1080 where the context rail or the presenter's
  rail is meant to be docked open
- the light theme, an expanded sidebar, and the context rail as it starts (docked open in a wide window)
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
| `route` | The page. `{today}`, `{sent}`, `{huntAction}`, `{detailAction}`, `{vpnArticle}`, `{sentArticle}` and `{stackProduct}` are filled from `demo.json`. |
| `viewport` | `{ width, height }`. Add `fullPage: true` to capture the whole page at that width. |
| `setup` | Optional. `async (page, demo) => {}` runs after the page settles: clicks, keys, scrolling. `scrollTo(selector, top)` scrolls a match to `top` px from the top of the window. |
| `clip` | Optional. A selector: the image is cut to the box around its matches, plus `pad` pixels (default 16). |
| `annotate` | Optional. Numbered callouts, described below. |
| `when` | Optional. `demo => boolean`. When it's false the shot is skipped, saying so, and its image is left as it was: for what isn't shown on every day (Today's word on feeds not pulled, on a weekend). |
| `after` | Optional. `async (page, demo) => {}` puts back what `setup` changed, on a fresh page, even when the shot failed. `present-action-beside` turns on *In a meeting, an action opens beside the slide* and turns it off again here. |

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

It starts TIDE with a few more Development-only demo switches than `capture.mjs` does, so each rule has a case to
check:

| Setting | What it adds |
| --- | --- |
| `Demo:TriageFiller=55` | Enough more new triage items for a second page, for the paging keys. |
| `Demo:SlideFiller=true` | A slide with a dozen indicators and CVEs, a long hash and a long URL, and an opening note carried over. |
| `Demo:StaleFeeds=true` | Three enabled feeds on loopback addresses that are never fetched, last pulled the day before, so Today says the feeds aren't pulled (with `Intake:Enabled=true` and `Intake:PulledBy=00:00`). |
| `Org:ActionBesideSlide=true` | A meeting's action beside the slide; only the `present-beside` pages open one. |
| `Demo:WorkingDay=true` | On a weekend the app's clock is moved on to Monday, the time of day kept (`DemoWorkingDayClock`), so today's brief is today's and present mode's pages can start its meeting (a meeting isn't held before its brief's day). On a working day it changes nothing. The top bar is checked with a day off's brief chip as well (`dayOffChip`), whatever the day. |

A setup that can't be done (a page that won't load, a meeting that won't start) fails that page at once, with why, and
its other widths aren't tried: they would only wait out the same timeouts.

`ONLY`, `REUSE`, `PORT` and `BASE_URL` work as above. `SHARD=2/4` checks one of four slices of the pages, in order, on
its own fresh demo; CI runs the four side by side. The pages from `CHANGES_THE_DEMO` on change the demo for those after
them, so they stay together at the end of the last slice: put a new page that changes the demo there, and any other
before the marker. To run the slices side by side yourself, build once, then give each its own `PORT` and `DEMO_DIR`
with `NO_BUILD=1`.

After a load, a click or a key, wait with `idle(page)` (tide.mjs), not a fixed pause: it waits until Blazor's circuit
is up, the server has no event in hand (the Development-only `/demo/activity`, on with `Demo:Seed`), and nothing in the
page has changed for a moment. Wait out a timer of the app's own (a search's pause after typing) with that time, saying
whose it is, and wait for something that comes and goes (a drop hint) by its selector.

Each page in its `pages` list has a `route`, an optional `setup`,
the `widths` to check at (900px tall) and its `rules`. An optional `when: demo => …` skips the page, saying so, when what
it checks isn't shown on the day of the run (Today's word on feeds not pulled since the morning isn't, on a weekend or
holiday: `demo.FeedsDue` is false then).

| Rule | Holds when |
| --- | --- |
| `oneRow(selector)` | Every match lays its visible children out on one line. |
| `lineUp(selectorA, selectorB, ...)` | The first visible match of each starts at the same x, within 2px. |
| `fits(selector)` | Nothing inside a match spills out of it sideways. |
| `noSidewaysScroll` | The page isn't wider than the window. |
| `visible(selector)` | At least one match is shown. |
| `widerThan(selector, px)` | The first match is wider than `px` (a data page isn't held to the usual 1400px). |

A rule over every match (`oneRow`, `fits`, `onTitleLine`, `belowTopBar`) fails when nothing it matches is shown, so a
renamed class or a seed without the case can't pass it unseen. `hidden(selector)` is the one rule that holds of nothing.

`[minWidth, rule]` applies a rule only at that width or wider (the slide's rows wrap below 1001px, as designed). A
failure names the page, the width and the rule: `slide-article at 1440px: .tide-quick-add #1: its 5 parts are on more
than one line (135px tall)`. When you add a screen with a row or a column that has to hold, add its rule here.

## Before and after pictures

`baseline.mjs` uses the same demo database and server to take a picture of each main screen before a change to the UI,
and again after it, so the two can be compared. It doesn't write to `docs/screenshots/`. Its images go to
`artifacts/ux-baseline/<label>/`, which is git-ignored.

```bash
node tools/screenshots/baseline.mjs before
# ...change the UI...
node tools/screenshots/baseline.mjs after
```

The label is a folder name (letters, digits, dots, hyphens and underscores) and defaults to `before`. A run with a label
that already exists replaces that folder's images.

Each screen in its `screens` list is captured at 1920×1080, 1440×900 and 780×900, in the light and the dark theme, as
`<screen>-<width>x<height>-<theme>.png`. That is 96 images for the 16 screens: Today, Triage, an article in the editor,
Search with an indicator looked up, the Actions list and board, an action, Our stack and one product, the Library's
four tabs (opened by their old addresses, which lead to the tabs), and present mode's title slide, an article slide
and the wrap-up.

`ONLY`, `REUSE`, `PORT` and `BASE_URL` work as above (`ONLY` names screens). `FULL=1` captures the whole page instead of
what the window shows. The run exits non-zero if any screen fails, and names it.

To add a screen, add an entry to `screens`: a `name`, a `route` and an optional `setup`, as for a shot.

## What's new, tips and the tour on the demo

The demo never opens *What's new*, a page's tip or the tour by itself, so no page is checked or captured under one. A
browser asks for them on a page load, kept in a cookie for that browser like `?as=` (`?onboarding=off` goes back):

- `?onboarding=on`: everything, as for anyone. A name the demo hasn't seen signs in for the first time, so it takes
  the tour.
- `?onboarding=returning`: as someone who used TIDE before the upgrade, whatever the name: *What's new* opens, then
  each changed page's tip, and never the tour.

Each shows once a person, so the `whatsnew`, `tip` and `tour` layout pages sign in as a different person at each
width.

## Two people in a meeting

The seed adds Jordan, who holds the Viewer role and so can follow a live meeting. To check present mode as a presenter
and a follower, start TIDE in Development on a demo database (`Demo:Seed=true`, as `tide.mjs` starts it). Open
`/present/<today>` in one browser. In a second browser, or a private window, open
`/present/<today>?as=Jordan&roles=Viewer`. The dev sign-in keeps both choices in cookies for that browser. `demo.json`
names the follower as `Follower`.
