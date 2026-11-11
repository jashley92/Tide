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

// A rule over every match of a selector would hold of nothing when nothing matches (a renamed class, a page that drew
// something else, a seed without the case), so each says so when it examined nothing shown, rather than holding.

/** Every element matching `selector` lays its visible children out on one line (they share a horizontal band, more than
 * the pixel or two of a border two wrapped lines can overlap by). */
const oneRow = selector => ({
  name: `one row: ${selector}`,
  check: page => page.$$eval(selector, (rows, selector) => {
    const shown = rows.filter(e => e.getBoundingClientRect().height > 0);
    if (!shown.length) return [`nothing shown matches ${selector}`];
    return shown.flatMap((row, i) => {
      const kids = [...row.children].map(c => c.getBoundingClientRect()).filter(r => r.width > 0 && r.height > 0);
      if (kids.length < 2) return [];
      const top = Math.max(...kids.map(r => r.top)), bottom = Math.min(...kids.map(r => r.bottom));
      return top < bottom - 2 ? [] : [`${selector} #${i + 1}: its ${kids.length} parts are on more than one line (${Math.round(row.getBoundingClientRect().height)}px tall)`];
    });
  }, selector),
});

/** Nothing inside an element matching `selector` spills out of it sideways. */
const fits = selector => ({
  name: `fits: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) => {
    const shown = els.filter(e => e.getBoundingClientRect().height > 0);
    if (!shown.length) return [`nothing shown matches ${selector}`];
    return shown.flatMap((el, i) =>
      el.scrollWidth > el.clientWidth + 1 ? [`${selector} #${i + 1}: its content is ${el.scrollWidth - el.clientWidth}px wider than it`] : []);
  }, selector),
});

/** The page doesn't scroll sideways. */
const noSidewaysScroll = {
  name: 'no sideways scroll',
  check: page => page.evaluate(() => {
    const over = document.documentElement.scrollWidth - document.documentElement.clientWidth;
    return over > 1 ? [`the page is ${over}px wider than the window`] : [];
  }),
};

/** The first shown match of `selector` starts within `px` of the bottom of the first shown match of `above`: nothing big
 *  (a repeated heading or paragraph) sits between them, whatever the fonts make of the rows above both. */
const closeBelow = (selector, above, px) => ({
  name: `within ${px}px below ${above}: ${selector}`,
  check: page => page.evaluate(([selector, above, px]) => {
    const box = s => [...document.querySelectorAll(s)].map(e => e.getBoundingClientRect()).find(r => r.width > 0 && r.height > 0);
    const b = box(selector), a = box(above);
    if (!b || !a) return [`nothing shown matches ${!b ? selector : above}`];
    const gap = Math.round(b.top - a.bottom);
    return gap <= px ? [] : [`${selector} starts ${gap}px below ${above}, more than ${px}px`];
  }, [selector, above, px]),
});

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
 * a badge that stays beside the title rather than dropping to a line of its own. Cells without both are skipped, but
 * at least one must have both. */
const onTitleLine = (cell, a, b) => ({
  name: `same line: ${a} and ${b} in ${cell}`,
  check: page => page.$$eval(cell, (cells, [cell, a, b]) => {
    const both = cells.map((c, i) => [i, c.querySelector(a), c.querySelector(b)]).filter(([, ea, eb]) => ea && eb);
    if (!both.length) return [`no ${cell} has both ${a} and ${b}`];
    return both.flatMap(([i, ea, eb]) => {
      const ra = [...ea.getClientRects()].pop(), rb = eb.getBoundingClientRect();
      return rb.top < ra.bottom && rb.bottom > ra.top ? [] : [`#${i + 1}: ${b} is on another line from ${a} (${Math.round(rb.top - ra.top)}px lower)`];
    });
  }, [cell, a, b]),
});

/** The first visible match is wholly on the first screen, the page not scrolled: seen without looking for it. */
const onFirstScreen = selector => ({
  name: `on the first screen: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) => {
    const r = els.map(e => e.getBoundingClientRect()).find(b => b.width > 0 && b.height > 0);
    if (!r) return [`nothing shown matches ${selector}`];
    return r.top >= 0 && r.bottom <= innerHeight + 1 ? [] : [`${selector} runs from ${Math.round(r.top)}px to ${Math.round(r.bottom)}px, past the window's ${innerHeight}px`];
  }, selector),
});

/**
 * The board's lanes fill the window's height: the closed strip sits at the bottom of the first screen (within 48px of
 * the window's edge), the lowest lanes reach down to it, and every lane has room to drop under its cards or scrolls on
 * its own (its cards are taller than it).
 */
const lanesFillTheHeight = {
  name: 'the lanes fill the height, a drop area in each',
  check: page => page.evaluate(() => {
    const strip = document.querySelector('.tide-kstrip')?.getBoundingClientRect();
    const lanes = [...document.querySelectorAll('.tide-kcol')].filter(l => l.getBoundingClientRect().height > 0);
    if (!strip || !lanes.length) return ['no lanes and strip shown'];
    const problems = [];
    if (strip.bottom < innerHeight - 48) problems.push(`the strip ends at ${Math.round(strip.bottom)}px, well above the window's bottom (${innerHeight}px): the lanes don't fill the height`);
    const lowest = Math.max(...lanes.map(l => l.getBoundingClientRect().bottom));
    if (strip.top - lowest > 24) problems.push(`the lanes stop ${Math.round(strip.top - lowest)}px above the strip`);
    lanes.forEach(l => {
      const cards = l.querySelector('.tide-kcol-cards'), drop = l.querySelector('.tide-kcol-drop')?.getBoundingClientRect();
      const scrolls = cards && cards.scrollHeight > cards.clientHeight + 1;
      if (!scrolls && !(drop && drop.height >= 40)) problems.push(`the ${l.dataset.status} lane has no room to drop under its cards`);
    });
    return problems;
  }),
};

/** The closed strip has a chip for each way of closing, each a drop target (a .tide-kchip with its status) and shown. */
const chipsAreDropTargets = {
  name: 'a drop target for each way of closing',
  check: page => page.evaluate(() => {
    const want = ['Done', 'Cancelled', 'NotApplicable'];
    const chips = [...document.querySelectorAll('.tide-kstrip .tide-kchip')].filter(c => c.getBoundingClientRect().height > 0);
    const have = chips.map(c => c.dataset.status);
    return want.every(w => have.includes(w)) ? [] : [`the strip's drop targets are ${have.join(', ') || 'none'}, not ${want.join(', ')}`];
  }),
};

/**
 * A card dragged onto each chip of the closed strip (as a mouse does it) asks how it closed, as a drop on that status
 * always has, naming the card and the status; Cancel leaves it where it was.
 */
const dropOnEachChip = {
  name: 'a card dropped on each chip asks how it closed',
  check: async page => {
    const problems = [];
    for (const [status, name] of [['Done', 'Done'], ['Cancelled', 'Cancelled'], ['NotApplicable', 'Not applicable']]) {
      const card = page.locator('.tide-kcard[draggable="true"]').first();
      if (!await card.count()) return ['no card the demo\'s analyst may move'];
      const number = (await card.locator('.tide-action-num').innerText()).trim();
      await card.dragTo(page.locator(`.tide-kchip[data-status="${status}"]`));
      const dialog = page.getByRole('dialog', { name: `Move ${number} to ${name}` });
      try { await dialog.waitFor({ timeout: 5000 }); } catch { problems.push(`dropping ${number} on ${name} didn't ask how it closed`); continue; }
      const dims = await page.evaluate(() => {
        const s = document.querySelector('.tide-modal-scrim'), r = s?.getBoundingClientRect();
        return !!s && r.width >= innerWidth - 20 && r.height >= innerHeight - 1 && getComputedStyle(s).backgroundColor !== 'rgba(0, 0, 0, 0)';
      });
      if (!dims) problems.push(`the ${name} dialog has no backdrop over the page`);
      await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
      await dialog.waitFor({ state: 'hidden', timeout: 5000 }).catch(() => problems.push(`the ${name} dialog didn't close on Cancel`));
    }
    return problems;
  },
};

const selectedAction = page => page.evaluate(() => {
  const rows = [...document.querySelectorAll('.tide-action-row')];
  return { at: rows.findIndex(r => r.classList.contains('selected')), focused: rows.indexOf(document.activeElement), row: new URL(location.href).searchParams.get('row') };
});

/**
 * The Actions list's keys: J and the down arrow move the selected row (it takes focus, and the address names it), K and
 * the up arrow move it back, and T typed in the search box is only typed: it takes nothing on.
 */
const actionKeys = {
  name: 'J and K move the selected action; T in a field is typing',
  check: async page => {
    const problems = [];
    await page.locator('.tide-action-row.selected').focus();
    for (const [key, at] of [['j', 1], ['ArrowDown', 2], ['k', 1], ['ArrowUp', 0]]) {
      await page.keyboard.press(key);
      await page.waitForTimeout(500);
      const now = await selectedAction(page);
      if (now.at !== at || now.focused !== at) problems.push(`after ${key} row ${now.at + 1} is selected and row ${now.focused + 1} has focus, not row ${at + 1}`);
      if ((now.row ?? '0') !== String(at)) problems.push(`after ${key} the address says row=${now.row}, not ${at}`);
    }
    const takes = await page.locator('.tide-row-take').count();
    const box = page.getByRole('searchbox', { name: 'Search actions' });
    await box.focus();
    await page.keyboard.press('t');
    await page.waitForTimeout(800);
    if (await page.locator('.tide-row-take').count() !== takes || await page.locator('.tide-toast').count()) problems.push('T typed in the search box took an action on');
    return problems;
  },
};

/** T on the selected row takes it on: the toast says so. */
const takeKey = {
  name: 'T takes the selected action on',
  check: async page => {
    const number = (await page.locator('.tide-action-row.selected .tide-action-num').innerText()).trim();
    await page.locator('.tide-action-row.selected').focus();
    await page.keyboard.press('t');
    const toast = page.getByText(`You took ${number} on.`);
    try { await toast.waitFor({ timeout: 5000 }); return []; } catch { return [`T on ${number} didn't take it on`]; }
  },
};

/** A row's Status menu from the keyboard: Enter on it opens its menu with focus on the first status, and Esc closes it
 * with focus back on the status. */
const statusMenuByKeyboard = {
  name: 'the Status menu opens and closes from the keyboard',
  check: async page => {
    const button = page.locator('.tide-status-menu').first();
    if (!await button.count()) return ['no row has a Status menu'];
    await button.focus();
    await page.keyboard.press('Enter');
    await page.waitForTimeout(500);
    const problems = [];
    const inMenu = await page.evaluate(() => !!document.activeElement?.closest('[role="menu"]'));
    if (!inMenu) problems.push('Enter on the status left focus outside its menu');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(500);
    if (await page.locator('.tide-menubutton-pop[role="menu"]').count()) problems.push('Esc left the menu open');
    if (!await page.evaluate(() => document.activeElement?.classList.contains('tide-status-menu'))) problems.push('Esc didn\'t put focus back on the status');
    return problems;
  },
};

/**
 * Beside the tabs of the row `tabs`, each of `selectors` sits on the tabs' centre line (its centre within 2px of the
 * first tab label's) and inside the tab row: not hanging below the row's bottom rule (the tabs' own bottom edge, where
 * their underline is drawn), nor scrolled or cut out of the window sideways.
 */
const besideTheTabs = (tabs, ...selectors) => ({
  name: `on the tabs' line: ${selectors.join(', ')}`,
  check: page => page.evaluate(([tabs, selectors]) => {
    const bar = document.querySelector(tabs), label = bar?.querySelector('.tide-tabbar-item');
    if (!bar || !label) return [`no tabs at ${tabs}`];
    const t = label.getBoundingClientRect(), row = bar.getBoundingClientRect(), mid = t.top + t.height / 2;
    return selectors.flatMap(s => {
      const r = document.querySelector(s)?.getBoundingClientRect();
      if (!r || !r.width) return [`${s} isn't shown`];
      const problems = [];
      const off = r.top + r.height / 2 - mid;
      if (Math.abs(off) > 2) problems.push(`${s}'s centre is ${off.toFixed(1)}px ${off > 0 ? 'below' : 'above'} the tab labels'`);
      if (r.bottom > row.bottom + 0.5) problems.push(`${s} hangs ${(r.bottom - row.bottom).toFixed(1)}px below the tab row's rule`);
      if (r.top < row.top - 0.5) problems.push(`${s} sticks ${(row.top - r.top).toFixed(1)}px above the tab row`);
      if (r.left < 0 || r.right > innerWidth) problems.push(`${s} is out of the window sideways`);
      return problems;
    });
  }, [tabs, selectors]),
});

/** The KEV flag is an outlined pill (a border, rounded): the same on the board's cards and the list's rows. */
const kevPill = selector => ({
  name: `KEV is a pill: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) => {
    const shown = els.filter(e => e.getBoundingClientRect().height > 0);
    if (!shown.length) return [`nothing shown matches ${selector}`];
    const cs = getComputedStyle(shown[0]);
    return parseFloat(cs.borderTopWidth) > 0 && parseFloat(cs.borderTopLeftRadius) >= 6 ? [] : [`${selector} has no outlined pill (border ${cs.borderTopWidth}, radius ${cs.borderTopLeftRadius})`];
  }, selector),
});

/**
 * Closing a card keeps the person's place: from its Move menu by keyboard (`how` 'menu'), or dragged onto a chip while
 * focus was on it ('drag'), then the outcome dialog's button: focus lands in the card that was next in its lane.
 */
const closeKeepsPlace = how => ({
  name: `after closing a card (${how}), focus is on the next card in its lane`,
  check: async page => {
    const lane = page.locator('.tide-kcol[data-status="Open"] .tide-kcard[draggable="true"]');
    if (await lane.count() < 2) return ['the Open lane has fewer than two cards to move'];
    const card = lane.first(), next = await lane.nth(1).getAttribute('data-id');
    if (how === 'menu') {
      await card.locator('.tide-kcard-move').focus();
      await page.keyboard.press('Enter');
      await card.getByRole('menuitem', { name: 'Cancelled' }).focus();
      await page.keyboard.press('Enter');
    } else {
      await card.locator('.tide-action-ref').focus();
      await card.dragTo(page.locator('.tide-kchip[data-status="Cancelled"]'));
    }
    const confirm = page.getByRole('button', { name: 'Move to Cancelled' });
    await confirm.waitFor({ timeout: 5000 });
    await confirm.focus();
    await page.keyboard.press('Enter');
    await page.waitForTimeout(1500);
    const at = await page.evaluate(() => {
      const el = document.activeElement;
      return el === document.body || !el ? 'the page body' : el.closest('.tide-kcard')?.dataset.id ?? `<${el.tagName.toLowerCase()} id="${el.id}">`;
    });
    return at === next ? [] : [`focus is on ${at}, not the next card ${next}`];
  },
});

/** Setup: every letter spaced wider, as a font wider than this machine's would set it (CI's): a rule that holds then
 * holds by the page's construction, not by pixels to spare. */
const wider = async page => { await page.addStyleTag({ content: '* { letter-spacing: .07em !important; }' }); };

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
    const shown = els.map(e => e.getBoundingClientRect()).filter(r => r.height > 0);
    if (!shown.length) return [`nothing shown matches ${selector}`];
    return shown.filter(r => r.top < bar - 1).map(r => `${selector} starts at ${Math.round(r.top)}px, under the top bar (to ${Math.round(bar)}px)`);
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
  await wider(page);
  const line = page.locator(group ? '.tide-triage-group' : '.tide-triage-row:not(.tide-triage-group):not(.tide-triage-member)', { has: page.locator('.tide-triage-flags .tide-badge-info') }).first();
  await line.locator(group ? '.tide-group-label' : '.tide-triage-meta > span').first().click();
  await page.locator('.tide-triage-decide .tide-triage-attach').waitFor();
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

/** The first visible match of `selector` starts no lower than `px` from the top of the page: what a page is for isn't
 * pushed down by headings above it. */
const startsAbove = (selector, px) => ({
  name: `starts above ${px}px: ${selector}`,
  check: page => page.evaluate(([selector, px]) => {
    const box = [...document.querySelectorAll(selector)].map(e => e.getBoundingClientRect()).find(r => r.width > 0 && r.height > 0);
    if (!box) return [`nothing shown matches ${selector}`];
    const top = Math.round(box.top + window.scrollY);
    return top <= px ? [] : [`${selector} starts at ${top}px, below ${px}px`];
  }, [selector, px]),
});

/** In each visible match of `card`, its first child starts within `px` of the card's top: no empty band above it. */
const headAtTop = (card, px) => ({
  name: `first line at the top: ${card}`,
  check: page => page.$$eval(card, (cards, [card, px]) => {
    const shown = cards.filter(e => e.getBoundingClientRect().height > 0);
    if (!shown.length) return [`nothing shown matches ${card}`];
    return shown.flatMap((c, i) => {
      const gap = Math.round(c.firstElementChild.getBoundingClientRect().top - c.getBoundingClientRect().top);
      return gap <= px ? [] : [`${card} #${i + 1}: its first line is ${gap}px below its top`];
    });
  }, [card, px]),
});

/** Pressing `button` puts exactly the text of `block` on the clipboard, its line breaks as LF (the page is opened with
 * clipboard permissions: `context` below). */
const copiesExactly = (button, block) => ({
  name: `copies exactly: ${block}`,
  check: async page => {
    const want = await page.$eval(block, el => el.textContent.replace(/\r\n?/g, '\n'));
    await page.evaluate(() => navigator.clipboard.writeText(''));
    await page.click(button);
    await page.waitForTimeout(400);
    const got = await page.evaluate(() => navigator.clipboard.readText());
    if (got.includes('\r')) return ['the clipboard has CR line breaks'];
    return got === want && want.length > 0 ? [] : [`the clipboard holds ${JSON.stringify(got.slice(0, 60))}, not the ${want.length} characters of ${block}`];
  },
});

/** Setup: every letter spaced wider, as a font wider than this machine's would set it (CI's), so a rule holds by the
 * page's construction rather than by pixels to spare. */
const wideFonts = async page => { await page.addStyleTag({ content: '* { letter-spacing: .07em !important; }' }); };

const clipboard = { permissions: ['clipboard-read', 'clipboard-write'] };

/** A rule run in the page: `fn(arg)` returns what's wrong (nothing, when it holds). */
const holds = (name, fn, arg) => ({ name, check: page => page.evaluate(fn, arg) });

/** Each item of the open menu is wholly visible: the point at each corner, just inside, is the item (or inside it),
 * not a box that clips it or something drawn over it. */
const menuItemsVisible = holds('the open menu\'s items are wholly visible', () => {
  const items = [...document.querySelectorAll('[role=menu] [role=menuitem]')];
  if (!items.length) return ['no open menu has items'];
  return items.flatMap(el => {
    const r = el.getBoundingClientRect();
    const corners = [[r.left + 2, r.top + 2], [r.right - 2, r.top + 2], [r.left + 2, r.bottom - 2], [r.right - 2, r.bottom - 2]];
    const hidden = corners.filter(([x, y]) => !el.contains(document.elementFromPoint(x, y)));
    return hidden.length ? [`"${el.innerText.trim()}" is cut off or covered (${hidden.length} of 4 corners)`] : [];
  });
});

/** The first visible match of `inner` is inside a match of `outer` that also holds the first visible match of `other`. */
const sameBox = (outer, inner, other) => holds(`${inner} and ${other} in one ${outer}`, ([outer, inner, other]) => {
  const shown = s => [...document.querySelectorAll(s)].find(e => e.getBoundingClientRect().height > 0);
  const a = shown(inner), b = shown(other);
  if (!a || !b) return [`nothing visible: ${!a ? inner : other}`];
  const box = a.closest(outer);
  return box && box.contains(b) ? [] : [`${inner} and ${other} aren't in one ${outer}`];
}, [outer, inner, other]);

/** The first visible match of `first` starts above the first visible match of `then`. */
const above = (first, then) => holds(`above: ${first} / ${then}`, ([first, then]) => {
  const top = s => [...document.querySelectorAll(s)].map(e => e.getBoundingClientRect()).find(r => r.height > 0)?.top;
  const a = top(first), b = top(then);
  if (a === undefined || b === undefined) return [`nothing visible: ${a === undefined ? first : then}`];
  return a < b ? [] : [`${first} (${Math.round(a)}px) isn't above ${then} (${Math.round(b)}px)`];
}, [first, then]);

/** No visible match of `selector` is inside a match of `outside`. */
const notInside = (selector, outside) => holds(`not inside ${outside}: ${selector}`, ([selector, outside]) => {
  const shown = [...document.querySelectorAll(selector)].filter(e => e.getBoundingClientRect().height > 0);
  if (!shown.length) return [`nothing shown matches ${selector}`];
  return shown.some(e => e.closest(outside)) ? [`${selector} is inside ${outside}`] : [];
}, [selector, outside]);

/** The visible text of the first match of `selector` matches `pattern`. */
const says = (selector, pattern) => holds(`says ${pattern}: ${selector}`, ([selector, source]) => {
  const el = [...document.querySelectorAll(selector)].find(e => e.getBoundingClientRect().height > 0);
  if (!el) return [`nothing shown matches ${selector}`];
  const text = el.innerText.replace(/\s+/g, ' ').trim();
  return new RegExp(source).test(text) ? [] : [`${selector} says "${text.slice(0, 80)}"`];
}, [selector, pattern.source]);

/** The first visible match of `selector` reaches within `px` of the window's bottom: a panel the height of the page. */
const reachesBottom = (selector, px) => holds(`reaches the bottom: ${selector}`, ([selector, px]) => {
  const r = [...document.querySelectorAll(selector)].map(e => e.getBoundingClientRect()).find(b => b.height > 0);
  if (!r) return [`nothing shown matches ${selector}`];
  return r.bottom >= window.innerHeight - px ? [] : [`${selector} ends at ${Math.round(r.bottom)}px, ${Math.round(window.innerHeight - r.bottom)}px above the window's bottom`];
}, [selector, px]);

/** `rule`, checked once the page has had `ms` more to settle: what the live page draws, after the prerendered one. */
const later = (rule, ms) => ({ name: rule.name, check: async page => { await page.waitForTimeout(ms); return rule.check(page); } });

/** Setup: wider letters, then the selected library entry's More menu opened. */
const openEntryMore = async page => {
  await wideFonts(page);
  await page.locator('.tide-library-entry .tide-menubutton-btn').first().click();
  await page.locator('[role=menu] [role=menuitem]').first().waitFor();
};

// The reading pane's open or closed is kept for the person, so the pages that press V sign in as someone of their own
// for this run (the dev sign-in's ?as=), and everyone else's Triage opens as it always does.
const run = Date.now().toString(36);

/** The first visible match of `selector` is `px` wide, give or take `slack`. */
const widthNear = (selector, px, slack) => holds(`about ${px}px wide: ${selector}`, ([selector, px, slack]) => {
  const r = [...document.querySelectorAll(selector)].map(e => e.getBoundingClientRect()).find(b => b.height > 0);
  if (!r) return [`nothing shown matches ${selector}`];
  return Math.abs(r.width - px) <= slack ? [] : [`${selector} is ${Math.round(r.width)}px wide, not about ${px}px`];
}, [selector, px, slack]);

/**
 * Tab from the selected row goes through the row's own controls and then into Triage's decision bar, its main button
 * first and the rest in the order they're shown (not past every other row's links and the reading pane on the way);
 * Shift+Tab from the bar's first button goes back to the row.
 */
const tabReachesBar = {
  name: 'Tab from the selected row reaches the decision bar, main button first',
  check: async page => {
    const where = () => page.evaluate(() => {
      const a = document.activeElement, r = a.getBoundingClientRect();
      return { inRow: !!a.closest('.tide-triage-row.selected'), inBar: !!a.closest('.tide-triage-decide'), primary: a.classList.contains('btn-primary'),
        x: Math.round(r.left), y: Math.round(r.top), text: (a.innerText || a.getAttribute('aria-label') || a.tagName).replace(/\s+/g, ' ').trim().slice(0, 40) };
    });
    await page.locator('.tide-triage-row.selected').focus();
    const seen = [];
    for (let i = 0; i < 30 && seen.length < 4; i++) {
      await page.keyboard.press('Tab');
      const at = await where();
      if (!at.inRow) seen.push(at);
    }
    if (!seen.length || !seen[0].inBar) return [`Tab from the selected row goes to "${seen[0]?.text}", not the decision bar`];
    if (!seen[0].primary) return [`the first of the bar Tab reaches is "${seen[0].text}", not its main button`];
    const bar = seen.filter(s => s.inBar);
    const backwards = bar.slice(1).filter((s, i) => s.y === bar[i].y && s.x <= bar[i].x);
    if (backwards.length) return [`Tab goes back along the bar's row: "${bar.map(s => s.text).join('", "')}"`];
    await page.locator('.tide-triage-decide .btn-primary').first().focus();
    await page.keyboard.press('Shift+Tab');
    const back = await where();
    return back.inRow ? [] : [`Shift+Tab from the bar's main button goes to "${back.text}", not back to the selected row`];
  },
};

/** Setup: the page scrolled well down, past the top of the list. */
async function scrollDown(page) {
  await page.mouse.wheel(0, 1500);
  await page.waitForTimeout(300);
}

// --- the pages: route, optional setup, widths (each 900px tall, or [width, height]) and rules ([minWidth, rule] from that
// width up) -------------------------------------------------------------------------------------------------------------

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
      visible('.tide-col-progress'), onTitleLine('.tide-table td:first-child', '.tide-action-num', '[title^="Escalated"]'),
      notClippedBelow('[role=group][aria-label="Views"]'), visible('.tide-chip-count'), fits('.tide-dt-scroll'), kevPill('.tide-action-row .tide-kev')] },
  // "Open here by default" (or "Opens here") and Save view sit on the view tabs' line, inside their row, as the design
  // has them: on the list and the board, at each width, and with wider letters.
  ...['list', 'board'].flatMap(layout => [false, true].map(wide => ({
    name: `actions-${layout}-view-tools${wide ? '-wide-letters' : ''}`, route: `/actions?view=open&layout=${layout}`, setup: wide ? wider : undefined,
    widths: [[1920, 1080], [1440, 900], [780, 900]],
    rules: [noSidewaysScroll, besideTheTabs('[role=group][aria-label="Views"]', '#view-default', '#save-view')],
  }))),
  // With wider letters (CI's fonts), the Progress column still leaves the title its line: the Escalated badge stays on it.
  { name: 'actions-list-wide-letters', route: '/actions?view=open&layout=list', setup: wider, widths: [[1920, 1080], [1440, 900]],
    rules: [noSidewaysScroll, visible('.tide-col-progress'), onTitleLine('.tide-table td:first-child', '.tide-action-num', '[title^="Escalated"]'), fits('.tide-dt-scroll')] },
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
  // The list's selected row and its keys, the Status menu from the keyboard, and T taking the selected row on.
  { name: 'actions-list-keys', route: '/actions?view=open&layout=list', widths: [[1440, 900]],
    rules: [visible('.tide-action-row.selected'), visible('.tide-actions-keys'), oneRow('.tide-actions-keys'), actionKeys, statusMenuByKeyboard] },
  { name: 'actions-list-take-key', route: '/actions?view=open&layout=list&assignee=none', widths: [[1440, 900]], rules: [takeKey] },
  // The four lanes share the width and fill the window's height, each with room to drop under its cards, and the closed
  // strip (a drop target for each way of closing) sits at the bottom of the first screen: on a laptop's window, a
  // smaller one and a narrow one (the lanes two by two), and with wider letters. Nothing scrolls sideways.
  ...['actions-board', 'actions-board-wide-letters'].map(name => ({
    name, route: '/actions?view=open&layout=board', setup: name.endsWith('letters') ? wider : undefined, widths: [[1920, 1080], [1440, 900], [780, 900]],
    rules: [noSidewaysScroll, fits('.tide-kanban'), visible('.tide-kcard'), oneRow('.tide-kcard-head'), fits('.tide-kcard-article'), kevPill('.tide-kcard .tide-kev'),
      onFirstScreen('.tide-kstrip'), lanesFillTheHeight, chipsAreDropTargets, [1440, oneRow('.tide-kstrip')], [1800, widerThan('.content', 1400)]],
  })),
  // A card dragged with the mouse onto each chip asks how it closed.
  { name: 'actions-board-drop', route: '/actions?view=open&layout=board', widths: [[1440, 900]], rules: [dropOnEachChip] },
  // A sweep's query is read whole, in the wide column: its long lines wrap, so nothing scrolls sideways inside its box.
  { name: 'action-hunt', route: '/actions/{huntAction}', widths: [1920, 1440],
    rules: [noSidewaysScroll, visible('.tide-action-main .tide-hunt pre.tide-query'), fits('.tide-hunt pre.tide-query'),
      visible('.tide-hunt-sweeping'), visible('[role=group][aria-label="Query language"]'), visible('#hunt-window'),
      // The language and the window on the tab row, at its right; the whole story's indicators on offer; Copy on the first screen.
      sameLine('.tide-drawer-tabs', '[role=group][aria-label="Query language"]'), sameLine('.tide-drawer-tabs', '#hunt-window'),
      visible('.tide-hunt-story'), startsAbove('.tide-hunt [data-copy-target]', 900),
      // A-1's query is KQL: the control says the language of the query on the action, on the live page too.
      later(says('.tide-drawer-head [aria-label="Query language"] [aria-pressed="true"]', '^KQL$'), 2500)] },
  // Triage: the keys on one line, each row's flags fitting its line, and the reading pane open by itself from 1400px.
  { name: 'triage', route: '/triage', widths: [1920, 1440, 780],
    rules: [noSidewaysScroll, fits('.tide-page-head'), visible('.tide-triage-keys'), oneRow('.tide-triage-keys'), visible('.tide-triage-row.selected'), fits('.tide-triage-row'),
      fits('.tide-triage-flags'), [1400, visible('.tide-reading')]] },
  { name: 'triage-narrow', route: '/triage', widths: [1300, 780], rules: [hidden('.tide-reading')] },
  // The reading pane open by itself stays below the top bar as the page scrolls.
  { name: 'triage-scrolled', route: '/triage', setup: scrollDown, widths: [1440], rules: [visible('.tide-reading'), belowTopBar('.tide-reading')] },
  // The decision bar, as designed: the list a column of about 440px, the selected item's buttons in a bar at the foot of
  // the reading pane (not in the row), opening with the line that says why its main button is the main one; the bar's
  // buttons one row, by construction (letters spaced wider); Tab from the row goes to them.
  { name: 'triage-decision', route: '/triage', setup: wider, widths: [[1920, 1080], [1440, 900]],
    rules: [noSidewaysScroll, widthNear('.tide-triage-layout > .tide-card:first-child', 440, 16), visible('.tide-reading .tide-triage-decide'),
      sameBox('.tide-reading', '.tide-triage-decide', '.tide-reading-title'), above('.tide-reading-title', '.tide-triage-decide'),
      above('.tide-triage-why', '.tide-triage-acts'), oneRow('.tide-triage-decide .tide-triage-acts'), fits('.tide-triage-decide'), fits('.tide-triage-acts'),
      hidden('.tide-triage-row.selected .tide-triage-decide'), fits('.tide-triage-row'), tabReachesBar] },
  // A covered row's Attach and Promote, and a covered group's Attach all and Promote as one, share the bar's one row.
  { name: 'triage-covered', route: '/triage', setup: selectCovered(), widths: [1440, 1920],
    rules: [visible('.tide-reading'), sameLine('.tide-triage-decide .tide-triage-attach', '.tide-triage-decide .tide-triage-promote'),
      oneRow('.tide-triage-decide .tide-triage-acts'), fits('.tide-triage-decide'), fits('.tide-triage-row.selected')] },
  // A covered group's Attach all and Promote as one share a line too (the demo's covered group: Demo:TriageFiller below).
  { name: 'triage-covered-group', route: '/triage', setup: selectCovered(true), widths: [1440, 1920],
    rules: [visible('.tide-reading'), sameLine('.tide-triage-decide .tide-triage-attach', '.tide-triage-decide .tide-triage-promote'),
      oneRow('.tide-triage-decide .tide-triage-acts'), fits('.tide-triage-decide'), fits('.tide-triage-group.selected')] },
  // Below 1400px the pane that opened by itself is hidden and the decision stays in the selected row, reason and all.
  { name: 'triage-decision-narrow', route: '/triage', setup: wider, widths: [1000, 780],
    rules: [noSidewaysScroll, hidden('.tide-reading'), visible('.tide-triage-row.selected .tide-triage-decide'), visible('.tide-triage-row.selected .tide-triage-why'),
      visible('.tide-triage-row.selected .tide-triage-promote'), fits('.tide-triage-row.selected')] },
  // V closes the pane that opened by itself, and opens it again; on a narrow window, where it's hidden, V opens it.
  // Held down, V is one press: the keyboard's repeats don't open and close the pane over and over.
  { name: 'triage-v-held', route: `/triage?as=v4${run}`, setup: holdV, widths: [1440], rules: [hidden('.tide-reading')] },
  // Closed by the person, the pane takes the decision bar with it: the buttons are in the selected row again.
  { name: 'triage-v-closes', route: `/triage?as=v1${run}`, setup: pressV(1), widths: [1440],
    rules: [hidden('.tide-reading'), visible('.tide-triage-row.selected .tide-triage-decide'), visible('.tide-triage-row.selected .tide-triage-promote')] },
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
  // Our stack: a product no team owns says so in its Team cell, as the No owning team filter does.
  // The filters above the table, outside its card, with a team to choose and the count said as the order; Right now the
  // fifth column; and on a narrow window each row's cells stacked, so the table never scrolls sideways inside its card.
  { name: 'stack', route: '/stack', setup: wideFonts, widths: [1920, 1440, 780],
    rules: [noSidewaysScroll, visible('.tide-table .tide-badge-off'), notInside('.tide-filterbar', '.tide-card'), visible('#stack-team'),
      says('.tide-filterbar .tide-dt-count', 'products · most pressing first'), [1000, says('.tide-stack-table thead th:nth-child(5)', '^Right now$')],
      fits('.tide-stack-list')] },
  // The Library: one header, the one search on the tab row beside the tabs, and the list high on the page; its findings,
  // tab by tab, for a search's words.
  { name: 'library', route: '/library/queries', setup: wideFonts, widths: [1920, 1440, 780],
    rules: [noSidewaysScroll, visible('#library-find'), [1440, sameLine('.tide-library-tabrow .tide-tabbar-page', '#library-find')],
      [1440, closeBelow('.tide-library-list', '.tide-library-tabrow', 120)], visible('.tide-library-group'),
      // The entry's Copy, Edit and More at its top right, the entry the height of the window, and Show retired a checkbox.
      [1000, sameLine('.tide-library-entry h3', '.tide-library-entry [data-copy-target]')], [1440, reachesBottom('.tide-library-entry', 24)],
      visible('#sq-retired'), hidden('.form-switch #sq-retired')] },
  // The entry's More menu opens wholly in view: Retire and Delete aren't cut off by the scrolling detail panel.
  { name: 'library-more', route: '/library/queries', setup: openEntryMore, widths: [1920, 1440], rules: [menuItemsVisible] },
  { name: 'library-search', route: '/library/queries?q=sweep', setup: wideFonts, widths: [1440, 780], rules: [noSidewaysScroll, visible('#library-find'), visible('.tide-library-list')] },
  // Search for an indicator: the hunt query wraps rather than scrolling sideways, Copy puts exactly its text on the
  // clipboard, and the cards' headings start at their tops.
  { name: 'search-lookup', route: '/search?q=update-check.example.net', setup: wideFonts, widths: [1920, 1440, 780], context: clipboard,
    rules: [noSidewaysScroll, fits('.tide-lookup-query'), headAtTop('.tide-lookup .tide-card', 24), visible('.tide-lookup-articles'),
      copiesExactly('[data-copy-target="lookup-query"]', '#lookup-query'),
      // The language as the action page's segmented control; the article, the actions raised and "Seen with" in one card.
      visible('#lookup-query-tools [role=group][aria-label="Query language"]'), sameBox('.tide-card', '.tide-lookup-articles', '#lookup-actions'),
      sameBox('.tide-card', '#lookup-actions', '#lookup-seen-with'), above('#lookup-actions', '#lookup-seen-with')] },
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
  // Closing a card from its Move menu, or by dragging it while it had focus, keeps the person's place in the lane.
  // Last, as each closes one of the demo's actions.
  { name: 'actions-board-close-menu', route: '/actions?view=open&layout=board', widths: [[1440, 900]], rules: [closeKeepsPlace('menu')] },
  { name: 'actions-board-close-drag', route: '/actions?view=open&layout=board', widths: [[1440, 900]], rules: [closeKeepsPlace('drag')] },
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
      for (const size of p.widths) {
        const [width, height] = Array.isArray(size) ? size : [size, 900];
        const rules = p.rules.map(r => Array.isArray(r) ? r : [0, r]).filter(([minWidth]) => width >= minWidth).map(([, r]) => r);
        let context;
        try {
          let page;
          ({ context, page } = await openPage(browser, { ...p, viewport: { width, height } }, demo, p.context));
          const before = problems.length;
          for (const rule of rules) problems.push(...(await rule.check(page)).map(f => `${p.name} at ${width}px: ${f}`));
          console.log(`  ${p.name} at ${width}x${height}: ${rules.length - (problems.length - before)}/${rules.length} rules hold`);
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
