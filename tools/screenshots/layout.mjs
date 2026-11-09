// Checks TIDE's layout in a real browser: rows of controls that must stay one line, columns that must line up, and
// pages that mustn't scroll sideways or let text spill out of its box, each at the widths it's used at. The HTML tests
// can't see these: v1.10.0's meeting quick add had all five controls in its markup, stacked one per line.
//
//   node tools/screenshots/layout.mjs                build, seed a demo database, start TIDE, check every page
//   ONLY=slide-article node tools/screenshots/layout.mjs                 just these pages
//   REUSE=1 / BASE_URL=http://localhost:5112 node ...                    as capture.mjs
//
// Each failure names the page, the width and the rule. Add a page or a rule below; README.md says how.

import { chromium } from 'playwright';
import { baseUrl, fill, readDemo, settle, startTide } from './tide.mjs';

// --- the rules: each runs in the page and returns what's wrong (nothing, when it holds) ---------------------------------

/** Every element matching `selector` lays its visible children out on one line (they share a horizontal band). */
const oneRow = selector => ({
  name: `one row: ${selector}`,
  check: page => page.$$eval(selector, (rows, selector) => rows.flatMap((row, i) => {
    const kids = [...row.children].map(c => c.getBoundingClientRect()).filter(r => r.width > 0 && r.height > 0);
    if (kids.length < 2) return [];
    const top = Math.max(...kids.map(r => r.top)), bottom = Math.min(...kids.map(r => r.bottom));
    return top < bottom ? [] : [`${selector} #${i + 1}: its ${kids.length} parts are on more than one line (${Math.round(row.getBoundingClientRect().height)}px tall)`];
  }), selector),
});

/** Nothing inside an element matching `selector` spills out of it sideways. */
const fits = selector => ({
  name: `fits: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) => els.flatMap((el, i) =>
    el.scrollWidth > el.clientWidth + 1 ? [`${selector} #${i + 1}: its content is ${el.scrollWidth - el.clientWidth}px wider than it`] : []), selector),
});

/** The page doesn't scroll sideways. */
const noSidewaysScroll = {
  name: 'no sideways scroll',
  check: page => page.evaluate(() => {
    const over = document.documentElement.scrollWidth - document.documentElement.clientWidth;
    return over > 1 ? [`the page is ${over}px wider than the window`] : [];
  }),
};

/** The first visible match of each selector starts at the same x (within 2px): a column that lines up. */
const lineUp = (...selectors) => ({
  name: `line up: ${selectors.join(' / ')}`,
  check: page => page.evaluate(selectors => {
    const lefts = selectors.map(s => [...document.querySelectorAll(s)].map(e => e.getBoundingClientRect()).find(r => r.width > 0));
    const missing = selectors.filter((_, i) => !lefts[i]);
    if (missing.length) return [`nothing visible to line up: ${missing.join(', ')}`];
    const xs = lefts.map(r => Math.round(r.left));
    return Math.max(...xs) - Math.min(...xs) > 2 ? [`not lined up: ${selectors.map((s, i) => `${s} at ${xs[i]}px`).join(', ')}`] : [];
  }, selectors),
});

/** At least one match is visible. */
const visible = selector => ({
  name: `visible: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) =>
    els.some(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; }) ? [] : [`${selector} isn't shown`], selector),
});

// --- the pages ----------------------------------------------------------------------------------------------------------
//
// route   as capture.mjs's ({today}, {vpnArticle}, ...)
// setup   async page => {...} before checking
// widths  the window widths to check at (each at 900px tall)
// rules   the rules above; [minWidth, rule] applies a rule only at that width or wider

/** Steps through the slides to the first article's (the one with its recommended lines and actions). */
const toArticleSlide = async page => {
  for (let i = 0; i < 15 && !(await page.locator('.tide-quick-add').count()); i++) {
    await page.keyboard.press('ArrowRight');
    await page.waitForTimeout(250);
  }
};

export const pages = [
  { name: 'today', route: '/', widths: [1440, 1000, 780],
    rules: [noSidewaysScroll, visible('.tide-steps'), fits('.tide-steps > li'), fits('.tide-page-head')] },
  { name: 'article', route: '/articles/{vpnArticle}', widths: [1440, 1000],
    rules: [noSidewaysScroll, visible('.tide-reader-sum'), visible('[role=tablist][aria-label="Beside the article"]')] },
  { name: 'article-actions-tab', route: '/articles/{vpnArticle}?side=actions', widths: [1440, 800],
    rules: [noSidewaysScroll, visible('#reader-panel .tide-action-ref')] },
  { name: 'actions-list', route: '/actions?tab=open&layout=list', widths: [1440, 1000],
    rules: [noSidewaysScroll, visible('.tide-action-ref')] },
  { name: 'actions-board', route: '/actions?tab=open&layout=board', widths: [1440],
    rules: [visible('.tide-kcard'), oneRow('.tide-kcard-head')] },
  { name: 'slide-article', route: '/present/{today}', setup: toArticleSlide, widths: [1440, 1100, 900],
    rules: [
      visible('.tide-quick-add'),
      [1001, oneRow('.tide-quick-add')],
      [1001, oneRow('.tide-reco li')],
      [1001, oneRow('.tide-slide-actlist > li')],
      [1001, lineUp('.tide-reco li > select:nth-of-type(1)', '.tide-slide-actlist .tide-action-assign > input')],
      [1001, lineUp('.tide-reco li > select:nth-of-type(2)', '.tide-slide-actlist .tide-action-assign > select')],
    ] },
];

// --- the run -----------------------------------------------------------------------------------------------------------

const only = (process.env.ONLY ?? '').split(',').map(s => s.trim()).filter(Boolean);
const unknown = only.filter(n => !pages.some(p => p.name === n));
if (unknown.length) {
  console.error(`No page named ${unknown.join(', ')}. Pages: ${pages.map(p => p.name).join(', ')}`);
  process.exit(1);
}

let server;
try {
  server = await startTide();
  const problems = await check(only.length ? pages.filter(p => only.includes(p.name)) : pages, readDemo());
  if (problems.length) {
    console.error(`\n${problems.length} layout problem${problems.length === 1 ? '' : 's'}:`);
    for (const p of problems) console.error(`  ${p}`);
    process.exitCode = 1;
  } else console.log('\nEvery layout rule holds.');
} catch (e) {
  console.error(e.message ?? e);
  process.exitCode = 1;
} finally {
  server?.stop();
}

async function check(list, demo) {
  const browser = await chromium.launch();
  const problems = [];
  try {
    for (const p of list) {
      for (const width of p.widths) {
        const context = await browser.newContext({ viewport: { width, height: 900 }, reducedMotion: 'reduce', locale: 'en-US', timezoneId: 'America/New_York' });
        const page = await context.newPage();
        try {
          await page.goto(baseUrl() + fill(p.route, demo), { waitUntil: 'networkidle' });
          await settle(page);
          if (p.setup) { await p.setup(page); await settle(page); }
          let held = 0;
          for (const entry of p.rules) {
            const [minWidth, rule] = Array.isArray(entry) ? entry : [0, entry];
            if (width < minWidth) continue;
            const found = await rule.check(page);
            problems.push(...found.map(f => `${p.name} at ${width}px: ${f}`));
            if (!found.length) held++;
          }
          console.log(`  ${p.name} at ${width}px: ${held} rule${held === 1 ? '' : 's'} hold`);
        } catch (e) {
          problems.push(`${p.name} at ${width}px: ${e.message.split('\n')[0]}`);
        } finally {
          await context.close();
        }
      }
    }
  } finally {
    await browser.close();
  }
  return problems;
}
