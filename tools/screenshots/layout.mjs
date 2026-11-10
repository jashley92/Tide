// Checks TIDE's layout in a real browser: rows of controls that must stay one line, columns that must line up, and
// pages that mustn't scroll sideways or let text spill out of its box, each at the widths it's used at (README.md says
// why, and how to add a page or a rule).
//
//   node tools/screenshots/layout.mjs                build, seed a demo database, start TIDE, check every page
//   ONLY=slide-article node tools/screenshots/layout.mjs                 just these pages
//   REUSE=1 / BASE_URL=http://localhost:5112 node ...                    as capture.mjs
//
// Each failure names the page, the width and the rule. Add a page or a rule below; README.md says how.

import { chromium } from 'playwright';
import { openPage, pick, readDemo, startTide, toArticleSlide } from './tide.mjs';

// --- the rules: each runs in the page and returns what's wrong (nothing, when it holds). A box is shown when it has
// width and height. --------------------------------------------------------------------------------------------------

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
    const lefts = selectors.map(s => [...document.querySelectorAll(s)].map(e => e.getBoundingClientRect()).find(r => r.width > 0 && r.height > 0));
    const missing = selectors.filter((_, i) => !lefts[i]);
    if (missing.length) return [`nothing visible to line up: ${missing.join(', ')}`];
    const xs = lefts.map(r => Math.round(r.left));
    return Math.max(...xs) - Math.min(...xs) > 2 ? [`not lined up: ${selectors.map((s, i) => `${s} at ${xs[i]}px`).join(', ')}`] : [];
  }, selectors),
});

/** The first match is wider than `px`: a page that takes the whole window isn't held to the usual width. */
const widerThan = (selector, px) => ({
  name: `wider than ${px}px: ${selector}`,
  check: page => page.$eval(selector, (el, [selector, px]) => {
    const width = Math.round(el.getBoundingClientRect().width);
    return width > px ? [] : [`${selector} is ${width}px wide, held to ${px}px or less`];
  }, [selector, px]),
});

/** At least one match is visible. */
const visible = selector => ({
  name: `visible: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) =>
    els.some(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; }) ? [] : [`${selector} isn't shown`], selector),
});

// --- the pages: route, optional setup, widths (each 900px tall) and rules ([minWidth, rule] from that width up) ------

const pages = [
  { name: 'today', route: '/', widths: [1440, 1000, 780],
    rules: [noSidewaysScroll, visible('.tide-steps'), fits('.tide-steps > li'), fits('.tide-page-head')] },
  { name: 'article', route: '/articles/{vpnArticle}', widths: [1440, 1000],
    rules: [noSidewaysScroll, visible('.tide-reader-sum'), visible('[role=tablist][aria-label="Beside the article"]')] },
  { name: 'article-actions-tab', route: '/articles/{vpnArticle}?side=actions', widths: [1440, 800],
    rules: [noSidewaysScroll, visible('#reader-panel .tide-action-ref')] },
  { name: 'actions-list', route: '/actions?tab=open&layout=list', widths: [1440, 1000],
    rules: [noSidewaysScroll, visible('.tide-action-ref')] },
  { name: 'actions-board', route: '/actions?tab=open&layout=board', widths: [1920, 1440],
    rules: [visible('.tide-kcard'), oneRow('.tide-kcard-head'), [1800, widerThan('.content', 1400)]] },
  { name: 'trends', route: '/trends', widths: [1920], rules: [noSidewaysScroll, widerThan('.content', 1400)] },
  { name: 'slide-article', route: '/present/{today}', setup: toArticleSlide, widths: [1440, 1100, 900],
    rules: [
      visible('.tide-quick-add'),
      [1001, oneRow('.tide-quick-add')],
      [1001, oneRow('.tide-reco li')],
      [1001, oneRow('.tide-slide-actlist > li')],
      [1001, lineUp('.tide-reco li > select:nth-of-type(1)', '.tide-slide-actlist .tide-action-assign > input')],
      [1001, lineUp('.tide-reco li > select:nth-of-type(2)', '.tide-slide-actlist .tide-action-assign > select')],
    ] },
  { name: 'metrics-grid', route: '/metrics?chart=group:alert-queue,group:vulnerability-sla&view=grid', widths: [1920, 1000, 700],
    rules: [noSidewaysScroll, visible('.tide-mc-cell'), visible('.tide-chart-band-warn'), oneRow('.tide-mc-cell-head'), [1800, widerThan('.content', 1400)]] },
];

// --- the run -----------------------------------------------------------------------------------------------------------

const wanted = pick(pages, 'page');

let server;
try {
  server = await startTide();
  const problems = await check(wanted, readDemo());
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
        const rules = p.rules.map(r => Array.isArray(r) ? r : [0, r]).filter(([minWidth]) => width >= minWidth).map(([, r]) => r);
        let context;
        try {
          let page;
          ({ context, page } = await openPage(browser, { ...p, viewport: { width, height: 900 } }, demo));
          const before = problems.length;
          for (const rule of rules) problems.push(...(await rule.check(page)).map(f => `${p.name} at ${width}px: ${f}`));
          console.log(`  ${p.name} at ${width}px: ${rules.length - (problems.length - before)}/${rules.length} rules hold`);
        } catch (e) {
          problems.push(`${p.name} at ${width}px: ${e.message.split('\n')[0]}`);
        } finally {
          await context?.close();
        }
      }
    }
  } finally {
    await browser.close();
  }
  return problems;
}
