// Captures the documentation screenshots (docs/screenshots/<name>.png) from a freshly seeded TIDE.
//
//   node tools/screenshots/capture.mjs                    build, seed a demo database, start TIDE, capture every shot
//   ONLY=triage-inbox,doc-editions node tools/screenshots/capture.mjs     capture just these
//   REUSE=1 node tools/screenshots/capture.mjs            keep the demo database from the last run (faster)
//   BASE_URL=http://localhost:5112 node ...               use a TIDE already running on the demo database
//
// The manifest below is the list of shots. See README.md in this folder for how to add one and how callouts work.

import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { chromium } from 'playwright';
import { idle, openPage, pick, readDemo, repo, startTide, toArticleSlide } from './tide.mjs';

const outDir = path.join(repo, 'docs', 'screenshots');
const startedAt = new Date();

// --- the manifest -------------------------------------------------------------------------------------------------
//
// name      the file: docs/screenshots/<name>.png
// route     the page, with {today}, {sent}, {huntAction}, {detailAction}, {vpnArticle} or {sentArticle} filled from
//           the seed (see DemoSeeder.Seeded)
// viewport  { width, height }; fullPage: true captures the whole page at that width
// setup     async (page, demo) => {...}: clicks, keys or scrolling before the capture
// clip      a CSS selector: capture just that element (with `pad` pixels around it, default 16)
// annotate  [[selector, n], ...] or [[selector, n, corner]]: a numbered circle on the element's corner
//           (corner: tl (default), tr, bl, br, l, r). A selector that matches nothing fails the run.
// when      demo => boolean: skip the shot, saying so and leaving its image as it was, when what it shows isn't there
//           on the day of the run
// after     async page => {...}: puts back what setup changed, for the shots after it (run on a fresh page)

const desktop = { width: 1440, height: 1000 };
// A window wide enough for the context rail and the presenter's rail to be docked open (1500px and wider).
const wide = { width: 1920, height: 1080 };

/** Scrolls the first match to `top` pixels below the top of the window. */
const scrollTo = (selector, top = 70) => async page => {
  await page.locator(selector).first().evaluate((el, top) => window.scrollTo(0, el.getBoundingClientRect().top + window.scrollY - top), top);
};

/** In present mode: steps on through the slides until `selector` is shown (Actions that need review, say). */
const toSlide = selector => async page => {
  for (let i = 0; i < 15 && !(await page.locator(selector).count()); i++) {
    await page.keyboard.press('ArrowRight');
    await idle(page);
  }
};

/** Today says the feeds aren't pulled from 8:00 (Intake:PulledBy, below) on a working day, New York time. */
const feedsNoted = demo => demo.FeedsDue
  && Number(new Intl.DateTimeFormat('en-US', { hour: 'numeric', hourCycle: 'h23', timeZone: 'America/New_York' }).format(new Date())) >= 8;

/** Ticks or unticks "In a meeting, an action opens beside the slide" in Organization settings, and saves it. */
const actionBesideSlide = on => async page => {
  await page.goto(new URL('/admin/organization', page.url()).href, { waitUntil: 'load' });
  await idle(page);
  const box = page.getByLabel('In a meeting, an action opens beside the slide');
  if ((await box.isChecked()) === on) return;
  await box.setChecked(on);
  await page.locator('.tide-card').filter({ has: box }).getByRole('button', { name: 'Save', exact: true }).click();
  await idle(page);
};

export const shots = [
  // ---- the README's shots (same names, so existing links keep working) ----
  { name: 'brief-todays-numbers', route: '/', viewport: desktop,
    setup: scrollTo('.tide-card:has(.tide-mentry-bar)', 70) },
  { name: 'triage-inbox', route: '/triage', viewport: desktop },
  { name: 'article-editor', route: '/articles/{vpnArticle}', viewport: { width: 1440, height: 1300 } },
  { name: 'present-metrics-slide', route: '/present/{today}', viewport: desktop,
    setup: async page => { await page.keyboard.press('ArrowRight'); await idle(page); } },
  { name: 'email-team', route: '/briefs/{sent}/email/team', viewport: desktop, fullPage: true },
  { name: 'email-company-wide', route: '/briefs/{sent}/email/public', viewport: desktop, fullPage: true },
  { name: 'sharepoint-copy', route: '/briefs/{sent}/sharepoint', viewport: desktop },
  { name: 'published-archive', route: '/published/{sent}', viewport: desktop },
  { name: 'action-hunt', route: '/actions/{huntAction}', viewport: desktop },
  { name: 'workload', route: '/workload', viewport: desktop },
  { name: 'search-lookup', route: '/search?q=update-check.example.net', viewport: { width: 1400, height: 1200 } },
  { name: 'export-indicators', route: '/', viewport: { width: 1400, height: 900 },
    setup: async page => { await page.getByRole('button', { name: 'Export indicators' }).click(); await idle(page); } },
  { name: 'trends', route: '/trends', viewport: { width: 1500, height: 1100 },
    setup: async page => {
      await page.locator('tr:has(> td:text-is("Ransomware")) td.tide-trend-now button').first().click();
      await idle(page);
      await scrollTo('.tide-card:has(td:text-is("Ransomware"))', 70)(page);
    } },
  { name: 'metrics-history', route: '/metrics', viewport: desktop },
  { name: 'rotation-calendar', route: '/rotation', viewport: desktop },

  // ---- shots for the documentation, with numbered callouts ----
  { name: 'doc-today-steps', route: '/', viewport: desktop, clip: '.tide-page-head, .tide-steps', pad: 6,
    annotate: [
      ['.tide-steps > li:nth-child(1)', 1], ['.tide-steps > li:nth-child(2)', 2], ['.tide-steps > li:nth-child(3)', 3],
      ['.tide-steps > li:nth-child(4)', 4], ['.tide-steps > li:nth-child(5)', 5], ['.tide-page-head button:has-text("Mark ready")', 6, 'l'],
    ] },
  // On a window this wide the reading pane opens by itself, with the text already read: nothing is fetched.
  { name: 'doc-triage-reading', route: '/triage', viewport: desktop,
    setup: async page => { await page.locator('.tide-reading').first().waitFor(); },
    annotate: [
      ['.tide-tabbar-page', 1, 'l'], ['.tide-filterbar', 2, 'l'], ['.tide-triage-row', 3, 'l'],
      ['.tide-triage-row .tide-cve', 4, 'tr'], ['.tide-triage-decide button:has-text("Promote")', 5, 'l'], ['.tide-reading', 6, 'tr'],
    ] },
  { name: 'doc-article-editor', route: '/articles/{vpnArticle}', viewport: { width: 1920, height: 1300 },
    annotate: [
      ['.tide-section-pills', 1, 'l'], ['.tide-section:has(h2:has-text("Assessment"))', 2, 'l'],
      ['.tide-bullet-row:nth-of-type(3) select', 3, 'tr'], ['.tide-reader .tide-seg', 4, 'l'],
      ['.tide-reader-sum', 5, 'l'], ['.tide-sticky-save', 6, 'l'],
    ] },
  { name: 'doc-editions', route: '/briefs/{sent}?tab=preview', viewport: { width: 1440, height: 1200 },
    annotate: [
      ['.tide-both-ed:nth-of-type(1) .tide-both-head', 1, 'l'], ['.tide-both-ed:nth-of-type(2) .tide-both-head', 2, 'l'],
      ['.tide-both-ed:nth-of-type(1) .tide-ed-numbers-head', 3, 'l'], ['.tide-both-ed:nth-of-type(1) li .tide-tlp-amber', 4, 'r'],
      ['.tide-both-ed:nth-of-type(2) :text("restricted parts were left out")', 5, 'l'],
    ] },
  { name: 'doc-action-detail', route: '/actions/{detailAction}', viewport: wide,
    annotate: [
      ['.tide-action-head', 1, 'l'], ['.tide-facts-side', 2, 'l'], ['.tide-drawer', 3, 'l'],
      ['.tide-feed', 4, 'l'], ['.tide-rail-article', 5, 'l'], ['.tide-files-side', 6, 'l'],
    ] },
  { name: 'doc-admin-settings', route: '/admin/organization', viewport: desktop,
    annotate: [
      ['.tide-admin-tabs', 1, 'l'], ['.tide-admin-subtabs', 2, 'l'], ['.tide-admin-pane .tide-card:nth-of-type(1)', 3, 'l'],
      ['.tide-admin-pane .tide-card:has(h2:has-text("Holidays"))', 4, 'l'],
    ] },
  { name: 'doc-api-tokens', route: '/admin/api', viewport: desktop,
    annotate: [
      ['.tide-card:has(h2:has-text("Callers")) table', 1, 'l'], ['.tide-card:has(h2:has-text("Callers")) td .tide-tlp', 2, 'l'],
      ['.tide-card:has(h2:has-text("Callers")) button:has-text("Revoke")', 3, 'l'], ['h3:has-text("Issue a token")', 4, 'l'],
      ['button:has-text("Issue the token")', 5, 'r'],
    ] },
  // ---- the context rail, Triage's groups and Sources, the board, and present mode ----
  { name: 'context-rail-docked', route: '/', viewport: wide },
  { name: 'context-rail-overlay', route: '/', viewport: desktop,
    setup: async page => { await page.keyboard.press(']'); await page.locator('#tide-rail').waitFor(); } },
  { name: 'doc-triage-same-event', route: '/triage', viewport: wide,
    setup: async page => {
      const group = page.locator('.tide-triage-group').first();
      await group.locator('.tide-group-label').click();
      await idle(page);
      await group.locator('.tide-triage-head button[aria-expanded="false"]').first().click();
    },
    annotate: [['.tide-triage-group', 1, 'l'], ['.tide-triage-decide', 2], ['section.tide-card:has(#rail-same-h)', 3, 'l']] },
  { name: 'today-feeds', route: '/', viewport: desktop, clip: '.tide-steps, .tide-feeds-nudge', pad: 20, when: feedsNoted },
  { name: 'triage-pull-all', route: '/triage?tab=sources', viewport: desktop, clip: '.tide-card:has(.tide-pull-head)', pad: 20 },
  { name: 'doc-actions-board', route: '/actions?view=open&layout=board', viewport: desktop,
    annotate: [['.tide-actions-views', 1, 'l'], ['.tide-filterbar', 2, 'l'], ['.tide-kcard.selected', 3], ['.tide-kstrip', 4, 'l']] },
  { name: 'present-needs-review', route: '/present/{today}', viewport: desktop, setup: toSlide('.tide-slide-open') },
  { name: 'doc-present-rail', route: '/present/{today}', viewport: wide, setup: toArticleSlide,
    annotate: [['#rail-actions-h', 1, 'l'], ['.tide-present-rail-raise', 2, 'l'], ['.tide-quick-add', 3, 'l'], ['#rail-note-mine-h', 4, 'l']] },
  { name: 'present-wrap-up', route: '/present/{today}', viewport: wide,
    setup: async page => { await page.keyboard.press('End'); await page.locator('.tide-wrap').waitFor(); } },
  // Last: it turns on the Organization setting for an action beside the slide (off by default), and off again after.
  { name: 'present-action-beside', route: '/present/{today}', viewport: wide,
    setup: async (page, demo) => {
      await actionBesideSlide(true)(page);
      await page.goto(new URL(`/present/${demo.Today}`, page.url()).href, { waitUntil: 'load' });
      await idle(page);
      await toArticleSlide(page);
      await page.locator('.tide-slide :is(.tide-action-ref-btn, .tide-action-open)').first().click();
      await page.locator('.tide-present-action.is-beside').waitFor();
    },
    after: actionBesideSlide(false) },
];

// --- the run -----------------------------------------------------------------------------------------------------------

const wanted = pick(shots, 'shot');

let server;
try {
  // Three feeds last pulled the day before (never fetched: their addresses are on this machine), with polling on and
  // a cutoff at 8:00, so Today says the feeds aren't pulled on a working day, and Sources has something to Pull all.
  server = await startTide(['--Demo:StaleFeeds=true', '--Intake:Enabled=true', '--Intake:PulledBy=08:00']);
  const demo = readDemo();
  const problems = await capture(wanted, demo);
  if (problems.length) {
    for (const p of problems) console.error(p);
    process.exitCode = 1;
  }
} catch (e) {
  console.error(e.message ?? e);
  process.exitCode = 1;
} finally {
  server?.stop();
}

async function capture(list, demo) {
  mkdirSync(outDir, { recursive: true });
  const browser = await chromium.launch();
  const problems = [];
  try {
    for (const shot of list) {
      if (shot.when && !shot.when(demo)) {
        console.log(`  ${shot.name}.png skipped: what it shows isn't there today; the image is left as it was`);
        continue;
      }
      let context;
      try {
        // The browser's clock stands still at the moment the run started, so anything the page times reads the same.
        let page;
        ({ context, page } = await openPage(browser, { ...shot, viewport: shot.viewport ?? desktop }, demo, { deviceScaleFactor: 1, fixedTime: startedAt }));
        const missing = await annotate(page, shot.annotate ?? []);
        if (missing.length) {
          problems.push(`${shot.name}: annotation target(s) not found: ${missing.join(' | ')}`);
          continue;
        }
        const file = path.join(outDir, `${shot.name}.png`);
        await page.screenshot({ path: file, fullPage: !!shot.fullPage, animations: 'disabled', caret: 'hide', clip: await clipFor(page, shot) });
        console.log(`  ${shot.name}.png`);
      } catch (e) {
        problems.push(`${shot.name}: ${e.message.split('\n')[0]}`);
      } finally {
        await context?.close();
        // Even when the shot failed: its setup may have got as far as changing something.
        if (shot.after) {
          let back;
          try {
            let page;
            ({ context: back, page } = await openPage(browser, { route: '/', viewport: desktop }, demo));
            await shot.after(page, demo);
          } catch (e) {
            problems.push(`${shot.name}, putting back what it changed: ${e.message.split('\n')[0]}`);
          } finally {
            await back?.close();
          }
        }
      }
    }
  } finally {
    await browser.close();
  }
  return problems;
}


async function clipFor(page, shot) {
  if (!shot.clip) return undefined;
  const pad = shot.pad ?? 16;
  const boxes = (await Promise.all((await page.locator(shot.clip).all()).map(l => l.boundingBox()))).filter(Boolean);
  if (!boxes.length) throw new Error(`clip target not found: ${shot.clip}`);
  const vp = page.viewportSize();
  const x = Math.max(0, Math.min(...boxes.map(b => b.x)) - pad);
  const y = Math.max(0, Math.min(...boxes.map(b => b.y)) - pad);
  const right = Math.min(vp.width, Math.max(...boxes.map(b => b.x + b.width)) + pad);
  const bottom = Math.min(vp.height, Math.max(...boxes.map(b => b.y + b.height)) + pad);
  return { x, y, width: right - x, height: bottom - y };
}

/** Draws the numbered callouts on the page. Returns the selectors that matched nothing visible. */
async function annotate(page, callouts) {
  const targets = [];
  const missing = [];
  for (const [selector, n, corner = 'tl'] of callouts) {
    const box = await page.locator(selector).first().boundingBox({ timeout: 2000 }).catch(() => null);
    if (!box || box.width === 0 || box.height === 0) missing.push(selector);
    else targets.push({ n, corner, box });
  }
  if (missing.length || !targets.length) return missing;
  await page.evaluate(targets => {
    const r = 14;
    const layer = document.createElement('div');
    layer.id = 'tide-callouts';
    layer.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:2147483647';
    for (const { n, corner, box } of targets) {
      const x = { tl: box.x, bl: box.x, l: box.x - r - 4, tr: box.x + box.width, br: box.x + box.width, r: box.x + box.width + r + 4 }[corner];
      const y = { tl: box.y, tr: box.y, l: box.y + box.height / 2, r: box.y + box.height / 2, bl: box.y + box.height, br: box.y + box.height }[corner];
      const cx = Math.min(Math.max(x, r + 2), innerWidth - r - 2);
      const cy = Math.min(Math.max(y, r + 2), innerHeight - r - 2);
      const dot = document.createElement('div');
      dot.textContent = String(n);
      dot.style.cssText = `position:absolute;left:${cx - r}px;top:${cy - r}px;width:${2 * r}px;height:${2 * r}px;border-radius:50%;` +
        'background:#6d28d9;color:#fff;border:2px solid #fff;box-sizing:border-box;box-shadow:0 1px 4px rgba(0,0,0,.45);' +
        'font:700 14px/24px system-ui,Segoe UI,sans-serif;text-align:center';
      layer.appendChild(dot);
    }
    document.body.appendChild(layer);
  }, targets);
  return [];
}
