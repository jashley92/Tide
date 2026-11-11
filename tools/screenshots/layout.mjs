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
import { openPage, pick, readDemo, settle, startTide, toArticleSlide } from './tide.mjs';

// --- the rules: each runs in the page and returns what's wrong (nothing, when it holds). A box is shown when it has
// width and height. --------------------------------------------------------------------------------------------------

/** Every element matching `selector` lays its visible children out on one line (they share a horizontal band, more than
 * the pixel or two of a border two wrapped lines can overlap by). */
const oneRow = selector => ({
  name: `one row: ${selector}`,
  check: page => page.$$eval(selector, (rows, selector) => rows.flatMap((row, i) => {
    const kids = [...row.children].map(c => c.getBoundingClientRect()).filter(r => r.width > 0 && r.height > 0);
    if (kids.length < 2) return [];
    const top = Math.max(...kids.map(r => r.top)), bottom = Math.min(...kids.map(r => r.bottom));
    return top < bottom - 2 ? [] : [`${selector} #${i + 1}: its ${kids.length} parts are on more than one line (${Math.round(row.getBoundingClientRect().height)}px tall)`];
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

/** While a match of `beside` is shown, the first match of `selector` is at least `px` wide: what `beside` adds to a
 *  row mustn't squeeze what matters more (the brief chip and the search box). Holds when `beside` isn't shown. */
const roomBeside = (selector, px, beside) => ({
  name: `at least ${px}px beside ${beside}: ${selector}`,
  check: page => page.evaluate(([selector, px, beside]) => {
    const box = s => [...document.querySelectorAll(s)].map(e => e.getBoundingClientRect()).find(r => r.width > 0 && r.height > 0);
    if (!box(beside)) return [];
    const width = Math.round(box(selector)?.width ?? 0);
    return width >= px ? [] : [`${selector} is ${width}px wide beside ${beside}, under ${px}px`];
  }, [selector, px, beside]),
});

/** At least one match is visible. */
const visible = selector => ({
  name: `visible: ${selector}`,
  check: async page => (await anyShown(page, selector)) ? [] : [`${selector} isn't shown`],
});

/** In each element matching `cell`, the first `b` sits on the line `a` ends on (their boxes share a horizontal band):
 * a badge that stays beside the title rather than dropping to a line of its own. Cells without both are skipped. */
const onTitleLine = (cell, a, b) => ({
  name: `same line: ${a} and ${b} in ${cell}`,
  check: page => page.$$eval(cell, (cells, [a, b]) => cells.flatMap((c, i) => {
    const ea = c.querySelector(a), eb = c.querySelector(b);
    if (!ea || !eb) return [];
    const ra = [...ea.getClientRects()].pop(), rb = eb.getBoundingClientRect();
    return rb.top < ra.bottom && rb.bottom > ra.top ? [] : [`#${i + 1}: ${b} is on another line from ${a} (${Math.round(rb.top - ra.top)}px lower)`];
  }), [a, b]),
});

/** Focus is on the title of the row `take` named (its `data-expect-focus`): the person's place in the list is kept. */
const focusedOnExpectedRow = {
  name: 'focused: the expected row\'s title',
  check: page => page.evaluate(() => {
    const want = document.body.dataset.expectFocus, el = document.activeElement;
    const num = el?.closest('tr')?.querySelector('.tide-action-num')?.innerText;
    if (el?.matches('.tide-table .tide-action-ref') && num === want) return [];
    return [`focus is on ${el && el !== document.body ? `<${el.tagName.toLowerCase()} class="${el.className}">${num ?? ''}` : 'the page body'}, not ${want}'s title`];
  }),
};

/**
 * Takes on an action from the keyboard (Tab to its Take it, Enter): the `which` ('first' or 'last') row offering it.
 * The row focus should land on is noted on the body: `expect` 'same' (it stays listed), 'next' or 'previous' (it leaves).
 */
const take = (which, expect) => async page => {
  const rows = page.locator('.tide-table tbody tr:has(.tide-row-take)');
  const row = which === 'last' ? rows.last() : rows.first();
  const all = await page.$$eval('.tide-table tbody tr .tide-action-num', ns => ns.map(n => n.innerText));
  const mine = await row.locator('.tide-action-num').innerText(), at = all.indexOf(mine);
  const want = expect === 'same' ? mine : expect === 'next' ? all[at + 1] : all[at - 1];
  await page.evaluate(w => { document.body.dataset.expectFocus = w; }, want);
  const button = await row.locator('.tide-row-take').elementHandle();
  await button.focus();
  await page.keyboard.press('Enter');
  // The button goes once the take is saved (settle then waits for the page to be still).
  await page.waitForFunction(el => !el.isConnected, button, { timeout: 5000 }).catch(() => { });
};

/** The first visible child of the row `selector` matching `which` (in the page): whether it's wholly inside the row's
 * visible box, within a pixel, as a row that scrolls sideways inside itself must bring it there. */
const inRow = (page, selector, which) => page.$eval(selector, (row, which) => {
  const el = which === 'focused' ? document.activeElement : row.querySelector('[aria-pressed="true"], [aria-current="page"], [aria-selected="true"]');
  if (!el || el.parentElement !== row) return [which === 'focused' ? `focus isn't on one of its tabs (it's on ${el?.tagName ?? 'nothing'})` : 'no current tab'];
  const e = el.getBoundingClientRect(), r = row.getBoundingClientRect();
  return e.left >= r.left - 1 && e.right <= r.right + 1 ? [] : [`"${el.innerText.replace(/\s+/g, ' ').trim()}" (${which}) is only partly in view`];
}, which);

/** Tabbing from the first button of the row `selector` to its last, each one is wholly in view as it takes focus. */
const tabsInView = selector => ({
  name: `each tab in view as it's focused: ${selector}`,
  check: async page => {
    const count = await page.$$eval(`${selector} > *`, ks => ks.length);
    await page.$eval(`${selector} > *`, b => b.focus());
    const problems = [];
    for (let i = 0; i < count; i++) {
      if (i > 0) await page.keyboard.press('Tab');
      await page.waitForTimeout(150);
      problems.push(...await inRow(page, selector, 'focused'));
    }
    return problems;
  },
});

/** The current item of the row `selector` (aria-pressed, aria-current or aria-selected) is wholly in view inside it. */
const activeInView = selector => ({ name: `current tab in view: ${selector}`, check: page => inRow(page, selector, 'current') });

/** A mouse click on the visible edge of the row `selector`'s tab that its right edge cuts (or covers with its fade)
 * chooses that tab: the address then says its `?view=` (a row that scrolls to show the tab mustn't swallow the click). */
const clickAtEdgeChooses = selector => ({
  name: `a click at the edge chooses that tab: ${selector}`,
  check: async page => {
    const hit = await page.$eval(selector, row => {
      const r = row.getBoundingClientRect(), x = r.right - 6, y = r.top + r.height / 2;
      const tab = [...row.children].find(t => { const b = t.getBoundingClientRect(); return b.left <= x && b.right >= x; });
      return tab ? { x, y, name: tab.innerText.replace(/\s+/g, ' ').trim() } : null;
    });
    if (!hit) return ['no tab at the row\'s right edge'];
    await page.mouse.click(hit.x, hit.y);
    await page.waitForTimeout(1500);
    const pressed = await page.$eval(`${selector} [aria-pressed="true"]`, t => t.innerText.replace(/\s+/g, ' ').trim());
    return pressed === hit.name ? [] : [`clicked "${hit.name}" at its visible edge, but "${pressed}" is still the view`];
  },
});

/** Nothing of the row `selector`'s children (their borders, the current tab's underline, a focus ring drawn inside them)
 * is cut off at the bottom of the row: a row that scrolls sideways clips whatever hangs below its box. */
const notClippedBelow = selector => ({
  name: `nothing clipped below: ${selector}`,
  check: page => page.$eval(selector, row => {
    const bottom = row.getBoundingClientRect().top + row.clientTop + row.clientHeight;
    return [...row.children].flatMap(t => {
      const over = t.getBoundingClientRect().bottom - bottom;
      return over > 0.5 ? [`"${t.innerText.replace(/\s+/g, ' ').trim()}" hangs ${over.toFixed(1)}px below the row's box, where it's cut off`] : [];
    });
  }),
});

/** Whether any match of `selector` is shown (has width and height). */
const anyShown = (page, selector) => page.$$eval(selector, els =>
  els.some(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; }));

/** No match is visible (none there, or each hidden). */
const hidden = selector => ({
  name: `hidden: ${selector}`,
  check: async page => (await anyShown(page, selector)) ? [`${selector} is shown`] : [],
});

/**
 * Triage's selection, and focus, are where the keys pressed quickly (pressQuickly) were aimed: as many rows on from where
 * they started as keys were pressed, on to the next page past the last row or back to the previous one before the first
 * (the address says which page). Counted in lines as shown, whatever the seed's groups make of a page.
 */
const selectedAsAimed = () => ({
  name: 'selected: the row the keys were aimed at',
  check: page => page.evaluate(() => {
    const aim = window.__tideAim;
    const rows = [...document.querySelectorAll('.tide-triage-row')];
    const at = rows.findIndex(r => r.classList.contains('selected'));
    const focused = rows.indexOf(document.activeElement);
    const shown = Number(new URL(location.href).searchParams.get('page') ?? 1);
    let [listPage, row] = [aim.page, aim.row + aim.step];
    if (row >= aim.count) [listPage, row] = [listPage + 1, row - aim.count];
    else if (row < 0) [listPage, row] = [listPage - 1, rows.length + row];
    return shown === listPage && at === row && focused === row ? []
      : [`page ${shown}, row ${at + 1} of ${rows.length} is selected and row ${focused + 1} has focus, not page ${listPage}, row ${row + 1}`];
  }),
});

/** Each visible match of `selector` starts at or below the top bar's bottom edge: nothing slides under the bar. */
const belowTopBar = selector => ({
  name: `below the top bar: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) => {
    const bar = document.querySelector('.top-row')?.getBoundingClientRect().bottom ?? 0;
    return els.map(e => e.getBoundingClientRect()).filter(r => r.height > 0 && r.top < bar - 1)
      .map(r => `${selector} starts at ${Math.round(r.top)}px, under the top bar (to ${Math.round(bar)}px)`);
  }, selector),
});

/** The first visible match of each selector shares one horizontal band: side by side, neither on a line of its own. */
const sameLine = (...selectors) => ({
  name: `same line: ${selectors.join(' / ')}`,
  check: page => page.evaluate(selectors => {
    const boxes = selectors.map(s => [...document.querySelectorAll(s)].map(e => e.getBoundingClientRect()).find(r => r.width > 0 && r.height > 0));
    const missing = selectors.filter((_, i) => !boxes[i]);
    if (missing.length) return [`nothing visible: ${missing.join(', ')}`];
    return Math.max(...boxes.map(r => r.top)) < Math.min(...boxes.map(r => r.bottom)) ? [] : [`not on one line: ${selectors.join(', ')}`];
  }, selectors),
});

/**
 * Setup: `key` pressed `times` quickly, 25ms apart, over a connection that takes 50ms each way, as a server across a
 * network does. The keys go faster than the answers come back: none may be lost, applied out of order, or undone by a
 * row taking focus, even across a page of the list. Waits until the selection has stayed put for a second.
 */
const pressQuickly = (key, times) => async page => {
  await page.routeWebSocket(/_blazor/, ws => {
    const server = ws.connectToServer();
    ws.onMessage(m => setTimeout(() => server.send(m), 50));
    server.onMessage(m => setTimeout(() => ws.send(m), 50));
  });
  await page.reload({ waitUntil: 'networkidle' });
  await settle(page);
  await page.locator('.tide-triage-row.selected').focus();
  // Where the keys start, for selectedAsAimed.
  await page.evaluate(step => {
    const rows = [...document.querySelectorAll('.tide-triage-row')];
    window.__tideAim = { page: Number(new URL(location.href).searchParams.get('page') ?? 1), row: rows.findIndex(r => r.classList.contains('selected')), count: rows.length, step };
  }, key === 'k' ? -times : times);
  for (let i = 0; i < times; i++) {
    await page.keyboard.press(key);
    await page.waitForTimeout(25);
  }
  const where = () => page.evaluate(() => location.href + '#' + [...document.querySelectorAll('.tide-triage-row')].findIndex(r => r.classList.contains('selected')));
  let was = await where();
  for (let quiet = 0, waited = 0; quiet < 1000 && waited < 15000; waited += 250) {
    await page.waitForTimeout(250);
    const now = await where();
    quiet = now === was ? quiet + 250 : 0;
    was = now;
  }
};

/**
 * Setup: the first line covered by an article (a coverage flag) is selected, by a click on it: a report's own line, or
 * with `group` a suggested group's. Every letter is spaced wider, as a font wider than this machine's would set it, so
 * a rule that holds holds by the page's construction (truncation, widths), not by pixels to spare.
 */
const selectCovered = (group = false) => async page => {
  await page.addStyleTag({ content: '* { letter-spacing: .07em !important; }' });
  const line = page.locator(group ? '.tide-triage-group' : '.tide-triage-row:not(.tide-triage-group)', { has: page.locator('.tide-triage-flags .tide-badge-info') }).first();
  await line.locator(group ? '.tide-group-label' : '.tide-triage-meta > span').first().click();
  await page.locator('.tide-triage-row.selected .tide-triage-attach').waitFor();
};

/** Setup: V pressed `times`, a moment apart (each read or close reaching the page before the next). */
const pressV = times => async page => {
  for (let i = 0; i < times; i++) {
    await page.keyboard.press('v');
    await page.waitForTimeout(700);
  }
};

/** Setup: V held down for a second and a half, the keyboard repeating it (20 keydowns, all but the first repeats). */
async function holdV(page) {
  for (let i = 0; i < 20; i++) {
    await page.keyboard.down('v');
    await page.waitForTimeout(75);
  }
  await page.keyboard.up('v');
  // Once the pane has gone, a moment more for a late switch back to show.
  await page.locator('.tide-reading').waitFor({ state: 'hidden', timeout: 5000 }).catch(() => {});
  await page.waitForTimeout(500);
}

/**
 * Setup: a context rail on the page, as ContextRail draws one (no page places one yet): its button in the top bar and
 * its region with a link in it, opened (as an overlay below 1500px) the way the button opens it. Focus goes into it.
 */
async function openRail(page) {
  await page.evaluate(() => {
    const button = document.createElement('button');
    button.id = 'tide-rail-toggle';
    button.type = 'button';
    button.textContent = 'Context';
    document.querySelector('.tide-top-end')?.prepend(button);
    const rail = document.createElement('aside');
    rail.id = 'tide-rail';
    rail.className = 'tide-rail';
    rail.tabIndex = -1;
    rail.innerHTML = '<div class="tide-rail-body"><a href="#" id="rail-link">A link in the rail</a></div>';
    document.body.append(rail);   // fixed in place as an overlay, wherever it sits
    window.tideRail.start();
  });
  await page.click('#tide-rail-toggle');
  await page.locator('#tide-rail').waitFor();
  await page.focus('#rail-link');
}

const selectedRow = page => page.evaluate(() => [...document.querySelectorAll('.tide-triage-row')].findIndex(r => r.classList.contains('selected')));

/** The keys pressed in the open rail are the rail's: J and the down arrow there leave the list's selection where it is. */
const railKeysStayInTheRail = {
  name: 'keys in the rail leave the list alone',
  check: async page => {
    const problems = [], before = await selectedRow(page);
    if (before < 0) return ['no row is selected to begin with'];
    for (const key of ['j', 'ArrowDown']) {
      await page.focus('#rail-link');
      await page.keyboard.press(key);
      await page.waitForTimeout(600);
      const now = await selectedRow(page);
      if (now !== before) problems.push(`${key} in the rail moved the selection from row ${before + 1} to row ${now + 1}`);
    }
    return problems;
  },
};

/** Escape in the open rail closes the rail, and only the rail: the reading pane stays, and so does its kept state
 * (the page opened again still opens the pane by itself). */
const escapeClosesTheRailOnly = {
  name: 'Escape closes the rail only',
  check: async page => {
    await page.focus('#rail-link');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(800);
    const problems = [];
    if (await anyShown(page, '#tide-rail')) problems.push('the rail is still open after Escape');
    if (!await anyShown(page, '.tide-reading')) problems.push('Escape in the rail closed the reading pane too');
    await page.reload({ waitUntil: 'networkidle' });
    await settle(page);
    if (!await anyShown(page, '.tide-reading')) problems.push('the reading pane is kept closed after Escape in the rail');
    return problems;
  },
};

/** Our stack's Add a product, from the keyboard: opening the form puts focus in its first box, and Cancel puts it back
 * on the button that opened it (not on the page's body, where the next Tab starts from the top). */
const addFormTakesFocus = {
  name: 'Add a product: focus into the form, and back to its button',
  check: async page => {
    const problems = [], focused = () => page.evaluate(() => {
      const el = document.activeElement;
      return !el || el === document.body ? 'the page body' : el.id || el.innerText.trim().slice(0, 40) || el.tagName;
    });
    const open = page.getByRole('button', { name: 'Add a product' });
    await open.focus();
    await page.keyboard.press('Enter');
    await page.waitForTimeout(600);
    if (await focused() !== 'new-name') problems.push(`opening the form left focus on ${await focused()}, not its Product box`);
    await page.getByRole('button', { name: 'Cancel' }).focus();
    await page.keyboard.press('Enter');
    await page.waitForTimeout(600);
    if (await focused() !== 'Add a product') problems.push(`Cancel left focus on ${await focused()}, not Add a product`);
    return problems;
  },
};

// The reading pane's open or closed is kept for the person, so the pages that press V sign in as someone of their own
// for this run (the dev sign-in's ?as=), and everyone else's Triage opens as it always does.
const run = Date.now().toString(36);

/** Setup: the page scrolled well down, past the top of the list. */
async function scrollDown(page) {
  await page.mouse.wheel(0, 1500);
  await page.waitForTimeout(300);
}

// --- the pages: route, optional setup, widths (each 900px tall) and rules ([minWidth, rule] from that width up) ------

const pages = [
  { name: 'today', route: '/', widths: [1440, 1000, 780],
    rules: [noSidewaysScroll, visible('.tide-steps'), fits('.tide-steps > li'), fits('.tide-page-head')] },
  // The top bar with the brief chip in it: one row from a narrow window up, nothing pushed out of its group (the Go to
  // button under the chip), and the search box never squeezed for the chip: the chip is in view from 900px, and
  // wherever it shows the search box keeps 200px. On a phone nothing pushes the page sideways.
  { name: 'top-bar', route: '/articles', widths: [1440, 1100, 1000, 900, 780, 375],
    rules: [noSidewaysScroll, [900, visible('.tide-brief-chip')], [780, oneRow('.top-row')], [780, oneRow('.tide-top-end')],
      [780, fits('.top-row')], [780, fits('.tide-top-find')], roomBeside('.tide-top-search', 200, '.tide-brief-chip')] },
  { name: 'article', route: '/articles/{vpnArticle}', widths: [1440, 1000],
    rules: [noSidewaysScroll, visible('.tide-reader-sum'), visible('[role=tablist][aria-label="Beside the article"]')] },
  { name: 'article-actions-tab', route: '/articles/{vpnArticle}?side=actions', widths: [1440, 800],
    rules: [noSidewaysScroll, visible('#reader-panel .tide-action-ref')] },
  // The page reads the tab from ?view= (each tab with its count). The tabs are one row at every width: when wider fonts
  // don't leave room, the row scrolls sideways inside itself, so there's no fits() on it; noSidewaysScroll checks that the
  // page never does. The Escalated badge stays on the number's line, the title wrapping before them.
  { name: 'actions-list', route: '/actions?view=open&layout=list', widths: [1920, 1440, 1000, 780],
    rules: [noSidewaysScroll, visible('.tide-action-ref'), visible('.tide-tabbar-count'), oneRow('[role=group][aria-label="Views"]'),
      visible('.tide-row-progress'), onTitleLine('.tide-table td:first-child', '.tide-action-num', '[title^="Escalated"]'),
      notClippedBelow('[role=group][aria-label="Views"]')] },
  // Take it from the keyboard keeps the person's place: on a row that stays listed (All open), focus stays on its title;
  // in No owner the row leaves, and focus goes to the next row's title, or for the last row, the one before.
  { name: 'actions-take-focus-stays', route: '/actions?view=open&layout=list', setup: take('first', 'same'), widths: [1920],
    rules: [focusedOnExpectedRow] },
  { name: 'actions-take-focus', route: '/actions?view=open&layout=list&assignee=none', setup: take('first', 'next'), widths: [1440],
    rules: [focusedOnExpectedRow] },
  // The view tabs scroll sideways inside their row when they don't fit (a phone, wider fonts): a tab taking focus comes
  // wholly into view, and the current one is in view when the page opens on it.
  { name: 'actions-tabs-focus', route: '/actions?view=open&layout=list', widths: [375, 780],
    rules: [tabsInView('[role=group][aria-label="Views"]')] },
  { name: 'actions-tabs-current', route: '/actions?view=closed&layout=list', widths: [375, 320],
    rules: [activeInView('[role=group][aria-label="Views"]')] },
  // A mouse click on a tab the row's edge cuts or fades still chooses it (the row doesn't scroll away under the pointer).
  { name: 'actions-tabs-click', route: '/actions?view=open&layout=list', widths: [375, 320],
    rules: [clickAtEdgeChooses('[role=group][aria-label="Views"]')] },
  { name: 'actions-take-focus-last', route: '/actions?view=open&layout=list&assignee=none', setup: take('last', 'previous'), widths: [780],
    rules: [focusedOnExpectedRow] },
  // The four working columns share the width and Closed folds into one narrow column: nothing scrolls sideways, the
  // board included, on a laptop's window.
  { name: 'actions-board', route: '/actions?view=open&layout=board', widths: [1920, 1440],
    rules: [noSidewaysScroll, fits('.tide-kanban'), visible('.tide-kcard'), visible('.tide-kclosed .tide-kcol[data-status="Done"]'),
      oneRow('.tide-kcard-head'), fits('.tide-kcard-article'), [1800, widerThan('.content', 1400)]] },
  // A sweep's query is read whole, in the wide column: its long lines wrap, so nothing scrolls sideways inside its box.
  { name: 'action-hunt', route: '/actions/{huntAction}', widths: [1920, 1440],
    rules: [noSidewaysScroll, visible('.tide-action-main .tide-hunt pre.tide-query'), fits('.tide-hunt pre.tide-query')] },
  // Triage: the keys on one line, each row's flags fitting its line, and the reading pane open by itself from 1400px.
  { name: 'triage', route: '/triage', widths: [1920, 1440, 780],
    rules: [noSidewaysScroll, fits('.tide-page-head'), visible('.tide-triage-keys'), oneRow('.tide-triage-keys'), visible('.tide-triage-row.selected'), fits('.tide-triage-row'),
      fits('.tide-triage-flags'), [1400, visible('.tide-reading')]] },
  { name: 'triage-narrow', route: '/triage', widths: [1300, 780], rules: [hidden('.tide-reading')] },
  // The reading pane open by itself stays below the top bar as the page scrolls; a covered row's Attach and Promote
  // share a line beside it.
  { name: 'triage-scrolled', route: '/triage', setup: scrollDown, widths: [1440], rules: [visible('.tide-reading'), belowTopBar('.tide-reading')] },
  { name: 'triage-covered', route: '/triage', setup: selectCovered(), widths: [1440, 1920],
    rules: [visible('.tide-reading'), sameLine('.tide-triage-row.selected .tide-triage-attach', '.tide-triage-row.selected .tide-triage-promote'), fits('.tide-triage-row.selected')] },
  // A covered group's Attach all and Promote as one share a line too (the demo's covered group: Demo:TriageFiller below).
  { name: 'triage-covered-group', route: '/triage', setup: selectCovered(true), widths: [1440, 1920],
    rules: [visible('.tide-reading'), sameLine('.tide-triage-group.selected .tide-triage-attach', '.tide-triage-group.selected .tide-triage-promote'), fits('.tide-triage-group.selected')] },
  // V closes the pane that opened by itself, and opens it again; on a narrow window, where it's hidden, V opens it.
  // Held down, V is one press: the keyboard's repeats don't open and close the pane over and over.
  { name: 'triage-v-held', route: `/triage?as=v4${run}`, setup: holdV, widths: [1440], rules: [hidden('.tide-reading')] },
  { name: 'triage-v-closes', route: `/triage?as=v1${run}`, setup: pressV(1), widths: [1440], rules: [hidden('.tide-reading')] },
  { name: 'triage-v-reopens', route: `/triage?as=v2${run}`, setup: pressV(2), widths: [1440], rules: [visible('.tide-reading')] },
  { name: 'triage-v-narrow', route: `/triage?as=v3${run}`, setup: pressV(1), widths: [780], rules: [visible('.tide-reading')] },
  // The context rail as an overlay over Triage: the keys pressed in it are its own. J and the arrows there don't move
  // the list, and Escape closes the rail without closing the reading pane (or keeping it closed).
  { name: 'triage-rail-keys', route: `/triage?as=r1${run}`, setup: openRail, widths: [1440],
    rules: [visible('#tide-rail'), visible('.tide-reading'), railKeysStayInTheRail, escapeClosesTheRailOnly] },
  // J and K pressed quickly land on the row they were pressed for: on the page (the selection isn't pulled back by a
  // row's focus), and across a page boundary both ways (the demo has a second page: Demo:TriageFiller below).
  { name: 'triage-keys', route: '/triage', setup: pressQuickly('j', 10), widths: [1440], rules: [selectedAsAimed()] },
  { name: 'triage-keys-next-page', route: '/triage?row=45', setup: pressQuickly('j', 10), widths: [1440], rules: [selectedAsAimed()] },
  { name: 'triage-keys-previous-page', route: '/triage?page=2&row=4', setup: pressQuickly('k', 10), widths: [1440], rules: [selectedAsAimed()] },
  { name: 'stack-add-focus', route: '/stack', widths: [1440], rules: [addFormTakesFocus] },
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
  // Enough more new items for Triage to have a second page (the paging keys' rules).
  server = await startTide(['--Demo:TriageFiller=55']);
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
