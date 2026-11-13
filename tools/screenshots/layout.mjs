// Checks TIDE's layout in a real browser: rows of controls that must stay one line, columns that must line up, and
// pages that mustn't scroll sideways or let text spill out of its box, each at the widths it's used at (README.md says
// why, and how to add a page or a rule).
//
//   node tools/screenshots/layout.mjs                build, seed a demo database, start TIDE, check every page
//   ONLY=slide-article node tools/screenshots/layout.mjs                 just these pages
//   REUSE=1 / BASE_URL=http://localhost:5112 node ...                    as capture.mjs
//
// Each failure names the page, the width and the rule. Add a page or a rule below; README.md says how.

import { writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';
import { idle, openPage, pick, readDemo, settle, startTide, toArticleSlide, watch } from './tide.mjs';

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

/** On a narrow window the board's lanes stay tall enough to read a card in (8rem of each), the page scrolling instead. */
const lanesTallEnough = {
  name: 'each lane shows at least 8rem of its cards',
  check: page => page.evaluate(() => {
    const lanes = [...document.querySelectorAll('.tide-board .tide-kcol-cards')].filter(l => l.getClientRects().length);
    if (!lanes.length) return ['no lanes shown'];
    const rem = parseFloat(getComputedStyle(document.documentElement).fontSize);
    return lanes.flatMap(l => l.clientHeight >= 8 * rem - 1 ? []
      : [`the ${l.closest('.tide-kcol')?.dataset.status} lane shows ${l.clientHeight}px of its cards`]);
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

/**
 * Each chip on the closed strip is a link as well as a drop target: to Recently closed on the list at its status and the
 * strip's reach, named for what it shows, not itself draggable, and able to take focus from the keyboard.
 */
const chipsAreLinks = {
  name: 'each closed chip links to the actions it counts',
  check: page => page.evaluate(() => {
    const problems = [];
    for (const [status, key] of [['Done', 'done'], ['Cancelled', 'cancelled'], ['NotApplicable', 'notapplicable']]) {
      const link = document.querySelector(`.tide-kstrip .tide-kchip[data-status="${status}"] a.tide-kchip-link`);
      if (!link || !link.getBoundingClientRect().height) { problems.push(`the ${status} chip has no link shown`); continue; }
      const q = new URL(link.href).searchParams;
      if (q.get('view') !== 'closed' || q.get('layout') !== 'list' || q.get('status') !== key || !/^(7|30)$/.test(q.get('closed') ?? ''))
        problems.push(`the ${status} chip links to ?${q}, not Recently closed at ${key} within the strip's reach`);
      const name = link.getAttribute('aria-label') ?? '';
      if (!/: \d+ closed in the last (7|30) days, show them$/.test(name)) problems.push(`the ${status} link is named "${name}"`);
      if (link.getAttribute('draggable') !== 'false') problems.push(`the ${status} link can itself be dragged`);
      if (link.tabIndex < 0) problems.push(`the ${status} link is out of the Tab order`);
      link.focus();
      if (document.activeElement !== link) problems.push(`the ${status} link can't take focus`);
    }
    return problems;
  }),
};

/** A click on the Done chip opens Recently closed on the list, at Done within the strip's week: every row Done. */
const chipLinkOpensTheList = {
  name: 'a closed chip opens its actions on the list',
  check: async page => {
    await page.locator('.tide-kchip[data-status="Done"] a.tide-kchip-link').click();
    try { await page.waitForURL(/view=closed/, { timeout: 5000 }); } catch { return ['a click on the Done chip went nowhere']; }
    await page.locator('.tide-action-row, .tide-empty').first().waitFor({ timeout: 5000 }).catch(() => {});
    return page.evaluate(() => {
      const problems = [], q = new URL(location.href).searchParams;
      if (q.get('layout') !== 'list' || q.get('status') !== 'done' || q.get('closed') !== '7') problems.push(`it opened ?${q}`);
      const current = document.querySelector('[role=group][aria-label="Views"] [aria-current="page"], [role=group][aria-label="Views"] .active');
      if (current && !/Recently closed/.test(current.textContent)) problems.push(`the ${current.textContent.trim()} tab is shown, not Recently closed`);
      if (!/Closed in the last 7 days/.test(document.body.innerText)) problems.push('the reach isn\'t shown as a filter');
      const rows = [...document.querySelectorAll('.tide-action-row')];
      const other = rows.filter(r => !/\bDone\b/.test(r.innerText));
      if (other.length) problems.push(`${other.length} of its ${rows.length} rows aren't Done`);
      return problems;
    });
  },
};

/** A closed action's rail on the list offers Reopen (to whoever may move it: the demo's analyst), named for the action. */
const reopenNamed = {
  name: 'Reopen is named for the action',
  check: page => page.evaluate(() => {
    const name = document.querySelector('#tide-rail .tide-rail-reopen')?.getAttribute('aria-label') ?? '';
    return /^Reopen A-\d+$/.test(name) ? [] : [`Reopen is named "${name}"`];
  }),
};

const selectedAction = page => page.evaluate(() => {
  const rows = [...document.querySelectorAll('.tide-action-row')];
  const at = rows.findIndex(r => r.classList.contains('selected')), params = new URL(location.href).searchParams;
  const number = rows[at]?.querySelector('a[href^="actions/"]:not(.tide-kcard-parent)')?.getAttribute('href').match(/\d+/)?.[0];
  return { at, focused: rows.indexOf(document.activeElement), row: params.get('row'), card: params.get('card'), number };
});

/**
 * The Actions list's keys: J and the down arrow move the selected row (it takes focus, and the address names its action),
 * K and the up arrow move it back, and T typed in the search box is only typed: it takes nothing on.
 */
const actionKeys = {
  name: 'J and K move the selected action; T in a field is typing',
  check: async page => {
    const problems = [];
    await page.locator('.tide-action-row.selected').focus();
    for (const [key, at] of [['j', 1], ['ArrowDown', 2], ['k', 1], ['ArrowUp', 0]]) {
      await page.keyboard.press(key);
      await idle(page);
      const now = await selectedAction(page);
      if (now.at !== at || now.focused !== at) problems.push(`after ${key} row ${now.at + 1} is selected and row ${now.focused + 1} has focus, not row ${at + 1}`);
      // The address names the selected action by its number (the first row is left out), not by its place.
      if (now.row !== null || now.card !== (at ? now.number : null)) problems.push(`after ${key} the address says ${new URLSearchParams({ ...(now.row && { row: now.row }), ...(now.card && { card: now.card }) })}, not card=${at ? now.number : '(none)'}`);
    }
    const takes = await page.locator('.tide-row-take').count();
    const box = page.getByRole('searchbox', { name: 'Search actions' });
    await box.focus();
    await page.keyboard.press('t');
    // A timer, not a condition: the search runs once typing has paused for 300ms (the Actions page's TypingPause).
    await page.waitForTimeout(300);
    await idle(page);
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
    await idle(page);
    const problems = [];
    const inMenu = await page.evaluate(() => !!document.activeElement?.closest('[role="menu"]'));
    if (!inMenu) problems.push('Enter on the status left focus outside its menu');
    await page.keyboard.press('Escape');
    await idle(page);
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
 * focus was on it ('drag') or after a click on it ('click-drag'), then the outcome dialog's button: focus lands in the card that was next in its lane.
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
    } else if (how === 'click-drag') {
      // A click on the card itself (not a link or button in it) first, as the mouse selects one.
      await card.locator('.tide-kcard-head').click({ position: { x: 4, y: 4 } });
      await card.dragTo(page.locator('.tide-kchip[data-status="Cancelled"]'));
    } else {
      await card.locator('.tide-action-ref').focus();
      await card.dragTo(page.locator('.tide-kchip[data-status="Cancelled"]'));
    }
    const confirm = page.getByRole('button', { name: 'Move to Cancelled' });
    await confirm.waitFor({ timeout: 5000 });
    await confirm.focus();
    await page.keyboard.press('Enter');
    await idle(page);
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
      await idle(page);
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
    await idle(page);
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
  await page.reload({ waitUntil: 'load' });
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
  const line = page.locator(group ? '.tide-triage-group' : '.tide-triage-row:not(.tide-triage-group):not(.tide-triage-member)', { has: page.locator('.tide-triage-cov') }).first();
  await line.locator(group ? '.tide-group-label' : '.tide-triage-meta > span').first().click();
  await page.locator('.tide-triage-decide .tide-triage-attach').waitFor();
};

/** No visible match of `selector` overlaps the first visible match of `other` sideways: two regions side by side. */
const besides = (selector, other) => ({
  name: `beside ${other}: ${selector}`,
  check: page => page.evaluate(([selector, other]) => {
    const box = s => [...document.querySelectorAll(s)].map(e => e.getBoundingClientRect()).find(r => r.width > 0 && r.height > 0);
    const a = box(selector), b = box(other);
    if (!a || !b) return [`nothing visible: ${!a ? selector : other}`];
    return a.right <= b.left + 1 || b.right <= a.left + 1 ? [] : [`${selector} (to ${Math.round(a.right)}px) runs under ${other} (from ${Math.round(b.left)}px)`];
  }, [selector, other]),
});

/** Each visible match of `selector` is at most `most` lines of text tall (one by default): it wraps no further. */
const oneLine = (selector, most = 1) => ({
  name: `${most === 1 ? 'one line' : `at most ${most} lines`}: ${selector}`,
  check: page => page.$$eval(selector, (els, [selector, most]) => els.flatMap((el, i) => {
    const r = el.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) return [];
    const s = getComputedStyle(el), line = parseFloat(s.lineHeight) || parseFloat(s.fontSize) * 1.5;
    return r.height < line * (most + .5) ? [] : [`${selector} #${i + 1}: "${el.textContent.trim().slice(0, 50)}" is on ${Math.round(r.height / line)} lines`];
  }), [selector, most]),
});

/** No visible match of `selector` draws a box of its own (a border or a background): flat, as a section in the rail. */
const flat = selector => ({
  name: `flat: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) => els.flatMap((el, i) => {
    const s = getComputedStyle(el);
    const box = ['Left', 'Right', 'Bottom'].some(side => parseFloat(s[`border${side}Width`]) > 0) || !/rgba\(0, 0, 0, 0\)|transparent/.test(s.backgroundColor);
    return el.getBoundingClientRect().height > 0 && box ? [`${selector} #${i + 1} is drawn as a card (border or background)`] : [];
  }), selector),
});

/** The first match of `selector` has `name` set to `value` (or, with null, doesn't have it). */
const attr = (selector, name, value) => ({
  name: `${name}=${value}: ${selector}`,
  check: page => page.$eval(selector, (el, [name, value]) => el.getAttribute(name) === value ? [] : [`its ${name} is ${el.getAttribute(name)}, not ${value}`], [name, value]),
});

/** The children of each visible match of `selector` are stacked, one under another, not side by side. */
const stacked = selector => ({
  name: `stacked: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) => els.flatMap((el, i) => {
    const kids = [...el.children].map(c => c.getBoundingClientRect()).filter(r => r.width > 0 && r.height > 0);
    return kids.every((r, k) => k === 0 || r.top >= kids[k - 1].bottom - 1) ? [] : [`${selector} #${i + 1}: its parts sit side by side`];
  }), selector),
});

/** Each visible match of `selector`, given focus from the keyboard, draws a focus ring (an outline or a box shadow). */
const ringOnFocus = selector => ({
  name: `focus ring: ${selector}`,
  check: async page => {
    const found = [];
    for (const el of await page.$$(selector)) {
      if (!(await el.isVisible())) continue;
      // A key first, so the browser treats the focus that follows as the keyboard's (:focus-visible).
      await page.keyboard.press('Shift');
      await el.focus();
      await idle(page);
      const ring = await el.evaluate(e => {
        const s = getComputedStyle(e);
        // A shadow counts when it's drawn: a colour that isn't transparent, and a spread.
        const shadow = /^(rgba?\([^)]*\)) [\d.]+px [\d.]+px [\d.]+px ([\d.]+)px/.exec(s.boxShadow);
        return (s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0)
          || (!!shadow && parseFloat(shadow[2]) > 0 && !/rgba\([^)]*,\s*0\)$/.test(shadow[1]));
      });
      if (!ring) found.push(`${selector}: no focus ring when focused from the keyboard`);
    }
    return found;
  },
});

/** Focus is inside a match of `selector` (after a button in it did its work), not dropped to the page. */
const focusIn = selector => ({
  name: `focus in: ${selector}`,
  check: page => page.evaluate(selector => {
    const at = document.activeElement;
    return at && at !== document.body && [...document.querySelectorAll(selector)].some(box => box.contains(at)) ? []
      : [`focus is on ${at === document.body ? 'the page (<body>)' : at?.outerHTML.slice(0, 80)}, not in ${selector}`];
  }, selector),
});

/** `key` (Tab, or Shift+Tab) pressed `times` from where focus is: it never lands outside `selector`, nor on <body> (where
 *  focus is once it has left the document for the browser's own controls): it wraps within, as a dialog's does. For an
 *  overlay that must hold focus. Run last on its page: it moves focus. */
const tabStaysIn = (selector, times, key = 'Tab') => ({
  name: `${key} stays in: ${selector}`,
  check: async page => {
    const out = new Set();
    for (let i = 0; i < times; i++) {
      await page.keyboard.press(key);
      const where = await page.evaluate(selector => {
        const at = document.activeElement;
        if (!at || at === document.body) return 'the page (<body>)';
        if (at.closest(selector)) return null;
        return `${at.tagName.toLowerCase()} "${(at.getAttribute('aria-label') || at.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 40)}"`;
      }, selector);
      if (where) out.add(where);
    }
    return [...out].map(w => `${key} reached ${w}, outside ${selector}`);
  },
});

/** Tab and Shift+Tab each stay in `selector`, `times` presses apiece. Run last on its page. */
const tabWraps = (selector, times) => [tabStaysIn(selector, times), tabStaysIn(selector, times, 'Shift+Tab')];

/** No two visible matches of `selector` overlap: controls in a row that each keep their own room. */
const noOverlap = selector => ({
  name: `no overlap: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) => {
    const shown = els.filter(e => !els.some(o => o !== e && o.contains(e)))
      .map(e => ({ e, r: e.getBoundingClientRect() })).filter(({ r }) => r.width > 0 && r.height > 0);
    const name = e => (e.getAttribute('aria-label') || e.textContent || e.tagName).trim().replace(/\s+/g, ' ').slice(0, 24);
    const found = [];
    for (let i = 0; i < shown.length; i++) for (let j = i + 1; j < shown.length; j++) {
      const [a, b] = [shown[i].r, shown[j].r];
      if (a.left < b.right - 1 && b.left < a.right - 1 && a.top < b.bottom - 1 && b.top < a.bottom - 1)
        found.push(`"${name(shown[i].e)}" (${Math.round(a.left)}–${Math.round(a.right)}px) overlaps "${name(shown[j].e)}" (${Math.round(b.left)}–${Math.round(b.right)}px)`);
    }
    return found;
  }, selector),
});

/** Each visible match of `selector` follows words on its line inside `within`: inline, never on a line of its own. */
const inlineIn = (selector, within) => ({
  name: `inline in ${within}: ${selector}`,
  check: page => page.$$eval(selector, (els, [selector, within]) => els.flatMap((el, i) => {
    const r = el.getBoundingClientRect(), box = el.closest(within)?.getBoundingClientRect();
    if (r.height === 0 || !box) return [];
    return r.left - box.left > 8 ? [] : [`${selector} #${i + 1} starts a line of its own in ${within}`];
  }), [selector, within]),
});

/** Focus is on the first match of `selector`. */
const focusOn = selector => ({
  name: `focus on: ${selector}`,
  check: page => page.evaluate(selector => {
    const at = document.activeElement, want = document.querySelector(selector);
    return want && at === want ? []
      : [`focus is on ${at === document.body ? 'the page (<body>)' : at?.outerHTML.slice(0, 90)}, not ${selector}`];
  }, selector),
});

/** Every visible match of `selector` is beside (not over) the first visible match of `other`. */
const allBesides = (selector, other) => ({
  name: `all beside ${other}: ${selector}`,
  check: page => page.evaluate(([selector, other]) => {
    const b = [...document.querySelectorAll(other)].map(e => e.getBoundingClientRect()).find(r => r.width > 0 && r.height > 0);
    const items = [...document.querySelectorAll(selector)].map(e => e.getBoundingClientRect()).filter(r => r.width > 0 && r.height > 0);
    if (!b || !items.length) return [`nothing visible: ${!b ? other : selector}`];
    return items.filter(a => !(a.right <= b.left + 1 || b.right <= a.left + 1))
      .map(a => `${selector} (${Math.round(a.left)}–${Math.round(a.right)}px) sits over ${other} (from ${Math.round(b.left)}px)`);
  }, [selector, other]),
});

/**
 * The open overlay's backdrop dims everything under it: the average brightness of each patch, measured from screenshots
 * with the rail closed and open, drops by at least `percent`. The patches: the page between the sidebar and the rail,
 * and, with `all`, the left navigation, the top bar beside the rail and the strip of top bar above it, wherever each is
 * shown. Run on a page whose setup opened the overlay; it closes it, measures, and opens it again.
 */
const dims = (percent, all = false) => ({
  name: `backdrop dims ${all ? 'everything under it' : 'the page'} by ${percent}%`,
  check: async page => {
    const patches = await page.evaluate(all => {
      const box = s => [...document.querySelectorAll(s)].map(e => e.getBoundingClientRect()).find(r => r.width > 0 && r.height > 0);
      const side = box('.sidebar'), bar = box('.top-row'), rail = document.getElementById('tide-rail').getBoundingClientRect();
      const left = side && side.right < rail.left ? side.right : 0;
      const width = Math.max(40, Math.min(200, rail.left - left - 20));
      const found = { 'the page': { x: left + 10, y: 300, width, height: 200 } };
      if (!all) return found;
      // A strip of the navigation's own colour, beside its links' text (which a backdrop of its colour would still dim).
      if (side && side.width > 40 && side.right < rail.left) found['the left navigation'] = { x: side.right - 8, y: 300, width: 4, height: 200 };
      if (bar && rail.top >= bar.bottom - 1) {
        const h = Math.max(8, Math.min(30, bar.height - 10));
        found['the top bar'] = { x: left + 10, y: bar.top + 5, width, height: h };
        found['the top bar above the rail'] = { x: rail.left + 4, y: bar.top + 5, width: Math.max(20, Math.min(100, rail.width - 8)), height: h };
      }
      return found;
    }, all);
    const names = Object.keys(patches);
    const shot = async () => Promise.all(names.map(async n => (await page.screenshot({ clip: patches[n] })).toString('base64')));
    await page.keyboard.press('Escape');
    await idle(page);
    const closed = await shot();
    await page.locator('#tide-rail-toggle').click();
    await idle(page);
    const open = await shot();
    // Decoded in a page of its own: the browser reads PNGs, so no library is needed.
    const scratch = await page.context().newPage();
    try {
      const light = await scratch.evaluate(async shots => Promise.all(shots.map(async s => {
        const img = new Image();
        img.src = `data:image/png;base64,${s}`;
        await img.decode();
        const c = Object.assign(document.createElement('canvas'), { width: img.width, height: img.height });
        const g = c.getContext('2d');
        g.drawImage(img, 0, 0);
        const d = g.getImageData(0, 0, c.width, c.height).data;
        let sum = 0;
        for (let i = 0; i < d.length; i += 4) sum += .2126 * d[i] + .7152 * d[i + 1] + .0722 * d[i + 2];
        return sum / (d.length / 4);
      })), [...closed, ...open]);
      return names.flatMap((n, i) => {
        const [a, b] = [light[i], light[i + names.length]];
        const drop = (a - b) / Math.max(a, 1) * 100;
        return drop >= percent ? [] : [`${n} under the open overlay is ${drop.toFixed(1)}% darker (${a.toFixed(0)} to ${b.toFixed(0)}), under ${percent}%`];
      });
    } finally {
      await scratch.close();
    }
  },
});

/** While the overlay is open, the top bar and the left navigation are inert: out of the tab order and unclickable. */
const underIsInert = {
  name: 'the top bar and the navigation are inert under the overlay',
  check: page => page.evaluate(() => ['.top-row', '.sidebar'].filter(s => document.querySelector(s)?.closest('[inert]') === null)
    .map(s => `${s} isn't inert while the overlay is open`)),
};

/** The sidebar's menu fits the window's height without scrolling (a short window tightens the links, not the list). */
const navFits = {
  name: "the sidebar's menu fits without scrolling",
  check: page => page.$eval('ul.tide-nav', ul => ul.scrollHeight <= ul.clientHeight + 1 ? []
    : [`the menu scrolls: ${ul.scrollHeight}px of ${ul.querySelectorAll('a').length} links in ${ul.querySelectorAll('.tide-nav-group').length} groups, in ${ul.clientHeight}px`]),
};

/** Every link in the sidebar is at least 24px tall (WCAG 2.5.8's target size), however short the window. */
const navTargets = {
  name: "the sidebar's links are at least 24px tall",
  check: page => page.$$eval('#tide-nav-links a', links => {
    const shown = links.filter(a => a.getBoundingClientRect().height > 0);
    if (!shown.length) return ['no sidebar link is shown'];
    return shown.filter(a => a.getBoundingClientRect().height < 24)
      .map(a => `${a.getAttribute('href') || 'today'} is ${a.getBoundingClientRect().height.toFixed(1)}px tall`);
  }),
};

/** Where the menu still scrolls (a very short window), its scrollbar is the thin one in the sidebar's colours, and the
 *  current page's link is within the part of the menu that shows. */
const navScrollsToActive = {
  name: "the sidebar's menu scrolls thin, its current link in view",
  check: page => page.$eval('ul.tide-nav', ul => {
    const problems = [];
    if (ul.scrollHeight <= ul.clientHeight + 1) problems.push(`the menu doesn't scroll at this height (${ul.scrollHeight}px in ${ul.clientHeight}px): the rule tests nothing`);
    const width = getComputedStyle(ul).scrollbarWidth;
    if (width !== 'thin') problems.push(`the menu's scrollbar-width is ${width}, not thin`);
    const a = ul.querySelector('a.active, a[aria-current="page"]');
    if (!a) return [...problems, 'no link is marked as the current page'];
    const r = a.getBoundingClientRect(), box = ul.getBoundingClientRect();
    if (r.top < box.top - 1 || r.bottom > box.bottom + 1) problems.push(`the current link (${a.getAttribute('href')}) is at ${Math.round(r.top)}-${Math.round(r.bottom)}px, outside the menu's ${Math.round(box.top)}-${Math.round(box.bottom)}px`);
    if (window.scrollY !== 0) problems.push(`the page scrolled to ${window.scrollY}px`);
    return problems;
  }),
};

/** Setup: the sidebar collapsed to icons. */
const collapseNav = async page => { await page.click('#nav-collapse-toggle'); await idle(page); };

/** Setup: the menu scrolled to its top, then the last page in it (Administration) opened by navigation, not a load. */
const navigateToAdmin = async page => {
  await page.$eval('ul.tide-nav', ul => { ul.scrollTop = 0; });
  await page.evaluate(() => window.Blazor.navigateTo('admin'));
  await page.waitForURL(/\/admin/);
  await idle(page);
};

/** Setup: every letter spaced wider, as a font wider than this machine's would set it (see selectCovered). */
// LETTER_SPACING=.12em widens it further by hand, near what a Linux runner's fonts do.
const wideFont = async page => { await page.addStyleTag({ content: `* { letter-spacing: ${process.env.LETTER_SPACING ?? '.07em'} !important; }` }); };

/** Setup: Today, signed in as `people[width]` (a name, and `&roles=…` when not the dev sign-in's) with what's new, tips
 *  and the tour asked for (`?onboarding=…`; off on the demo otherwise), then `keys`; wider letters. `how` is `on` (a new
 *  name takes the first-sign-in tour) or `returning` (someone who used TIDE before: what's new opens). */
const onboardingAs = (how, people, ...keys) => async page => {
  await page.goto(new URL(`./?as=${people[page.viewportSize().width]}&onboarding=${how}`, page.url()).href, { waitUntil: 'load' });
  await idle(page);
  for (const key of keys) { await page.keyboard.press(key); await idle(page); }
  await wideFont(page);
};

/** Setup: What's new over Today, as someone who used TIDE before. */
const whatsNewAs = (people, ...keys) => onboardingAs('returning', people, ...keys);

/** Setup: as whatsNewAs, with What's new closed (Escape), so Today's tip shows; then `keys`. */
const tipAs = (people, ...keys) => whatsNewAs(people, 'Escape', ...keys);

/** Setup: a first-ever sign-in, so the tour opens on Today at its first step; then `keys`. */
const tourAs = (people, ...keys) => onboardingAs('on', people, ...keys);

/** Setup: as tourAs, then a mouse click at the middle of `selector`, as on the page under the step. */
const tourClick = (people, selector) => async page => {
  await tourAs(people)(page);
  const box = await page.locator(selector).first().boundingBox();
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await idle(page);
};

/** Setup: someone who used TIDE before, on Help, takes the tour from there, then ends it at once with Escape. */
const tourFromHelp = people => async page => {
  await page.goto(new URL(`./help?as=${people[page.viewportSize().width]}&onboarding=returning`, page.url()).href, { waitUntil: 'load' });
  await idle(page);
  if (await page.locator('.tide-whatsnew').count()) { await page.keyboard.press('Escape'); await idle(page); }
  await page.locator('#help-take-tour').click();
  await idle(page);
  await page.keyboard.press('Escape');
  await idle(page);
};

/** Setup: as tipAs, then `route` loaded afresh (What's new is seen by then), so that page's tip shows. */
const tipOn = (route, people) => async page => {
  await tipAs(people)(page);
  await page.goto(new URL(`.${route}`, page.url()).href, { waitUntil: 'load' });
  await idle(page);
  await wideFont(page);
};

/** Setup: as tipAs, then Got it pressed from the keyboard. */
const tipGotIt = people => async page => {
  await tipAs(people)(page);
  await page.locator('.tide-tip .btn-primary').focus();
  await page.keyboard.press('Enter');
  await idle(page);
};

/** Setup: as whatsNewAs, then What's new's Take the tour. */
const tourFromWhatsNew = people => async page => {
  await whatsNewAs(people)(page);
  await page.locator('#whatsnew-take-tour').click();
  await page.locator('.tide-tour').waitFor();
  await idle(page);
};

/** A guide's step is up: then `keys` (Enter: Next), and wider letters. */
const stepThen = async (page, keys) => {
  await page.locator('.tide-tour').waitFor();
  await idle(page);
  for (const key of keys) { await page.keyboard.press(key); await idle(page); }
  await wideFont(page);
};

/** Setup: a guide started from Help's Guides (`key`), which opens its page; then `keys`. */
const guideFromHelp = (key, ...keys) => async page => {
  await page.goto(new URL('./help', page.url()).href, { waitUntil: 'load' });
  await idle(page);
  await page.locator(`#help-guide-${key}`).click();
  await stepThen(page, keys);
};

/** Setup: Learn to present from Help (present mode in rehearsal, on today's brief), then `keys`. */
const rehearsal = (...keys) => guideFromHelp('present', ...keys);

/** Setup: the page's guide started from its Show me around, then `keys`. */
const guideOn = (...keys) => async page => {
  await page.locator('#tide-guide-start').click();
  await stepThen(page, keys);
};

/** Setup: today's meeting started (or taken up) in another browser, then this page asks for a rehearsal of it. */
const rehearsalWhileLive = async (page, demo) => {
  for (const c of meetingGuests.contexts) await c.close().catch(() => {});
  meetingGuests = { key: 'rehearsal/live', contexts: [] };
  const { context, page: presenter } = await openPage(page.context().browser(), { route: '/present/{today}', viewport: page.viewportSize() }, demo);
  meetingGuests.contexts.push(context);
  await startOrTakeUp(presenter);
  await page.goto(new URL(`./present/${demo.Today}?rehearse=1`, page.url()).href, { waitUntil: 'load' });
  await idle(page);
};

/** Setup: a Today queue row whose line says whose it is with the longest ticket reference the field takes, as an
 *  external team's escalated action does, with wider letters. */
const longQueueTicket = async page => {
  await wideFont(page);
  await page.evaluate(() => {
    // The demo's tester has no escalated or queued action: one row as the Escalated card draws it, in the first card shown
    // (the rail's where it's docked).
    const card = [...document.querySelectorAll('#tide-rail .tide-card, main .tide-card')].find(c => c.getBoundingClientRect().height > 0);
    if (!card) return;
    const row = document.createElement('div');
    row.className = 'tide-queue-row';
    row.innerHTML = '<div class="tide-queue-title"><a href="actions/1">Patch the hosted edge</a><div class="tide-queue-meta"></div></div>';
    row.querySelector('.tide-queue-meta').textContent = 'Hosting Provider · INC-' + '0123456789ABCDEF'.repeat(6);
    card.append(row);
  });
};

/** Setup: every holder named by the longest ticket reference the field takes (100 characters, one word), as an external
 *  team's action is named by its ticket, with wider letters. */
const longTicket = async page => {
  await wideFont(page);
  await page.evaluate(() => {
    const ticket = () => Object.assign(document.createElement('span'), { className: 'tide-holder', textContent: 'INC-' + '0123456789ABCDEF'.repeat(6) });
    // A card's holder follows its person icon; a row's is under its team's name.
    document.querySelectorAll('.tide-kcard-meta > .bi-person').forEach(icon => {
      if (icon.nextSibling?.nodeType === Node.TEXT_NODE) icon.nextSibling.remove();
      icon.after(' ', ticket());
    });
    document.querySelectorAll('.tide-action-row td[class="small"] > div.tide-muted').forEach(cell => cell.replaceChildren(ticket()));
  });
};

/** Setup: wider letters, and `theme` (dark) set on the page as the app's theme switch would. */
const darkWide = async page => {
  await page.evaluate(() => document.documentElement.setAttribute('data-bs-theme', 'dark'));
  await toggleRail(page);
};

/** Setup: the meeting's form opened from the keyboard (Change presenter), then `button` (Save or Cancel) pressed with Enter. */
const meetingClosedBy = button => async page => {
  await openMeeting(page);
  if (button === 'Save') await page.locator('#tide-rail #intro').fill('A quiet day.');
  await page.locator('#tide-rail section[aria-labelledby="meeting-h"]').getByRole('button', { name: button, exact: true }).focus();
  await page.keyboard.press('Enter');
  await page.locator('#tide-rail #analyst').waitFor({ state: 'detached', timeout: 10000 });
  await idle(page);
};

/** Setup: the opening note's Edit pressed with Enter; then, with `then`, its Cancel or Save (after typing) the same way. */
const noteEdited = then => async page => {
  const note = page.locator('section[aria-labelledby="team-note-h"]');
  await note.getByRole('button', { name: 'Edit', exact: true }).focus();
  await page.keyboard.press('Enter');
  await page.locator('#team-note').waitFor();
  await idle(page);
  if (!then) return;
  if (then === 'Save note') await page.locator('#team-note').fill(`Payroll lures are up (${Date.now()}).`);
  await note.getByRole('button', { name: then, exact: true }).focus();
  await page.keyboard.press('Enter');
  await page.locator('#team-note').waitFor({ state: 'detached', timeout: 10000 });
  await idle(page);
};

/**
 * Setup: the analyst types into `field` (the meeting's title slide line, or the opening note) and stays in it; meanwhile
 * a colleague (Cole, in a browser of their own) saves the same field. The analyst's page hears it.
 */
const colleagueSavesWhileTyping = field => async page => {
  const origin = new URL(page.url()).origin;
  const open = async p => {
    if (field === 'intro') {
      if (!(await p.locator('#tide-rail').isVisible())) await p.locator('#tide-rail-toggle').click();
      await p.locator('#tide-rail').getByRole('button', { name: 'Title slide line', exact: true }).click();
      await p.locator('#intro').waitFor();
    } else {
      await p.locator('section[aria-labelledby="team-note-h"]').getByRole('button', { name: 'Edit', exact: true }).click();
      await p.locator('#team-note').waitFor();
    }
  };
  await open(page);
  await page.locator(`#${field}`).click();
  await page.keyboard.type('Mine, typed and not saved');
  const cole = await page.context().browser().newContext({ viewport: { width: 1920, height: 900 }, reducedMotion: 'reduce' });
  await watch(cole);
  try {
    const theirs = await cole.newPage();
    await theirs.goto(`${origin}/?as=Cole`, { waitUntil: 'load' });
    await settle(theirs);
    await open(theirs);
    await theirs.locator(`#${field}`).fill('Theirs, saved');
    await theirs.locator(field === 'intro' ? '#tide-rail section[aria-labelledby="meeting-h"]' : 'section[aria-labelledby="team-note-h"]')
      .getByRole('button', { name: field === 'intro' ? 'Save' : 'Save note', exact: true }).click();
    await page.locator('.tide-changed-elsewhere, .tide-toast').first().waitFor({ timeout: 15000 });
    await idle(page);
  } finally {
    await cole.close();
  }
};

/** What's typed in `field` is still there, and the page says a colleague changed it meanwhile. */
/** What's typed in `field` is still there, and a "changed elsewhere" notice shows inside `near` (where it's typed). */
const keptWhatsTyped = (field, near) => ({
  name: `kept what's typed: #${field}, notice in ${near}`,
  check: page => page.evaluate(([field, near]) => {
    const box = document.getElementById(field), notice = document.querySelector(`${near} .tide-changed-elsewhere`);
    return [
      ...(box && box.value.includes('Mine, typed and not saved') ? [] : [`#${field} holds "${box?.value}", not what was typed`]),
      ...(notice && notice.getClientRects().length ? [] : [`no "changed elsewhere" notice in ${near}`]),
    ];
  }, [field, near]),
});

/** Each visible match of `selector` is wholly in view: no ancestor that clips its overflow cuts any of it off. */
/** No visible match of `selector` is drawn narrower than its content: it hasn't been squeezed. */
const notSqueezed = selector => ({
  name: `not squeezed: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) => els.flatMap((el, i) =>
    el.getBoundingClientRect().width > 0 && el.scrollWidth > el.clientWidth + 1 ? [`${selector} #${i + 1} is ${el.scrollWidth - el.clientWidth}px narrower than its text`] : []), selector),
});

const notClipped = selector => ({
  name: `not clipped: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) => els.flatMap((el, i) => {
    const r = el.getBoundingClientRect();
    if (r.width === 0 || r.height === 0 || getComputedStyle(el).display === 'none') return [];
    for (let a = el.parentElement; a; a = a.parentElement) {
      if (getComputedStyle(a).overflow === 'visible') continue;
      const b = a.getBoundingClientRect();
      if (r.left < b.left - 1 || r.right > b.right + 1 || r.top < b.top - 1 || r.bottom > b.bottom + 1)
        return [`${selector} #${i + 1} is cut off by ${a.tagName.toLowerCase()}.${a.className}`];
    }
    return [];
  }), selector),
});

/** The first visible match of each selector comes lower on the page than the one before: the page's order. */
const inOrder = (...selectors) => ({
  name: `in order: ${selectors.join(' / ')}`,
  check: page => page.evaluate(selectors => {
    const tops = selectors.map(s => [...document.querySelectorAll(s)].map(e => e.getBoundingClientRect()).find(r => r.height > 0)?.top);
    const missing = selectors.filter((_, i) => tops[i] === undefined);
    if (missing.length) return [`nothing visible: ${missing.join(', ')}`];
    return tops.slice(1).flatMap((t, i) => t > tops[i] ? [] : [`${selectors[i + 1]} (at ${Math.round(t)}px) isn't below ${selectors[i]} (at ${Math.round(tops[i])}px)`]);
  }, selectors),
});

/** Each visible match of `selector` lies within the first match of `box`, top to bottom. */
const insideOf = (selector, box) => ({
  name: `inside ${box}: ${selector}`,
  check: page => page.evaluate(([selector, box]) => {
    const b = document.querySelector(box)?.getBoundingClientRect();
    return [...document.querySelectorAll(selector)].map(e => e.getBoundingClientRect()).filter(r => r.height > 0 && b && (r.top < b.top - 1 || r.bottom > b.bottom + 1))
      .map(r => `${selector} (${Math.round(r.top)}–${Math.round(r.bottom)}px) spills out of ${box} (${Math.round(b.top)}–${Math.round(b.bottom)}px)`);
  }, [selector, box]),
});

/** Setup: wider letters, on today's `n`th article's slide (0, the first: two indicators; 1: none; 2: a dozen, and a
 *  dozen CVEs, with Demo:SlideFiller). */
const articleSlide = n => async page => {
  await wideFont(page);
  await toArticleSlide(page);
  for (let i = 0; i < n; i++) {
    const at = await page.locator('.tide-slide').getAttribute('aria-label');
    await page.keyboard.press('ArrowRight');
    await page.waitForFunction(was => document.querySelector('.tide-slide')?.getAttribute('aria-label') !== was, at);
  }
};

/** Setup: wider letters, then the slides stepped through (from the title) to the first that shows `selector`. */
const slideWith = selector => async page => {
  await wideFont(page);
  // The presenter's rail holds their strips: opened by its key where it isn't docked, then shut again while the slides
  // move (open over the slide, it's a dialog, and the slide's keys aren't its), and opened on each slide to look.
  const over = async open => {
    if (await page.locator(`#present-rail-toggle[aria-expanded="${open ? 'false' : 'true'}"]`).count() && !(await page.locator('#present-rail.is-docked').count())) {
      await page.keyboard.press(']');
      await idle(page);
    }
  };
  for (let i = 0; i < 12; i++) {
    await over(true);
    if (await page.locator(selector).count()) break;
    await over(false);
    await page.keyboard.press('ArrowRight');
    await idle(page);
  }
};

/** Setup: on the dozen-indicator slide, `opener` pressed to open its card (`title` its heading), then the card closed by
 *  `how`: 'Escape', or the name of its Close button. */
const cardClosed = (opener, title, how) => async (page, demo) => {
  await articleSlide(2)(page, demo);
  await page.locator(opener).first().click();
  await page.locator(title).waitFor();
  await idle(page);
  if (how === 'Escape') await page.keyboard.press('Escape');
  else await page.getByRole('button', { name: how }).click();
  await page.locator(title).waitFor({ state: 'detached' });
  await idle(page);
};

/** Setup: on the opening note's slide, the presenter's `then` done in its strip: 'edit' (Edit pressed), 'escape' (Edit,
 *  then Escape in the box) or 'keep' (Still current). */
const noteStrip = then => async (page, demo) => {
  await slideWith('.tide-slide-note')(page, demo);
  const strip = page.locator('.tide-slide-presenter');
  await strip.getByRole('button', { name: then === 'keep' ? 'Still current' : 'Edit', exact: true }).click();
  if (then === 'escape') {
    await page.locator('#slide-note-text').waitFor();
    await page.locator('#slide-note-text').focus();
    await page.keyboard.press('Escape');
  }
  await idle(page);
};

/** Setup: on the numbers slide with one missing, `text` typed in its box and Enter pressed. */
const numberEntered = text => async (page, demo) => {
  await slideWith('.tide-slide-presenter input')(page, demo);
  await page.locator('.tide-slide-presenter input').first().fill(text);
  await page.keyboard.press('Enter');
  await idle(page);
};

/** Setup: the presenter's rail opened over the article's slide, then a click on the scrim beside it. */
const railScrimClicked = async (page, demo) => {
  await presenterRail(true)(page, demo);
  await page.mouse.click(20, Math.round(page.viewportSize().height / 2));
  await idle(page);
};

/** Setup: wider letters, on the title slide with the presenter's rail open, then its Enter beside the numbers missing. */
const missingNumberEntered = async page => {
  await wideFont(page);
  if (!(await page.locator('#present-rail').count())) {
    await page.keyboard.press(']');
    await page.locator('#present-rail').waitFor();
  }
  await page.locator('#present-rail').getByRole('button', { name: 'Enter', exact: true }).click();
  await idle(page);
};

/** Setup: wider letters, on the first article's slide; with `open`, the presenter's rail opened by its key (`]`). */
const presenterRail = (open = false) => async page => {
  await wideFont(page);
  await toArticleSlide(page);
  if (open) {
    await page.keyboard.press(']');
    await page.locator('#present-rail').waitFor();
  }
  await idle(page);
};

/** This page starts today's meeting, or takes it up again (the dev analyst presented it last), and waits for it. */
const startOrTakeUp = async p => {
  const button = p.getByRole('button', { name: 'Start the meeting' });
  if (await button.count()) await button.click();
  await p.locator('.tide-present-people').waitFor();
};

/** The meeting the present-people pages share: which one it is ("presenter/5"), and the browsers of its other people,
 *  kept open across a page's widths (each width opens only its own page) and closed when the next one is set up. */
let meetingGuests = { key: '', contexts: [] };

/** The meeting's other people, by name. */
const GUESTS = ['Olive', 'Robin', 'Sam', 'Lee', 'Kim', 'Ana', 'Max'];

/**
 * Setup: a live meeting on today's brief with `people` in it (eight unless said) and `hands` of them with a hand up, in
 * order. `as` 'presenter': this page (the dev analyst) starts it, or takes it up again, and the others join in browsers
 * of their own. 'follower': the dev analyst presents from another browser, the others join, and this page joins as Pat,
 * following. Wider letters, on the first article's slide.
 */
const meetingOf = (as, hands, people = 8) => async (page, demo) => {
  const key = `${as}/${hands}/${people}`;
  const browser = page.context().browser();
  const join = async who => {
    const { context, page: theirs } = await openPage(browser,
      { route: `/present/{today}${who ? `?as=${who}&roles=Viewer` : ''}`, viewport: { width: 1440, height: 900 } }, demo);
    meetingGuests.contexts.push(context);
    return theirs;
  };
  if (meetingGuests.key !== key) {
    for (const c of meetingGuests.contexts) await c.close().catch(() => {});
    meetingGuests = { key, contexts: [] };
    const presenter = as === 'presenter' ? page : await join('');
    await startOrTakeUp(presenter);
    await toArticleSlide(presenter);
    for (const [i, who] of GUESTS.slice(0, people - (as === 'presenter' ? 1 : 2)).entries()) {
      const theirs = await join(who);
      if (i >= hands) continue;
      await theirs.locator('.tide-present-people').getByRole('button', { name: 'Raise hand' }).click();
      await theirs.locator('.tide-present-people').getByRole('button', { name: 'Lower hand' }).waitFor();
    }
  }
  if (as === 'presenter') {
    await startOrTakeUp(page);
    await toArticleSlide(page);
  } else {
    await page.goto(`${page.url()}?as=Pat&roles=Viewer`, { waitUntil: 'load' });
    await settle(page);
  }
  await page.waitForFunction(([hands, people]) => document.querySelector('.tide-present-here')?.textContent.startsWith(`${people} here`)
    && (document.querySelector('.tide-hands-n')?.textContent.trim() ?? String(document.querySelectorAll('.tide-present-people .tide-hands > li').length)) === String(hands), [hands, people], { timeout: 15000 });
  await wideFont(page);
};

/** Setup: the presenter's meeting with five hands up, and its list of hands opened from the top bar. */
const handsListOpen = async (page, demo) => {
  await meetingOf('presenter', 5)(page, demo);
  await page.locator('.tide-hands-btn').click();
  await page.locator('#present-hands').waitFor();
};

/** Setup: the list of hands opened (the room's view of them: the presenter lowers a hand from the rail), then Escape. */
const handsListEscaped = async (page, demo) => {
  await handsListOpen(page, demo);
  await page.keyboard.press('Escape');
  await idle(page);
};

/** Setup: the list of hands opened, then a press on the slide, outside it. */
const handsListPressedOutside = async (page, demo) => {
  await handsListOpen(page, demo);
  await page.mouse.click(40, 600);
  await idle(page);
};

/** Setup: the presenter opens the list of hands while one is up; that hand goes down, and then another goes up. The
 *  list stays shut: it never opens without a click. */
const handsListAfterEmpty = async (page, demo) => {
  await meetingOf('presenter', 0, 3)(page, demo);
  const [olive, robin] = meetingGuests.contexts.map(c => c.pages()[0]);
  const hand = p => p.locator('.tide-present-people').getByRole('button', { name: /Raise hand|Lower hand/ }).click();
  await hand(olive);
  await page.locator('.tide-hands-btn').click();
  await page.locator('#present-hands').waitFor();
  await hand(olive);
  await page.locator('.tide-hands-btn').waitFor({ state: 'detached' });
  await hand(robin);
  await page.locator('.tide-hands-btn').waitFor();
  await idle(page);
};

/** Setup: a toast shown while the rail is docked (the opening note saved; whatever the toast says, it's placed alike). */
async function toastBesideRail(page) {
  await noteEdited(null)(page);
  await page.locator('#team-note').fill(`Payroll lures are up (${Date.now()}).`);
  await page.locator('section[aria-labelledby="team-note-h"]').getByRole('button', { name: 'Save note', exact: true }).click();
  await page.locator('.tide-toast').first().waitFor();
}

/** Setup: wider letters, then `]` pressed: the context rail opens where it's an overlay, and closes where it docks. */
const toggleRail = async page => {
  await wideFont(page);
  await page.locator('body').press(']');
  await idle(page);
};

/** Setup: the first two articles (both ready) taken off today's brief, so "Not on a brief yet" lists them; then the first one's Add
 *  pressed from the keyboard, and the card read again. */
async function addFromRail(page) {
  const card = page.locator('section[aria-labelledby="recent-h"]');
  for (let i = 0; i < 2; i++) {
    const n = await card.locator('li').count();
    await page.getByRole('button', { name: /off this brief$/ }).first().click();
    await page.waitForFunction(([n]) => document.querySelectorAll('section[aria-labelledby="recent-h"] li').length > n, [n], { timeout: 10000 });
  }
  const before = await card.locator('li').count();
  await card.getByRole('button', { name: /^Add / }).first().focus();
  await page.keyboard.press('Enter');
  await page.waitForFunction(([n]) => document.querySelectorAll('section[aria-labelledby="recent-h"] li').length < n, [before], { timeout: 10000 });
  await idle(page);
}

/** Setup: wider letters, then the meeting's Change presenter pressed (opening its form, a docked rail or the overlay). */
async function openMeeting(page) {
  await wideFont(page);
  if (!(await page.locator('#tide-rail').isVisible())) {
    await page.locator('#tide-rail-toggle').click();
    await idle(page);
  }
  await page.locator('#tide-rail').getByRole('button', { name: 'Change presenter' }).click();
  await page.locator('#tide-rail #analyst').waitFor({ state: 'attached' });
  await idle(page);
}

/**
 * Setup: a colleague takes the action whose Take it has focus in the rail's queue. The demo's dev analyst and a second
 * person (Olive, an action owner) go on the first team, with two actions nobody has; focus goes to the first one's
 * Take it, and Olive takes that action from her own Today. The row leaves the analyst's queue, live.
 */
async function colleagueTakesFocusedRow(page) {
  const browser = page.context().browser();
  const olive = await browser.newContext({ viewport: { width: 1920, height: 900 }, reducedMotion: 'reduce' });
  await watch(olive);
  try {
    const her = await olive.newPage();
    await her.goto(`${new URL(page.url()).origin}/?as=Olive&roles=ActionOwner`, { waitUntil: 'load' });
    await page.goto(new URL('/admin/teams', page.url()).href, { waitUntil: 'load' });
    await settle(page);
    const pick = page.locator('select[aria-label^="Person to add to"]').first();
    const team = (await pick.getAttribute('aria-label')).replace('Person to add to ', '');
    for (const who of ['Dev Analyst', 'Olive']) {
      if (!await pick.locator('option', { hasText: who }).count()) continue;   // on the team already (the demo's analyst)
      await pick.selectOption({ label: who });
      await pick.locator('xpath=following::button[normalize-space()="Add"][1]').click();
      await idle(page);
    }
    for (const title of ['Live check one', 'Live check two']) {
      await page.goto(new URL('/actions/new', page.url()).href, { waitUntil: 'load' });
      await settle(page);
      await page.locator('#a-title').fill(title);
      await page.locator('#a-team').selectOption({ label: team });
      await idle(page);
      await page.getByRole('button', { name: 'Create action' }).click();
      await page.waitForURL(/actions\/\d+/);
    }
    await page.goto(new URL('/', page.url()).href, { waitUntil: 'load' });
    await settle(page);
    const queue = page.locator('#tide-rail section[aria-labelledby="teams-queue-h"]');
    await queue.getByRole('button', { name: /Live check one/ }).focus();
    await her.reload({ waitUntil: 'load' });
    await settle(her);
    await her.locator('section[aria-labelledby="team-queue-h"]').getByRole('button', { name: /Live check one/ }).click();
    await queue.getByRole('button', { name: /Live check one/ }).waitFor({ state: 'detached', timeout: 15000 });
    await idle(page);
  } finally {
    await olive.close();
  }
}

/**
 * Setup: the first team's active limit set to 1, warning and asking why (Teams & SLA); then on the first article's slide
 * of today's meeting, the presenter's rail opened over it by its key, a title typed in its quick add for that team, Add
 * pressed, and the question that asks why cancelled with Escape. The limit is left in place: last of the pages.
 */
async function overLimitCancelled(page, demo) {
  const to = route => new URL(route, page.url()).href;
  await page.goto(to('/admin/teams'), { waitUntil: 'load' });
  await settle(page);
  const card = page.locator('.tide-card').filter({ has: page.getByRole('button', { name: 'Edit', exact: true }) }).first();
  const team = (await card.locator('.tide-card-title').innerText()).trim();
  await card.getByRole('button', { name: 'Edit', exact: true }).click();
  await idle(page);
  const form = page.locator('.tide-card').filter({ has: page.getByLabel('Active limit') });   // one team's at a time
  await form.getByLabel('Active limit').fill('1');
  await form.getByLabel('Active limit').press('Tab');
  await form.getByLabel('At the limit').selectOption({ label: 'Warn, and ask why' });
  await idle(page);
  await form.getByRole('button', { name: 'Save', exact: true }).click();
  await page.getByText('Limit: 1 active, warn, and ask why').waitFor();
  await page.goto(to(`/present/${demo.Today}`), { waitUntil: 'load' });
  await settle(page);
  await page.keyboard.press('Escape');   // an action the last page left open for the room
  await idle(page);
  await presenterRail(true)(page, demo);
  const rail = page.locator('#present-rail');
  await rail.locator('.tide-quick-add select[aria-label="Team"]').selectOption({ label: team });
  await rail.getByRole('textbox', { name: 'New action title' }).fill('Over the limit, then cancelled');
  await idle(page);
  await rail.locator('.tide-quick-add').getByRole('button', { name: 'Add', exact: true }).click();
  await page.locator('.tide-modal').waitFor();
  await idle(page);
  await page.keyboard.press('Escape');
  await page.locator('.tide-modal').waitFor({ state: 'detached' });
  await idle(page);
}

/** Setup: V pressed `times`, a moment apart (each read or close reaching the page before the next). */
const pressV = times => async page => {
  for (let i = 0; i < times; i++) {
    await page.keyboard.press('v');
    await idle(page);
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
  await idle(page);
}

/**
 * Setup: the page's context rail opened (as an overlay below 1500px) the way its button opens it, and focus on the first
 * control in its sections.
 */
async function openRail(page) {
  if (!(await page.locator('#tide-rail').isVisible())) await page.click('#tide-rail-toggle');
  await page.locator('#tide-rail').waitFor();
  await page.locator(railLink).first().focus();
}

/** A control in the rail's sections (Triage's: a CVE's link, an article's, Add to what we run…). */
const railLink = '#tide-rail .tide-rail-body :is(a[href], button)';

// --- Triage's context rail (option E 3.2) ------------------------------------------------------------------------------

/** Lines of Triage's list by what the rail says of them: a report an article covers, a group one covers, one naming a
 *  CVE TIDE has a record of. */
const triageLines = {
  covered: '.tide-triage-row:not(.tide-triage-group):not(.tide-triage-member):has(.tide-triage-cov)',
  group: '.tide-triage-group:has(.tide-triage-cov)',
  cve: '.tide-triage-row:not(.tide-triage-group):has(.tide-cvss)',
};

/** Setup: wider letters (LETTER_SPACING), then the first line of `kind` (triageLines) selected by a click on its source's
 *  name; the rail follows it. */
const railOn = kind => async page => {
  await wideFont(page);
  await page.locator(triageLines[kind]).first().locator('.tide-group-label, .tide-triage-meta > span').first().click();
  await page.waitForFunction(kind => document.querySelector('.tide-triage-row.selected')?.matches(kind), triageLines[kind], { timeout: 10000 });
  await idle(page);
};

/** Setup: wider letters, the rail opened over the page with `]` (below 1500px), then `kind`'s line selected from the list
 *  first, as railOn. */
const railOverOn = kind => async page => {
  await railOn(kind)(page);
  await page.locator('.tide-triage-row.selected').focus();
  await page.keyboard.press(']');
  await page.locator('#tide-rail').waitFor();
  await idle(page);
};

/** Setup: Escape pressed on the selected row, where the rail is docked: Triage's own Escape, which closes the pane. */
async function escapeOnRow(page) {
  await wideFont(page);
  await page.locator('.tide-triage-row.selected').focus();
  await page.keyboard.press('Escape');
  await idle(page);
}

const selectedRow = page => page.evaluate(() => [...document.querySelectorAll('.tide-triage-row')].findIndex(r => r.classList.contains('selected')));

/** The keys pressed in the open rail are the rail's: J and the down arrow there leave the list's selection where it is. */
const railKeysStayInTheRail = {
  name: 'keys in the rail leave the list alone',
  check: async page => {
    const problems = [], before = await selectedRow(page);
    if (before < 0) return ['no row is selected to begin with'];
    for (const key of ['j', 'ArrowDown']) {
      await page.locator(railLink).first().focus();
      await page.keyboard.press(key);
      await idle(page);
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
    await page.locator(railLink).first().focus();
    await page.keyboard.press('Escape');
    await idle(page);
    const problems = [];
    if (await anyShown(page, '#tide-rail')) problems.push('the rail is still open after Escape');
    if (!await anyShown(page, '.tide-reading')) problems.push('Escape in the rail closed the reading pane too');
    await page.reload({ waitUntil: 'load' });
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
    await idle(page);
    if (await focused() !== 'new-name') problems.push(`opening the form left focus on ${await focused()}, not its Product box`);
    await page.getByRole('button', { name: 'Cancel' }).focus();
    await page.keyboard.press('Enter');
    await idle(page);
    if (await focused() !== 'Add a product') problems.push(`Cancel left focus on ${await focused()}, not Add a product`);
    return problems;
  },
};

/** Where focus is, said for a failure. */
const focusedOn = page => page.evaluate(() => {
  const el = document.activeElement;
  return !el || el === document.body ? 'the page body' : `<${el.tagName.toLowerCase()} class="${el.className}"> ${(el.innerText ?? '').trim().slice(0, 30)}`;
});

/**
 * A popout that closes when it's done with: Escape anywhere on the page (with focus outside it too), a click outside it,
 * or focus leaving it by Tab. `wrap` holds the popout and the button that opens it (`opener`); `panel` is the popout;
 * `inside` is where focus is put for an Escape in it, and `bare` a part of it that takes no focus, pressed. Escape is the popout's alone: it leaves the reading pane (where
 * there is one) as it was, and an Escape in it puts focus back on its button; a click outside leaves focus where it went.
 */
const popoutCloses = (label, { wrap, opener, panel, inside, bare }) => {
  const pane = page => anyShown(page, '.tide-reading');
  const open = async page => {
    // One a rule before left open (it failed) goes, with the page as it was.
    if (await anyShown(page, `${wrap} ${panel}`)) { await page.reload({ waitUntil: 'load' }); await settle(page); }
    await page.locator(`${wrap} ${opener}`).first().click();
    await page.locator(`${wrap} ${panel}`).first().waitFor({ timeout: 5000 });
    await idle(page);
  };
  const stillOpen = async (page, how) => {
    await idle(page);
    return (await anyShown(page, `${wrap} ${panel}`)) ? [`${label} is still open after ${how}`] : [];
  };
  const paneKept = async (page, was, how) => (await pane(page)) === was ? [] : [`${how} ${was ? 'closed' : 'opened'} the reading pane too`];
  const onOpener = page => page.evaluate(sel => document.activeElement === document.querySelector(sel), `${wrap} ${opener}`);
  return [
    { name: `${label}: Escape with focus on the page closes it, and only it`, check: async page => {
      const was = await pane(page);
      await open(page);
      // Focus left on the page while it's open: the body, where a removed control or a click on bare page leaves it.
      await page.evaluate(() => document.activeElement?.blur());
      await page.keyboard.press('Escape');
      return [...await stillOpen(page, 'Escape with focus on the page'), ...await paneKept(page, was, 'that Escape')];
    } },
    { name: `${label}: Tab out of it closes it`, check: async page => {
      await open(page);
      for (let i = 0; i < 20 && await page.evaluate(sel => !!document.activeElement?.closest(sel), wrap); i++) await page.keyboard.press('Tab');
      return stillOpen(page, 'Tab took focus out of it');
    } },
    { name: `${label}: a click outside closes it, focus staying there`, check: async page => {
      await open(page);
      // By the mouse at the heading, whatever is over it (a backdrop that hears the click is outside too).
      const at = await page.locator('.tide-page-head h1').first().boundingBox();
      await page.mouse.click(at.x + 10, at.y + at.height / 2);
      const problems = await stillOpen(page, 'a click on the page heading');
      if (await onOpener(page)) problems.push(`a click outside put focus back on its button`);
      return problems;
    } },
    { name: `${label}: Escape in it closes it, focus back on its button`, check: async page => {
      const was = await pane(page);
      await open(page);
      await page.locator(`${wrap} ${inside}`).first().focus();
      await page.keyboard.press('Escape');
      const problems = [...await stillOpen(page, 'Escape in it'), ...await paneKept(page, was, 'that Escape')];
      if (!await onOpener(page)) problems.push(`Escape in it left focus on ${await focusedOn(page)}, not its button`);
      return problems;
    } },
    // A press on a part of it that takes no focus (its background, a label, a hint): the browser moves focus to the
    // nearest focusable element holding it, which mustn't count as leaving it. By mouse, and by a tap.
    ...(bare ? ['mouse', 'touch'].map(by => ({ name: `${label}: a ${by} press inside it (${bare}) leaves it open`, check: async page => {
      await open(page);
      const part = page.locator(`${wrap} ${bare}`).first();
      await part.scrollIntoViewIfNeeded();
      const box = await part.boundingBox();
      const hit = await page.evaluate(([x, y]) => document.elementFromPoint(x, y)?.className, [box.x + 3, box.y + 3]);
      await pressAt(page, by, box.x + 3, box.y + 3);
      await idle(page);
      return (await anyShown(page, `${wrap} ${panel}`)) ? [] : [`a ${by} press on ${bare} (${hit}) closed ${label}, focus going to ${await focusedOn(page)}`];
    } })) : []),
  ];
};

/** A mouse click, or a tap (touch, by the DevTools protocol, as a phone sends it), at x, y. */
async function pressAt(page, by, x, y) {
  if (by === 'mouse') return page.mouse.click(x, y);
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 1 });
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x, y, radiusX: 1, radiusY: 1 }] });   // a fine point: no snapping to a nearby control
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await cdp.detach();
}

/** Setup: a click on the first `opener`, and its `panel` drawn. */
const openFirst = (opener, panel) => async page => {
  await page.locator(opener).first().click();
  await page.locator(panel).first().waitFor({ timeout: 5000 });
};

/** Every shown match of `selector` lies side to side inside the page: within the window, and clear of the sidebar
 * beside it (from where <main> starts). */
const insidePage = selector => ({
  name: `inside the page: ${selector}`,
  check: page => page.$$eval(selector, (els, selector) => {
    const shown = els.map(e => e.getBoundingClientRect()).filter(r => r.width > 0 && r.height > 0);
    if (!shown.length) return [`nothing shown matches ${selector}`];
    const from = Math.max(0, document.querySelector('main')?.getBoundingClientRect().left ?? 0), to = document.documentElement.clientWidth;
    return shown.filter(r => r.left < from - 0.5 || r.right > to + 0.5)
      .map(r => `${selector} runs from x=${Math.round(r.left)} to ${Math.round(r.right)}px, outside the page (${Math.round(from)} to ${to}px)`);
  }, selector),
});

/**
 * Opening a popout (by a click on `opener`, as the mouse, a tap or Enter does) moves nothing and hides nothing:
 * - the page doesn't scroll sideways, isn't wider than what shows of it (the body's clientWidth, without the scrollbar
 *   or the gutter kept for it), and a sideways wheel or Shift+wheel over it afterwards still doesn't scroll it sideways;
 * - the popout lies inside what shows of the window, top to bottom, and never over its button;
 * - no part of it is cut off by a box it's drawn in: the middle of each edge of it, and each end of each item, is its own
 *   (a hit test there finds it).
 */
const opensInPlace = (label, opener, panel) => ({
  name: `${label} opens in place`,
  check: async page => {
    const button = page.locator(opener).first();
    await button.scrollIntoViewIfNeeded();
    const x0 = await page.evaluate(() => scrollX);
    await button.click();
    await page.locator(panel).first().waitFor({ timeout: 5000 });
    await idle(page);
    const seen = await page.evaluate(([sel, opener]) => {
      const el = [...document.querySelectorAll(sel)].find(e => e.getBoundingClientRect().height > 0);
      const r = el.getBoundingClientRect(), b = document.querySelector(opener).getBoundingClientRect();
      const de = document.documentElement;
      const mine = (x, y) => el.contains(document.elementFromPoint(x, y));
      // Just inside the middle of each edge (its corners are rounded off).
      const mx = (r.left + r.right) / 2, my = (r.top + r.bottom) / 2;
      const edges = [[r.left + 3, my], [r.right - 3, my], [mx, r.top + 3], [mx, r.bottom - 3]];
      const items = [...el.querySelectorAll('[role=menuitem], [role=option], [role=menuitemradio], [role=menuitemcheckbox], .tide-profile-item, .tide-export-panel a')]
        .map(i => [i, i.getBoundingClientRect()]).filter(([, ir]) => ir.height > 0 && ir.top >= r.top && ir.bottom <= r.bottom);
      return {
        x: scrollX, scrollWidth: de.scrollWidth, clientWidth: Math.min(de.clientWidth, document.body.clientWidth), clientHeight: de.clientHeight, top: r.top, bottom: r.bottom,
        overOpener: r.left < b.right && r.right > b.left && r.top < b.bottom && r.bottom > b.top,
        cutEdges: edges.filter(([x, y]) => !mine(x, y)).map(([x, y]) => { const e = document.elementFromPoint(x, y); return (e ? `${e.tagName.toLowerCase()}.${e.className}`.trim() : "nothing") + ` at ${Math.round(x)},${Math.round(y)}`; }),
        cutItems: items.filter(([i, ir]) => ![[ir.left + 2, (ir.top + ir.bottom) / 2], [ir.right - 2, (ir.top + ir.bottom) / 2]]
          .every(([x, y]) => i.contains(document.elementFromPoint(x, y)))).map(([i]) => i.innerText.trim().slice(0, 24)),
      };
    }, [panel, opener]);
    const problems = [];
    if (seen.x !== x0) problems.push(`opening it scrolled the page sideways ${Math.round(seen.x - x0)}px`);
    if (seen.scrollWidth > seen.clientWidth) problems.push(`the page is ${seen.scrollWidth - seen.clientWidth}px wider than what shows of it`);
    if (seen.top < -0.5 || seen.bottom > seen.clientHeight + 0.5)
      problems.push(`it runs from y=${Math.round(seen.top)} to ${Math.round(seen.bottom)}px, outside the window (0 to ${seen.clientHeight}px)`);
    if (seen.overOpener) problems.push('it covers its own button');
    if (seen.cutEdges.length) problems.push(`${seen.cutEdges.length} of its edges are cut off or covered (by ${seen.cutEdges.join(", ")})`);
    if (seen.cutItems.length) problems.push(`items cut off or covered: ${seen.cutItems.join(', ')}`);
    // A sideways wheel, and Shift with the wheel, over the page heading.
    const h = await page.locator('h1').first().boundingBox();
    await page.mouse.move(h.x + 5, Math.max(5, h.y + h.height / 2));
    await page.mouse.wheel(120, 0);
    await page.keyboard.down('Shift');
    await page.mouse.wheel(0, 120);
    await page.keyboard.up('Shift');
    await idle(page);
    const x1 = await page.evaluate(() => scrollX);
    if (x1 !== x0) problems.push(`a sideways wheel then scrolled the page ${Math.round(x1 - x0)}px`);
    return problems;
  },
});

/** Where focus is: an element's text, or "the page body". */
const focusName = page => page.evaluate(() => {
  const el = document.activeElement;
  return !el || el === document.body ? 'the page body' : (el.innerText || el.getAttribute('aria-label') || el.tagName).trim().slice(0, 40);
});

/** Delete in the entry's More menu asks first; Escape, or Cancel, leaves the entry with focus back on More. */
const deleteCancelledFocusesMore = {
  name: 'Delete cancelled: focus back on More',
  check: async page => {
    const problems = [];
    for (const how of ['Escape', 'Cancel']) {
      await page.locator('.tide-library-entry .tide-menubutton-btn').first().click();
      await page.getByRole('menuitem', { name: 'Delete' }).click();
      await page.locator('.tide-modal').waitFor({ timeout: 5000 });
      await idle(page);
      if (how === 'Escape') await page.keyboard.press('Escape');
      else await page.locator('.tide-modal').getByRole('button', { name: 'Cancel' }).click();
      await page.locator('.tide-modal').waitFor({ state: 'detached', timeout: 5000 });
      await idle(page);
      const now = await focusName(page);
      if (now !== 'More') problems.push(`after ${how} focus is on ${now}, not More`);
    }
    return problems;
  },
};

/** The palette opened (Ctrl+K) over the open + Filter, then closed: focus is back on + Filter. */
/** Save view, its name typed and Enter pressed: saved, as a form is; its menu button is named for the items it offers. */
const saveViewByEnter = {
  name: 'Save view: Enter saves, and the menu is named for what it offers',
  check: async page => {
    await page.locator('#save-view').click();
    await page.locator('#saved-view-name').waitFor({ timeout: 5000 });
    await page.locator('#saved-view-name').fill('By Enter');
    await page.keyboard.press('Enter');
    await idle(page);
    const problems = [];
    if (await anyShown(page, '.tide-modal')) return ['Enter in the Name box left the dialog open'];
    const menu = page.locator('.tide-savedview', { hasText: 'By Enter' }).locator('.tide-savedview-menu');
    if (!await menu.count()) return ['no saved view "By Enter" after Enter'];
    const named = (await menu.innerText()).trim();
    await menu.click();
    await page.locator('.tide-savedview [role=menuitem]').first().waitFor({ timeout: 5000 });
    const items = (await page.locator('.tide-savedview [role=menuitem]').allInnerTexts()).map(t => t.trim().toLowerCase());
    const said = named.replace(/^By Enter: /, '').split(/, | or /);
    if (said.join('|') !== items.join('|')) problems.push(`the menu button says "${named}" but the menu offers ${items.join(', ')}`);
    await page.keyboard.press('Escape');
    return problems;
  },
};

const paletteGivesFocusBack = {
  name: 'the palette over + Filter gives focus back to it',
  check: async page => {
    await page.locator('.tide-filter-menu .tide-filter-add').click();
    await page.locator('.tide-filter-pop').waitFor({ timeout: 5000 });
    await idle(page);
    await page.keyboard.press('Control+k');
    await page.locator('[data-palette-input]').waitFor({ timeout: 5000 });
    await idle(page);
    await page.keyboard.press('Escape');
    await idle(page);
    const now = await focusName(page);
    return /Filter/.test(now) ? [] : [`focus is on ${now}, not + Filter`];
  },
};

/** In each visible match of `row`, every cell's text starts at least `px` right of the row's left edge: clear of an
 * accent drawn inside the row's edge. */
const clearOfEdge = (row, px) => holds(`text clear of the edge: ${row}`, ([row, px]) => {
  const rows = [...document.querySelectorAll(row)].filter(e => e.getBoundingClientRect().height > 0);
  if (!rows.length) return [`nothing shown matches ${row}`];
  return rows.flatMap(r => [...r.children].flatMap((cell, i) => {
    const box = cell.getBoundingClientRect();
    if (box.width === 0) return [];
    const start = box.left + parseFloat(getComputedStyle(cell).paddingLeft) - r.getBoundingClientRect().left;
    return start >= px ? [] : [`cell ${i + 1}'s text starts ${Math.round(start)}px from the row's edge, under its accent`];
  }));
}, [row, px]);

/** Every visible link matching `selector` is drawn alike: the same colour and the same underline (or none). */
const linksAlike = selector => holds(`links alike: ${selector}`, selector => {
  const links = [...document.querySelectorAll(selector)].filter(e => e.getBoundingClientRect().height > 0);
  if (links.length < 2) return [`fewer than two links shown match ${selector}`];
  const look = a => { const s = getComputedStyle(a); return `${s.color} ${s.textDecorationLine}`; };
  const first = look(links[0]);
  return links.filter(a => look(a) !== first).map(a => `"${a.textContent.trim().slice(0, 40)}" is ${look(a)}, not ${first}`);
}, selector);

/** Setup: wider letters, then the rail's Mark checked pressed from the keyboard. */
async function markCheckedFromRail(page) {
  await wideFont(page);
  await page.locator('#tide-rail').getByRole('button', { name: 'Mark checked' }).focus();
  await page.keyboard.press('Enter');
  await page.locator('#tide-rail').getByRole('button', { name: 'Mark checked' }).waitFor({ state: 'detached', timeout: 10000 });
  await idle(page);
}

/** A list's selected row, as the keys move it: J selects the next row (the row marked, focused and named in the
 * address, the rail named for it and its sections, `loaded` among them, read once it rests), K the one before, a click
 * selects the row clicked (`clickAt`), and Enter on the selected row opens its page (`path`). On a list of three rows or
 * more (`min`), J is pressed twice. */
const listKeys = ({ name, row, loaded, path, min, clickAt }) => ({
  name,
  check: async page => {
    const problems = [];
    const state = () => page.evaluate(([row, loaded]) => {
      const rows = [...document.querySelectorAll(row)];
      const at = rows.findIndex(r => r.classList.contains('selected'));
      return {
        at, count: rows.length, current: rows[at]?.getAttribute('aria-current'), focused: document.activeElement === rows[at],
        name: rows[at]?.querySelector('td a')?.textContent.trim(), rail: document.querySelector('#tide-rail')?.getAttribute('aria-label'),
        loaded: !!document.querySelector(`#tide-rail ${loaded}`) && !document.querySelector('#tide-rail').textContent.includes('Loading…'),
        row: new URL(location.href).searchParams.get('row'),
      };
    }, [row, loaded]);
    const expect = async (at, why) => {
      await idle(page);
      const s = await state();
      if (s.at !== at) problems.push(`${why}: row ${s.at + 1} is selected, not row ${at + 1}`);
      if (s.current !== 'true') problems.push(`${why}: the selected row has no aria-current`);
      if (s.rail !== `About ${s.name}`) problems.push(`${why}: the rail is "${s.rail}", not About ${s.name}`);
      if (!s.loaded) problems.push(`${why}: the rail's sections didn't load`);
      if ((s.row ?? '0') !== String(at)) problems.push(`${why}: the address says row=${s.row}, not ${at}`);
      return s;
    };
    if ((await state()).count < min) return [`fewer than ${min} rows on the list`];
    await page.keyboard.press('j');
    if (!(await expect(1, 'J')).focused) problems.push('J: focus didn\'t follow the selected row');
    if (min > 2) {
      await page.keyboard.press('j');
      await expect(2, 'J again');
    }
    await page.keyboard.press('k');
    await expect(min > 2 ? 1 : 0, 'K');
    await page.locator(row).nth(clickAt).locator('td').nth(3).click();
    const clicked = await expect(clickAt, 'a click');
    await page.keyboard.press('Enter');
    await idle(page);
    const heading = await page.locator('h1').first().textContent();
    if (!path.test(new URL(page.url()).pathname) || !heading?.includes(clicked.name))
      problems.push(`Enter on the selected row went to ${page.url()} ("${heading?.trim()}"), not ${clicked.name}'s page`);
    return problems;
  },
});

/** Our stack's selected row, as the keys move it. */
const stackKeys = listKeys({ name: 'Our stack: J, K, a click and Enter', row: '.tide-stack-item', loaded: '#rail-articles-h', path: /\/stack\/[0-9a-f-]{36}$/,
  min: 3, clickAt: 0 });

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
    await idle(page);
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

/** Each shown match of `selector` lies wholly within the window: nothing to scroll to, nothing cut off at its edge. */
const inWindow = selector => holds(`wholly in the window: ${selector}`, selector =>
  [...document.querySelectorAll(selector)].filter(e => e.getBoundingClientRect().width > 0).flatMap((e, i) => {
    const r = e.getBoundingClientRect();
    return r.left < -1 || r.right > innerWidth + 1 || r.top < -1 || r.bottom > innerHeight + 1
      ? [`${selector} #${i + 1} ("${(e.getAttribute('aria-label') ?? e.textContent).trim().slice(0, 30)}") is at ${Math.round(r.left)}–${Math.round(r.right)}px of a ${innerWidth}px window`] : [];
  }), selector);

/** Each shown match of `selector` lies within the page's own width, left of any scrollbar, its border included. */
const inClient = selector => holds(`within the page's width: ${selector}`, selector =>
  [...document.querySelectorAll(selector)].filter(e => e.getBoundingClientRect().width > 0).flatMap((e, i) => {
    const r = e.getBoundingClientRect(), width = Math.min(document.documentElement.clientWidth, document.body.clientWidth);
    return r.left < 0 || r.right > width ? [`${selector} #${i + 1} is at ${Math.round(r.left)}–${Math.round(r.right)}px of a ${width}px page`] : [];
  }), selector);

/** How many are here is shown, and whole. */
const countWhole = holds('the count is shown, whole', () => {
  const here = document.querySelector('.tide-present-here');
  const r = here?.getBoundingClientRect();
  if (!r || r.width === 0) return ['.tide-present-here isn\'t shown'];
  return here.scrollWidth > here.clientWidth + 1 ? [`"${here.textContent}" is cut to ${Math.round(r.width)}px`] : [];
});

/** Each raised hand's name in the top bar is whole, or not shown at all (wrapped out of its one line): never cut. */
const namesWhole = holds('hands\' names whole or not shown', () => {
  const box = document.querySelector('.tide-hands-names')?.getBoundingClientRect();
  if (!box) return ['.tide-hands-names isn\'t there'];
  return [...document.querySelectorAll('.tide-hands-names > span')].flatMap((n, i) => {
    const r = n.getBoundingClientRect();
    const out = r.top >= box.bottom - 1 || r.width === 0;
    const whole = r.left >= box.left - 1 && r.right <= box.right + 1 && r.bottom <= box.bottom + 1 && n.scrollWidth <= n.clientWidth + 1;
    return out || whole ? [] : [`hand #${i + 1} ("${n.textContent}") is cut: ${Math.round(r.left)}–${Math.round(r.right)}px in ${Math.round(box.left)}–${Math.round(box.right)}px`];
  });
});

/** The text of each shown match of `selector` (or its `pseudo`, '::placeholder' say) contrasts with what's behind it
 *  (the nearest box with a background) by at least `ratio` (WCAG's relative luminance; 4.5 is AA for body text). */
const contrastAtLeast = (selector, ratio, pseudo = null) => holds(`contrast at least ${ratio}:1: ${selector}${pseudo ?? ''}`, ([selector, ratio, pseudo]) => {
  const rgb = c => (c.match(/[\d.]+/g) ?? []).map(Number);
  const lum = ([r, g, b]) => [r, g, b].map(v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; })
    .reduce((sum, v, i) => sum + v * [0.2126, 0.7152, 0.0722][i], 0);
  const behind = el => {
    for (let e = el; e; e = e.parentElement) {
      const c = rgb(getComputedStyle(e).backgroundColor);
      if (c.length >= 3 && (c.length < 4 || c[3] > 0.5)) return c;
    }
    return [255, 255, 255];
  };
  const shown = [...document.querySelectorAll(selector)].filter(e => e.getBoundingClientRect().width > 0);
  if (!shown.length) return [`nothing shown matches ${selector}`];
  return shown.flatMap((e, i) => {
    // A translucent colour is laid over what's behind it.
    const [r0, g0, b0, a = 1] = rgb(getComputedStyle(e, pseudo).color), under = behind(e);
    const color = [r0, g0, b0].map((v, k) => v * a + under[k] * (1 - a));
    const fg = lum(color), bg = lum(under);
    const r = (Math.max(fg, bg) + 0.05) / (Math.min(fg, bg) + 0.05);
    return r + 0.005 >= ratio ? [] : [`${selector}${pseudo ?? ''} #${i + 1}: ${getComputedStyle(e, pseudo).color} on ${under.join(',')} is ${r.toFixed(2)}:1`];
  });
}, [selector, ratio, pseudo]);

/** The presenter's rail's hints (its placeholders) read: AA, in either theme. */
const railPlaceholdersReadable = contrastAtLeast('#present-rail :is(input, textarea)[placeholder]', 4.5, '::placeholder');

/** Each visible list (select) in the presenter's rail is wide enough for its longest choice: none is cut off. */
const railSelectsWhole = holds('the rail\'s lists show their longest choice whole', () =>
  [...document.querySelectorAll('#present-rail select')].filter(el => el.getBoundingClientRect().width > 0).flatMap(el => {
    const has = el.getBoundingClientRect().width;
    el.style.width = 'max-content';
    const needs = el.getBoundingClientRect().width;
    el.style.width = '';
    return needs > has + 1 ? [`${el.getAttribute('aria-label')}: ${Math.round(has)}px wide, its longest choice needs ${Math.round(needs)}px`] : [];
  }));

/** While the rail is open over the slide, the slide's keys aren't heard: an arrow doesn't move the slide. */
const slideKeysHeld = {
  name: 'the slide stays put while the rail is open over it',
  check: async page => {
    const before = await page.locator('[data-slide-key]').first().getAttribute('data-slide-key');
    await page.evaluate(() => document.activeElement?.blur());
    await page.keyboard.press('ArrowRight');
    await idle(page);
    const after = await page.locator('[data-slide-key]').first().getAttribute('data-slide-key');
    return before === after ? [] : [`the slide moved from ${before} to ${after}`];
  },
};

/** The first shown match of `a` and of `b` look alike in each of `props` (computed): controls side by side that match. */
const sameLook = (a, b, props) => holds(`same ${props.join(', ')}: ${a} / ${b}`, ([a, b, props]) => {
  const first = s => [...document.querySelectorAll(s)].find(e => e.getBoundingClientRect().width > 0);
  const x = first(a), y = first(b);
  if (!x || !y) return [`nothing shown matches ${!x ? a : b}`];
  return props.flatMap(p => {
    const v = getComputedStyle(x)[p], w = getComputedStyle(y)[p];
    return v === w ? [] : [`${p}: ${v} on ${a}, ${w} on ${b}`];
  });
}, [a, b, props]);

/** Each shown button saying `text` names what it's for in its accessible name, starting `name` (none shown holds: an
 *  earlier page may have taken the demo's actions, and a test covers the name too). */
const buttonsNamed = (text, name) => holds(`"${text}" buttons named "${name}…"`, ([text, name]) => {
  const shown = [...document.querySelectorAll('button')].filter(b => b.textContent.trim() === text && b.getBoundingClientRect().width > 0);
  return shown.filter(b => !(b.getAttribute('aria-label') ?? '').startsWith(name)).map(b => `a "${text}" button is named "${b.getAttribute('aria-label') ?? text}"`);
}, [text, name]);

/** The slide's "+N more" links (the CVEs', the indicators') read on the dark slide: AA. */
const slideLinksReadable = contrastAtLeast('.tide-slide :is(.tide-slide-cve-more, .tide-slide-ioc-more)', 4.5);

/** The present top bar's title is cut only when there's no room: who is here then has none to spare (its parts and
 *  their gaps fill it). */
const titleTakesRoom = holds('the title is cut only when there\'s no room', () => {
  const title = document.querySelector('.tide-present-title > span:not(.tide-brand-mark)');
  const people = document.querySelector('.tide-present-people');
  if (!title || !people) return ['no title or people in the top bar'];
  if (title.scrollWidth <= title.clientWidth + 1) return [];
  const kids = [...people.children].filter(e => e.getBoundingClientRect().width > 0);
  const used = kids.reduce((sum, e) => sum + e.getBoundingClientRect().width, 0) + parseFloat(getComputedStyle(people).columnGap || '0') * Math.max(0, kids.length - 1);
  const spare = people.getBoundingClientRect().width - used;
  return spare <= 2 ? [] : [`the title is cut by ${title.scrollWidth - title.clientWidth}px while who is here has ${Math.round(spare)}px to spare`];
});

/** The matches of `selector` (the top bar's title, people and tools) take at most `n` rows, by their tops. */
const rowsAtMost = (selector, n) => holds(`at most ${n} rows: ${selector}`, ([selector, n]) => {
  const rows = [];
  for (const e of document.querySelectorAll(selector)) {
    const r = e.getBoundingClientRect();
    if (r.width === 0) continue;
    if (!rows.some(t => Math.abs(t - r.top) < r.height / 2)) rows.push(r.top);
  }
  return rows.length <= n ? [] : [`${selector}: ${rows.length} rows`];
}, [selector, n]);

/** Exactly `n` matches of `selector` are shown. */
const shownCount = (selector, n) => holds(`${n} shown: ${selector}`, ([selector, n]) => {
  const shown = [...document.querySelectorAll(selector)].filter(e => e.getBoundingClientRect().height > 0).length;
  return shown === n ? [] : [`${shown} of ${selector} shown, not ${n}`];
}, [selector, n]);


/** The slide's body fits, shrunk (present.js's --tide-fit) to no less than 80%, and scrolls only once it's at 80%. */
const slideFits = holds('the slide fits, or scrolls only at 80%', () => {
  const body = document.querySelector('[data-slide-body]');
  if (!body) return ['no slide body'];
  const fit = parseFloat(getComputedStyle(body).getPropertyValue('--tide-fit') || '1');
  const over = body.scrollHeight - body.clientHeight;
  if (fit < 0.799) return [`the slide is shrunk to ${fit}, below 80%`];
  return over > 1 && fit > 0.801 ? [`the slide's body is ${over}px too tall at ${fit}, and could shrink further`] : [];
});

// --- actions that need review ------------------------------------------------------------------------------------------

/** A row of Actions that need review says it has gone too long without an update, and what its last update was. */
const quietRow = holds('a row is quiet too long, with its last update', () => {
  const rows = [...document.querySelectorAll('.tide-slide-open tbody tr')];
  return rows.some(r => /quiet \d+ business days?/.test(r.querySelector('.tide-slide-why')?.textContent ?? '')
    && /^last: /.test(r.querySelector('.tide-slide-waited')?.textContent.trim() ?? ''))
    ? [] : ['no row says it is quiet too long, with its last update'];
});

/** Each row's title starts on its priority badge's line and wraps on from there, as plain text does: it doesn't drop
 *  below the badge as a block of its own (the presenter's and a Visitor's alike). */
const badgeWithTitle = holds('each title starts on its priority badge\'s line', () => {
  const cells = [...document.querySelectorAll('.tide-slide-open tbody td:first-child')];
  if (!cells.length) return ['nothing shown matches .tide-slide-open tbody td:first-child'];
  return cells.flatMap((td, i) => {
    const badge = td.querySelector('.tide-badge')?.getBoundingClientRect();
    const first = td.querySelector('.tide-slide-row-title')?.getClientRects()[0];
    if (!badge || !first) return [`row ${i + 1} has no badge or title`];
    return first.top < badge.bottom && badge.top < first.bottom ? [] : [`row ${i + 1}: its title starts ${Math.round(first.top - badge.top)}px below its badge`];
  });
});

/** Setup: on Actions that need review, the first row's action opened over the slide and closed with Escape. */
const reviewActionClosed = async page => {
  await slideWith('.tide-slide-open')(page);
  await page.locator('.tide-slide-open tbody tr:first-child .tide-slide-row-open').click();
  await page.locator('#present-action-title').waitFor();
  await idle(page);
  await page.keyboard.press('Escape');
  await page.locator('#present-action-title').waitFor({ state: 'detached' });
  await idle(page);
};

// --- the wrap-up -------------------------------------------------------------------------------------------------------

/** The slide's More pill, where it shows, covers none of `selector` that shows in the slide's body: it sits in the room
 *  below the last line, not over it. */
const pillClear = selector => holds(`the More pill covers none of ${selector}`, selector => {
  const pill = document.querySelector('.tide-slide-more');
  const p = pill?.getBoundingClientRect();
  if (!p || p.width === 0 || p.height === 0) return [];
  const body = document.querySelector('[data-slide-body]')?.getBoundingClientRect();
  return [...document.querySelectorAll(selector)].flatMap(el => {
    const r = el.getBoundingClientRect();
    if (r.width === 0 || r.height === 0 || (body && (r.bottom <= body.top || r.top >= body.bottom))) return [];
    // Only the part that shows: what the body's scroll cuts off below it isn't covered.
    const top = Math.max(r.top, body?.top ?? r.top), bottom = Math.min(r.bottom, body?.bottom ?? r.bottom);
    const over = r.left < p.right && p.left < r.right && top < p.bottom && p.top < bottom;
    return over ? [`the More pill (${Math.round(p.top)}-${Math.round(p.bottom)}px) covers "${el.textContent.trim().slice(0, 40)}"`] : [];
  });
}, selector);

/** Setup: wider letters, on the wrap-up. 'presenter': this page, with no meeting, pressing End. 'follower': the dev
 *  analyst starts (or takes up) today's meeting from another browser the size of this page and goes to the wrap-up;
 *  this page joins as Pat, following. */
const wrapUp = as => async (page, demo) => {
  if (as === 'presenter') {
    await wideFont(page);
    await page.keyboard.press('End');
    await page.locator('.tide-wrap').waitFor();
    await idle(page);
    return;
  }
  for (const c of meetingGuests.contexts) await c.close().catch(() => {});
  meetingGuests = { key: 'wrap/follower', contexts: [] };
  const { context, page: presenter } = await openPage(page.context().browser(), { route: '/present/{today}', viewport: page.viewportSize() }, demo);
  meetingGuests.contexts.push(context);
  await startOrTakeUp(presenter);
  await presenter.keyboard.press('End');
  await presenter.locator('.tide-wrap').waitFor();
  await page.goto(`${page.url()}?as=Pat&roles=Viewer`, { waitUntil: 'load' });
  await settle(page);
  await wideFont(page);
  await page.locator('.tide-wrap').waitFor();
  await idle(page);
};

/** The wrap-up's three lists (not raised, who is doing what, not discussed), each whole in its column: side by side from
 *  1400px, and the slide still fits. (Who is doing what is checked with actions in it on the presenter's page, early in
 *  the run: by the follower's, late, the pages before have changed today's brief and it may have none.) */
const wrapUpRules = [noSidewaysScroll, slideFits, shownCount('.tide-wrap > .tide-wrap-list', 3), fits('.tide-wrap'), fits('.tide-wrap-list'),
  notSqueezed('.tide-wrap-list[data-wrap="owners"] .tide-present-help'), [1400, oneRow('.tide-wrap')],
  oneLine('.tide-wrap-list[data-wrap="owners"] .text-nowrap'), pillClear('.tide-wrap-item, .tide-wrap-item li, .tide-attendance li')];

// --- an action beside the slide (Org:ActionBesideSlide, on for this run) -------------------------------------------

const BESIDE = '.tide-present-action.is-beside';

/** What the room's screen shows of an action beside the slide: the slide's and the card's boxes, the slide's fit
 *  (present.js's --tide-fit) and how far its body is scrolled (a fraction, as it's shared), and whether it scrolls. */
const besideGeometry = beside => {
  const box = s => document.querySelector(s)?.getBoundingClientRect();
  const body = document.querySelector('[data-slide-body]');
  const slide = box('#present-root > .tide-slide'), card = box(beside);
  const range = body ? body.scrollHeight - body.clientHeight : 0;
  return {
    slide: slide && Math.round(slide.width), cardLeft: card && Math.round(card.left), card: card && Math.round(card.width),
    fit: body ? parseFloat(getComputedStyle(body).getPropertyValue('--tide-fit') || '1') : null,
    // A few pixels of range is the fit's rounding (present.js stops within half a percent), not a slide that scrolls.
    scrolls: range > 8, at: range > 8 ? body.scrollTop / range : 0,
  };
};

/**
 * Setup: a live meeting with an action open beside the first article's slide. `as` 'presenter': this page presents
 * (its rail as it starts: docked from 1500px) and opens the slide's first action. 'follower': the dev analyst presents
 * from another browser the size of this page, with their rail shut, opens it and scrolls the slide by hand; this page
 * joins as Pat, following, and keeps what the presenter's screen showed (window.presenterGeometry) to compare. Wider
 * letters.
 */
const actionBeside = as => async (page, demo) => {
  for (const c of meetingGuests.contexts) await c.close().catch(() => {});
  meetingGuests = { key: `beside/${as}`, contexts: [] };
  const present = async p => {
    await startOrTakeUp(p);
    await toArticleSlide(p);
    // Unless the page before left one open for the room (over the slide, in a narrow window, it covers the slide's
    // links): the first slide with an action on it, from this article's on and then from the title. The pages before
    // may have changed today's running order; the actions that need review have some, whatever the articles have (each
    // row opens its action from a button over its cell).
    const opener = p.locator('.tide-slide :is(.tide-action-ref-btn, .tide-action-open, .tide-slide-row-open)');
    for (let i = 0; i < 16 && !(await p.locator(BESIDE).count()) && !(await opener.count()); i++) {
      await p.keyboard.press(i === 4 ? 'Home' : 'ArrowRight');
      await idle(p);
    }
    if (!(await p.locator(BESIDE).count())) await opener.first().click();
    await p.locator(BESIDE).waitFor();
    await idle(p);
  };
  if (as === 'presenter') {
    await wideFont(page);
    await present(page);
    return;
  }
  const { context, page: presenter } = await openPage(page.context().browser(), { route: '/present/{today}', viewport: page.viewportSize() }, demo);
  meetingGuests.contexts.push(context);
  await wideFont(presenter);
  if (await presenter.locator('#present-rail.is-docked').count()) {
    await presenter.keyboard.press(']');
    await presenter.locator('#present-rail').waitFor({ state: 'detached' });
  }
  await present(presenter);
  await page.goto(`${page.url()}?as=Pat&roles=Analyst`, { waitUntil: 'load' });
  await settle(page);
  await wideFont(page);
  await page.locator(BESIDE).waitFor();
  await idle(page);
  // Scrolled by hand (the wheel), as the presenter would: what's shared with the room.
  const body = presenter.locator('[data-slide-body]');
  if (await body.evaluate(b => b.scrollHeight > b.clientHeight + 8)) {
    // Up to the top first: the meeting keeps the last place shared on this slide (an earlier width's), and a scroll to
    // where it already is isn't passed on again.
    await body.hover();
    await presenter.mouse.wheel(0, -2000);
    await idle(presenter);
    await presenter.mouse.wheel(0, 240);
    await idle(presenter);
    // The room follows within moments: waited for here, so the rule compares where each settled.
    const at = await body.evaluate(b => b.scrollTop / (b.scrollHeight - b.clientHeight));
    await page.waitForFunction(at => {
      const b = document.querySelector('[data-slide-body]'), range = b.scrollHeight - b.clientHeight;
      return range > 8 && Math.abs(b.scrollTop / range - at) < 0.03;
    }, at, { timeout: 6000 }).catch(() => {});
  }
  const geometry = await presenter.evaluate(besideGeometry, BESIDE);
  await page.evaluate(g => { window.presenterGeometry = g; }, geometry);
};

/** A follower's screen shows the action beside the slide as the presenter's does: the same slide and card widths, the
 *  same fit, and the slide scrolled as far (a fraction). */
const matchesPresenter = {
  name: 'the slide, the card, the fit and the scroll as the presenter has them',
  check: async page => {
    const theirs = await page.evaluate(() => window.presenterGeometry), mine = await page.evaluate(besideGeometry, BESIDE);
    if (!theirs) return ['the presenter\'s geometry wasn\'t kept'];
    const off = [];
    for (const k of ['slide', 'cardLeft', 'card']) if (Math.abs((mine[k] ?? -99) - (theirs[k] ?? 99)) > 1) off.push(`${k} ${mine[k]}px, the presenter's ${theirs[k]}px`);
    if (Math.abs(mine.fit - theirs.fit) > 0.006) off.push(`fit ${mine.fit}, the presenter's ${theirs.fit}`);
    if (mine.scrolls !== theirs.scrolls) off.push(`scrolls: ${mine.scrolls}, the presenter's ${theirs.scrolls}`);
    else if (Math.abs(mine.at - theirs.at) > 0.03) off.push(`scrolled to ${mine.at.toFixed(2)}, the presenter's ${theirs.at.toFixed(2)}`);
    return off;
  },
};

/** Each of the action's facts beside the slide has room for its words (Due, Assignee, Team...): none narrower than 7em. */
const besideFactsRoomy = holds('the action facts beside the slide have room', beside =>
  [...document.querySelectorAll(`${beside} .tide-fact`)].flatMap((el, i) => {
    const w = el.getBoundingClientRect().width, em = parseFloat(getComputedStyle(el).fontSize);
    return w > 0 && w < 7 * em ? [`fact #${i + 1} ("${el.querySelector('dt')?.textContent}") is ${Math.round(w)}px wide`] : [];
  }), BESIDE);

/** The status choices beside the slide fill their rows: the last ends at the group's right edge, no blank cell after it. */
const besideStatusFilled = holds('the action’s status choices fill their rows', beside => {
  const seg = document.querySelector(`${beside} .tide-status-seg`);
  if (!seg) return ['no status choices shown'];
  const last = [...seg.children].at(-1).getBoundingClientRect(), box = seg.getBoundingClientRect();
  return box.right - last.right > 3 ? [`the last choice ends ${Math.round(box.right - last.right)}px short of the row's end: a blank cell`] : [];
}, BESIDE);

/** The action opens over the slide, not beside it: a narrow window. */
const actionOver = holds('the action is over the slide', () => {
  const card = document.querySelector('.tide-present-action');
  if (!card) return ['no action open'];
  const slide = document.querySelector('#present-root > .tide-slide').getBoundingClientRect();
  return getComputedStyle(card).position === 'fixed' && Math.round(slide.width) >= window.innerWidth - 2 ? [] : ['the action takes a column of its own'];
});

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

/** `rule`, checked once the page is still again: what the live page draws, after the prerendered one. */
const later = rule => ({ name: rule.name, check: async page => { await idle(page); return rule.check(page); } });

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
 * Tab from the selected row goes through the row's own controls, then the reading pane's head tools (Read it here or
 * Read again, Open the site), then Triage's decision bar, its main button first and the rest in the order they're
 * shown (not past every other row's links on the way). From the bar's last control Tab goes on to the next row, as it
 * did when the buttons were in the row, and Shift+Tab from there comes back to the bar; Shift+Tab from the bar's
 * first goes back to the head tools.
 */
const tabReachesBar = {
  name: 'Tab from the selected row: head tools, then the decision bar, then the next row',
  check: async page => {
    const where = () => page.evaluate(() => {
      const a = document.activeElement, r = a.getBoundingClientRect();
      const row = a.closest('.tide-triage-row');
      return { inRow: !!a.closest('.tide-triage-row.selected'), inTools: !!a.closest('.tide-reading-tools'), inBar: !!a.closest('.tide-triage-decide'),
        nextRow: !!row && row.previousElementSibling?.classList.contains('selected'), primary: a.classList.contains('btn-primary'),
        x: Math.round(r.left), y: Math.round(r.top), text: (a.innerText || a.getAttribute('aria-label') || a.tagName).replace(/\s+/g, ' ').trim().slice(0, 40) };
    });
    await page.locator('.tide-triage-row.selected').focus();
    const seen = [];
    for (let i = 0; i < 40; i++) {
      await page.keyboard.press('Tab');
      const at = await where();
      if (!at.inRow) seen.push(at);
      if (!at.inRow && !at.inTools && !at.inBar) break;
    }
    const tools = seen.filter(s => s.inTools), bar = seen.filter(s => s.inBar), last = seen[seen.length - 1];
    const order = seen.map(s => s.inTools ? 'tools' : s.inBar ? 'bar' : 'other');
    if (!tools.length || order.indexOf('bar') < order.lastIndexOf('tools') || order[0] !== 'tools')
      return [`Tab from the selected row goes: ${seen.map(s => `"${s.text}"`).join(', ')}; not the head tools, then the bar`];
    if (!bar.length || !bar[0].primary) return [`the first of the bar Tab reaches is "${bar[0]?.text}", not its main button`];
    const backwards = bar.slice(1).filter((s, i) => s.y === bar[i].y && s.x <= bar[i].x);
    if (backwards.length) return [`Tab goes back along the bar's row: "${bar.map(s => s.text).join('", "')}"`];
    if (!last?.nextRow) return [`after the bar's last control Tab goes to "${last?.text}", not the next row`];
    await page.keyboard.press('Shift+Tab');
    const fromNext = await where();
    if (!fromNext.inBar) return [`Shift+Tab from the next row goes to "${fromNext.text}", not back to the bar`];
    await page.locator('.tide-triage-decide .btn-primary').first().focus();
    await page.keyboard.press('Shift+Tab');
    const back = await where();
    return back.inTools ? [] : [`Shift+Tab from the bar's main button goes to "${back.text}", not back to the pane's head tools`];
  },
};

/**
 * Each coverage flag on a line of Triage's list (a report's or a group's), one line with oneLine: its article's title
 * clipped with an ellipsis rather than wrapped, and the whole text ("CVE-… covered Wed Nov 11: the whole title") its
 * tooltip and its link's name.
 */
const coverageCutWhole = holds("each coverage flag's title is cut with an ellipsis and whole in its tooltip", () => {
  const flags = [...document.querySelectorAll('.tide-triage-cov')].filter(e => e.getBoundingClientRect().height > 0);
  if (!flags.length) return ['nothing shown matches .tide-triage-cov'];
  return flags.flatMap((flag, i) => {
    const a = flag.querySelector('a'), problems = [];
    if (!a) return [`.tide-triage-cov #${i + 1} has no link to its article`];
    const ls = getComputedStyle(a);
    if (ls.whiteSpace !== 'nowrap' || ls.textOverflow !== 'ellipsis' || ls.overflow !== 'hidden') problems.push(`its title wraps or is cut without an ellipsis (${ls.whiteSpace}, ${ls.textOverflow}, ${ls.overflow})`);
    if (a.getBoundingClientRect().right > flag.getBoundingClientRect().right + 1) problems.push('its title spills out of it');
    const title = flag.getAttribute('title') ?? '';
    if (!/ covered( \w{3} \w{3} \d{1,2})?: /.test(title) || !title.endsWith(': ' + a.textContent.trim())) problems.push(`its tooltip isn't the whole text ("${title.slice(0, 50)}")`);
    if (a.getAttribute('aria-label') !== title) problems.push('its link isn\'t named with the whole text');
    return problems.map(p => `.tide-triage-cov #${i + 1}: ${p}`);
  });
});

/** A line's "We run" (and "Watching") chips sit on its CVE chips' line, at its end, where there's room for them there. */
const weRunBesideTheCves = holds('the We run chip is on the CVEs\' line where there is room', () => {
  const lines = [...document.querySelectorAll('.tide-triage-meta')].filter(m => m.getBoundingClientRect().height > 0 && m.querySelector('.tide-werun-chip') && m.querySelector('.tide-cve'));
  if (!lines.length) return ['no line shown names a CVE and something we run'];
  return lines.flatMap((meta, i) => {
    const box = meta.querySelector('.tide-werun-chip').closest('.tide-triage-flags'), kids = [...meta.children];
    const flags = box.querySelector('.tide-werun-chip').getBoundingClientRect(), cve = [...meta.querySelectorAll('.tide-cve')].at(-1).getBoundingClientRect();
    if (flags.top < cve.bottom && flags.bottom > cve.top) return [];
    // What ends the CVEs' line: the rightmost of what comes before the flags on it.
    const before = kids.slice(0, kids.indexOf(box)).map(e => e.getBoundingClientRect())
      .filter(r => r.height > 0 && r.top < cve.bottom && r.bottom > cve.top);
    const room = meta.getBoundingClientRect().right - Math.max(...before.map(r => r.right)) - (parseFloat(getComputedStyle(meta).columnGap) || 0);
    // The chips' own width (with the gaps between them), not their box's, which may hold more.
    const chips = [...box.querySelectorAll('.tide-werun-chip')];
    const need = chips.reduce((w, c) => w + c.getBoundingClientRect().width, 0) + (chips.length - 1) * (parseFloat(getComputedStyle(box).columnGap) || 0);
    return room >= need ? [`line #${i + 1}: We run is on a line of its own, with ${Math.round(room)}px free beside the CVEs for its ${Math.round(need)}px`] : [];
  });
});

/**
 * The decision bar's main button keeps its words: its visible text starts with its whole verb ("Attach to", "Attach
 * all 2 to", "Promote"), the button itself isn't cut, the article's title in it ends in an ellipsis when it's cut, and
 * the button is at least as wide as any other button in the bar: the others give way first.
 */
const mainButtonKeepsItsWords = holds('the main button keeps its verb, cuts only the title, and is the widest', () => {
  const bar = [...document.querySelectorAll('.tide-reading .tide-triage-decide')].find(e => e.getBoundingClientRect().height > 0);
  const main = bar?.querySelector('.tide-triage-acts .btn-primary');
  if (!main) return ['no main button in a shown decision bar'];
  const problems = [];
  const verb = /^(Attach all \d+ to|Attach to|Promote)/;
  const range = document.createRange();
  // What's on screen of the button's own words (outside the title): each text node, wholly inside the button's box.
  for (const node of [...main.childNodes].filter(n => n.nodeType === 3 && n.textContent.trim())) {
    range.selectNodeContents(node);
    const r = range.getBoundingClientRect(), b = main.getBoundingClientRect();
    if (r.right > b.right - 2) problems.push(`the main button's words "${node.textContent.trim()}" are cut off (they end at ${Math.round(r.right)}px, the button at ${Math.round(b.right)}px)`);
  }
  const text = main.innerText.replace(/\s+/g, ' ').trim();
  if (!verb.test(text)) problems.push(`the main button reads "${text}", not its whole verb`);
  if (main.scrollWidth > main.clientWidth + 1) problems.push(`the main button's content is ${main.scrollWidth - main.clientWidth}px wider than it`);
  const title = main.querySelector('.tide-triage-attach-title');
  if (title && title.scrollWidth > title.clientWidth + 1) {
    if (getComputedStyle(title).textOverflow !== 'ellipsis') problems.push('the article title is cut without an ellipsis');
    if (title.clientWidth < 16) problems.push(`the article title has ${title.clientWidth}px, too little to show its ellipsis`);
  }
  const width = main.getBoundingClientRect().width;
  for (const other of bar.querySelectorAll('.btn:not(.btn-primary)')) {
    const w = other.getBoundingClientRect().width;
    if (w > width + 1) problems.push(`"${other.innerText.replace(/\s+/g, ' ').trim()}" is ${Math.round(w)}px wide, wider than the main button's ${Math.round(width)}px`);
  }
  return problems;
});

/** In the full-height page the window doesn't scroll: the document is no taller than the window (give or take 4px). */
const windowDoesntScroll = holds('the window does not scroll', () => {
  const over = document.scrollingElement.scrollHeight - window.innerHeight;
  return over <= 4 ? [] : [`the document is ${document.scrollingElement.scrollHeight}px tall, ${over}px more than the window`];
});

/**
 * The reading pane is as tall as what it holds: its decision bar starts within `px` of the end of what's above it (the
 * text, or what was found in it), with no blank space between them; or, for a short item, the pane is at its least
 * height (app.css: what an item not read yet takes), which keeps the bar where it was from item to item.
 */
const barFollowsText = px => holds(`the decision bar follows the pane's text within ${px}px`, px => {
  const bar = [...document.querySelectorAll('.tide-reading > .tide-triage-decide')].find(e => e.getBoundingClientRect().height > 0);
  if (!bar) return ['no decision bar shown in the reading pane'];
  // At its least height: without that least, the pane would be shorter.
  const pane = bar.parentElement, height = pane.getBoundingClientRect().height;
  pane.style.minHeight = '0';
  const natural = pane.getBoundingClientRect().height;
  pane.style.minHeight = '';
  if (natural < height - 1) return [];
  let before = bar.previousElementSibling;
  while (before && before.getBoundingClientRect().height === 0) before = before.previousElementSibling;
  if (!before) return ['nothing above the decision bar'];
  const gap = Math.round(bar.getBoundingClientRect().top - before.getBoundingClientRect().bottom);
  return gap <= px ? [] : [`the decision bar starts ${gap}px below ${before.className || before.tagName}, more than ${px}px`];
}, px);

/**
 * Every button and select of the first shown match of `selector` (a decision: its main buttons, Attach to…, the dismiss
 * reason and Dismiss; not the extras that may go on a second row) shares one horizontal band: one row.
 */
const controlsOneRow = selector => holds(`controls on one row: ${selector}`, selector => {
  const box = [...document.querySelectorAll(selector)].find(e => e.getBoundingClientRect().height > 0);
  if (!box) return [`nothing shown matches ${selector}`];
  const controls = [...box.querySelectorAll('.btn, select')].filter(e => e.matches('.tide-attach > .btn') || !e.closest('.tide-triage-more, .tide-attach-panel'))
    .map(e => [e, e.getBoundingClientRect()]).filter(([, r]) => r.height > 0);
  if (controls.length < 2) return [`fewer than two controls shown in ${selector}`];
  const top = Math.max(...controls.map(([, r]) => r.top)), bottom = Math.min(...controls.map(([, r]) => r.bottom));
  if (top < bottom - 2) return [];
  return [`${selector}: its ${controls.length} controls aren't on one row (${controls.map(([e]) => `"${e.innerText.replace(/\s+/g, ' ').trim().slice(0, 16)}"`).join(', ')})`];
}, selector);

/** The selected row's excerpt keeps to a readable measure: at most about 80ch wide and `lines` lines tall. */
const excerptMeasure = lines => holds(`the selected row's excerpt is at most 80ch and ${lines} lines`, lines => {
  const ex = [...document.querySelectorAll('.tide-triage-row.selected .tide-triage-excerpt')].find(e => e.getBoundingClientRect().height > 0);
  if (!ex) return ['no excerpt shown in the selected row'];
  const probe = document.createElement('span');
  probe.style.cssText = 'position: absolute; visibility: hidden; display: block; width: 80ch';
  ex.append(probe);
  const measure = probe.getBoundingClientRect().width;
  probe.remove();
  const style = getComputedStyle(ex), r = ex.getBoundingClientRect();
  const line = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.5;
  const problems = [];
  if (r.width > measure + 2) problems.push(`the excerpt is ${Math.round(r.width)}px wide, more than 80ch (${Math.round(measure)}px)`);
  if (r.height > lines * line + 2) problems.push(`the excerpt is ${Math.round(r.height)}px tall, more than ${lines} lines of ${Math.round(line)}px`);
  return problems;
}, lines);

/**
 * In the selected row, the decision is compact: its why line and one row of buttons, no taller than `px` (a line of
 * small text, a row of small buttons and the gaps between).
 */
const rowDecisionCompact = px => holds(`the selected row's decision is at most ${px}px tall`, px => {
  const d = [...document.querySelectorAll('.tide-triage-row.selected .tide-triage-decide')].find(e => e.getBoundingClientRect().height > 0);
  if (!d) return ['no decision shown in the selected row'];
  const h = Math.round(d.getBoundingClientRect().height);
  return h <= px ? [] : [`the selected row's decision is ${h}px tall, more than ${px}px`];
}, px);

/**
 * Moving down the list with J, from item to item that hasn't been read here, the decision bar's buttons stay where they
 * were (within `px`): what's under the mouse doesn't jump. Each item's own buttons differ; their row doesn't move. (An
 * item with extra buttons, on a row of their own under the bar's, has its bar that row higher, and isn't compared.)
 */
const barStaysPut = (steps, px) => ({
  name: `the decision bar stays put over ${steps} presses of J (within ${px}px)`,
  check: async page => {
    const tops = [];
    await page.locator('.tide-triage-row.selected').focus();
    for (let i = 0; i <= steps; i++) {
      if (i) await pressJ(page);
      tops.push(await page.evaluate(() => {
        const read = document.querySelector('.tide-reading .tide-reading-text');
        const acts = document.querySelector('.tide-reading .tide-triage-acts');
        const extras = document.querySelector('.tide-reading .tide-triage-more')?.getBoundingClientRect().height > 0;
        return read || extras || !acts ? null : Math.round(acts.getBoundingClientRect().top);
      }));
    }
    const shown = tops.filter(t => t !== null);
    if (shown.length < 2) return [`fewer than two unread items with a bar over ${steps} presses of J`];
    return Math.max(...shown) - Math.min(...shown) <= px ? [] : [`the bar's buttons moved between ${Math.min(...shown)}px and ${Math.max(...shown)}px down the window (${tops.join(', ')})`];
  },
});

/** J pressed on the selected row, then a wait until the next row is selected and the reading pane has caught up. */
async function pressJ(page) {
  const was = await page.locator('.tide-triage-row.selected').getAttribute('aria-label');
  await page.keyboard.press('j');
  await page.waitForFunction(was => document.querySelector('.tide-triage-row.selected')?.getAttribute('aria-label') !== was
    && document.querySelector('.tide-reading')?.getAttribute('aria-busy') !== 'true', was, { timeout: 10000 });
  await idle(page);
}

/** Setup: J pressed down to the first single report (not a group) that hasn't been read here, the pane beside it. */
async function selectUnread(page) {
  const unread = () => page.evaluate(() => !document.querySelector('.tide-reading .tide-reading-text') && !document.querySelector('.tide-triage-group.selected'));
  await page.locator('.tide-triage-row.selected').focus();
  for (let i = 0; i < 12 && !(await unread()); i++) await pressJ(page);
}

/** Setup: the account menu opened from the keyboard (Enter on the avatar), then the down arrow pressed on the avatar. */
async function accountMenuArrow(page) {
  await page.locator('.tide-profile-btn').focus();
  await page.keyboard.press('Enter');
  await page.locator('.tide-profile [role="menu"]').waitFor();
  await page.keyboard.press('ArrowDown');
  await idle(page);
}

/** The down arrow on the open account menu's avatar went into the menu: it's still open, focus is in it, and Triage's
 * selection stayed on its first row (the address names no other). */
const arrowIntoAccountMenu = holds('the down arrow goes into the account menu, not the list under it', () => {
  const pop = document.querySelector('.tide-profile [role="menu"]');
  if (!pop) return ['the account menu closed'];
  const wrong = [];
  if (!pop.contains(document.activeElement)) wrong.push(`focus is on ${document.activeElement?.outerHTML.slice(0, 90)}, not in the menu`);
  if (new URLSearchParams(location.search).has('row')) wrong.push(`Triage's selection moved (${location.search})`);
  return wrong;
});

/** Setup: the first "Looks like one event" group opened, its reports listed under it. */
async function openGroup(page) {
  await page.locator('.tide-triage-group .tide-group-toggle').first().click();
  await page.locator('.tide-triage-member').first().waitFor();
}

/**
 * In an open group, no report's "Looks like" flag names another report of the same group: the group already says they
 * look alike. Its Duplicate button stays, in the decision, once the report is selected.
 */
const noLooksLikeInItsGroup = {
  name: "an open group's reports don't flag each other as \"Looks like\"",
  check: async page => {
    const problems = await page.evaluate(() => {
      const out = [];
      for (const group of document.querySelectorAll('.tide-triage-group')) {
        const members = [];
        for (let li = group.nextElementSibling; li?.classList.contains('tide-triage-member'); li = li.nextElementSibling) members.push(li);
        const title = m => m.querySelector('.tide-triage-title')?.innerText.trim();
        const titles = new Set(members.map(title));
        for (const m of members)
          for (const flag of m.querySelectorAll('.tide-triage-flags .tide-badge-warn[title]'))
            if (/^Looks like/.test(flag.innerText.trim()) && titles.has(flag.getAttribute('title').trim()))
              out.push(`"${title(m)}" is flagged as looking like "${flag.getAttribute('title')}", a report of its own group`);
      }
      return out;
    });
    // The report that looks like another still offers Duplicate (D) once selected.
    const members = page.locator('.tide-triage-member');
    if (!(await members.count())) return ['no open group to look in'];
    let duplicate = false;
    for (let i = 0, n = await members.count(); i < n && !duplicate; i++) {
      await members.nth(i).locator('.tide-triage-meta > span').first().click();
      await page.locator('.tide-triage-member.selected .tide-triage-decide, .tide-reading .tide-triage-decide').first().waitFor();
      await idle(page);
      duplicate = await page.locator('.tide-triage-decide button', { hasText: /^Duplicate/ }).first().isVisible().catch(() => false);
    }
    if (!duplicate) problems.push("no report of the open group offers Duplicate once selected");
    return problems;
  },
};

/**
 * Setup: a colleague's presence on a row far down the list, as PresenceNote draws it (its words for screen readers in a
 * visually hidden span, positioned absolutely), then End pressed and the wheel turned over the pane's head and over the
 * page's header.
 */
async function endAndWheel(page) {
  await page.$eval('.tide-triage-row:last-of-type .tide-triage-head', head => {
    const note = document.createElement('span');
    note.className = 'visually-hidden';
    note.textContent = 'Robin is looking at this item';
    head.append(note);
  });
  await page.keyboard.press('End');
  await idle(page);
  for (const s of ['.tide-reading-head', '.tide-page-head']) {
    const box = await page.locator(s).first().boundingBox();
    if (box) { await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2); await page.mouse.wheel(0, 1500); await idle(page); }
  }
}

/**
 * A clicked decision behaves as its key: after a click on the bar's `label` button (Shortlist, Dismiss), focus is on the
 * row selected next, not left on the bar's button (which now belongs to the next item) or dropped on the page.
 */
const clickFocusesNextRow = label => ({
  name: `a click on ${label} puts focus on the next selected row`,
  check: async page => {
    const before = await page.locator('.tide-triage-row.selected').getAttribute('aria-label');
    await page.locator('.tide-reading .tide-triage-acts button', { hasText: new RegExp(`^${label}`) }).first().click();
    await page.waitForFunction(was => document.querySelector('.tide-triage-row.selected')?.getAttribute('aria-label') !== was, before, { timeout: 10000 });
    await idle(page);
    const focus = await page.evaluate(() => {
      const a = document.activeElement;
      return a.classList.contains('selected') && a.classList.contains('tide-triage-row') ? null : `${a.tagName}${a.className ? '.' + a.className.split(' ').join('.') : ''} "${(a.innerText || '').trim().slice(0, 30)}"`;
    });
    return focus ? [`after the click on ${label} focus is on ${focus}, not the next selected row`] : [];
  },
});

/** Setup: the first single report (not a group) selected, by a click on its line. */
async function selectReport(page) {
  await page.locator('.tide-triage-row:not(.tide-triage-group):not(.tide-triage-member) .tide-triage-meta > span').first().click();
  await page.locator('.tide-reading .tide-triage-acts').waitFor();
}

/**
 * Setup: a long kept text in the reading pane, as an article read in full leaves (the demo keeps only short ones): the
 * selected item's text is filled out to a few thousand words in the page. Letters spaced wider too.
 */
async function longKeptText(page) {
  await wider(page);
  await page.locator('.tide-reading .tide-reading-text').waitFor();
  await page.$eval('.tide-reading .tide-reading-text', text => {
    const paragraph = 'The vendor has released fixed builds for an authentication bypass in the web interface of its managed file '
      + 'transfer server. An unauthenticated attacker who can reach the interface can create an administrator session. ';
    text.textContent = Array.from({ length: 40 }, () => paragraph).join('\n\n');
  });
  // And a long title, which wraps beside the head's tools.
  await page.$eval('.tide-reading .tide-reading-title', title => {
    title.textContent = 'Vendor patches an authentication bypass in its managed file transfer server, exploited against manufacturers in three countries';
  });
  await idle(page);
}

/** The first visible match of `selector` scrolls inside itself (its content is taller than its box). */
const scrollsInside = selector => holds(`scrolls inside itself: ${selector}`, selector => {
  const el = [...document.querySelectorAll(selector)].find(e => e.getBoundingClientRect().height > 0);
  if (!el) return [`nothing shown matches ${selector}`];
  return el.scrollHeight > el.clientHeight + 1 ? [] : [`${selector} is as tall as its content (${el.clientHeight}px): it doesn't scroll inside itself`];
}, selector);

/** Setup: the page scrolled well down, past the top of the list. */
async function scrollDown(page) {
  await page.mouse.wheel(0, 1500);
  await idle(page);
}

// --- Adversaries' context rail -----------------------------------------------------------------------------------------

/** Adversaries' selected row, as the keys move it (as Our stack's). */
const adversaryKeys = listKeys({ name: 'Adversaries: J, K, a click and Enter', row: '.tide-adv-item', loaded: '#rail-lately-h',
  path: /\/adversaries\/[0-9a-f-]{36}$/, min: 2, clickAt: 1 });

/** The rail shows no marked text: a profile's notes (TLP:AMBER, the demo's all say "for the demo") stay on its page. */
const railHasNoNotes = holds('the rail has no AMBER notes', () => {
  const rail = document.querySelector('#tide-rail');
  if (!rail) return ['there is no rail'];
  return /for the demo|TLP:AMBER/i.test(rail.textContent) ? ['the rail shows the profile\'s notes or a marking'] : [];
});

/** What either Adversaries rail holds, docked: flat sections that each fit the column, buttons as Our stack's. */
const adversaryRailHolds = [noSidewaysScroll, visible('#tide-rail .tide-card'), besides('#main-content', '#tide-rail'), attr('#tide-rail', 'role', null),
  visible('#tide-rail .tide-rail-head kbd'), visible('#tide-rail-close'), flat('#tide-rail .tide-rail-body > section'), fits('.tide-rail-body'),
  fits('#tide-rail .tide-rail-body > section'), fits('#tide-rail .tide-stack-rail-buttons'), fits('#tide-rail .tide-facts'),
  visible('#tide-rail .tide-facts'), visible('#tide-rail section[aria-labelledby="rail-werun-h"]'),
  ringOnFocus('#tide-rail .tide-stack-rail-buttons a, #tide-rail .tide-stack-rail-buttons button'), railHasNoNotes];

/** What either Adversaries rail holds as an overlay (below 1500px): a modal dialog that keeps Tab, its sections fitting. */
const adversaryRailOverHolds = [noSidewaysScroll, visible('#tide-rail .tide-card'), fits('.tide-rail-body'), fits('#tide-rail .tide-rail-body > section'),
  focusIn('#tide-rail'), attr('#tide-rail', 'role', 'dialog'), attr('#tide-rail', 'aria-modal', 'true'), tabStaysIn('#tide-rail', 30), railHasNoNotes];

// --- the action page's context rail (option E 3.5) ---------------------------------------------------------------------

/** The open overlay closes by `how` ('Escape', 'Close' or 'backdrop'), and focus goes back to the Context button. Run
 *  last on its page: it closes the rail. */
const overlayClosesBy = how => ({
  name: `${how} closes the overlay, focus back on Context`,
  check: async page => {
    if (how === 'Escape') await page.keyboard.press('Escape');
    else if (how === 'Close') await page.locator('#tide-rail-close').click();
    else {
      const rail = await page.locator('#tide-rail').boundingBox();
      await page.mouse.click(Math.max(2, rail.x / 2), rail.y + rail.height / 2);
    }
    await idle(page);
    const problems = [];
    if (await anyShown(page, '#tide-rail')) problems.push(`the rail is still open after ${how}`);
    const at = await page.evaluate(() => document.activeElement?.id || document.activeElement?.tagName);
    if (at !== 'tide-rail-toggle') problems.push(`focus is on ${at}, not the Context button`);
    return problems;
  },
});

/** A file dropped on the rail's Files box, and an image pasted while it has focus, wait there to be added. */
const railFilesTakeDropAndPaste = {
  name: 'the rail\'s Files box takes a drop and a paste',
  check: async page => {
    const zone = '#tide-rail .tide-file-pick';
    await fire(page, 'dragover', zone, [['rail-drop.log', 'text/plain']]);
    await fire(page, 'drop', zone, [['rail-drop.log', 'text/plain']]);
    await waitForPending(page, 'rail-drop.log');
    await page.locator(zone).first().focus();
    await fire(page, 'paste', zone, [['image.png', 'image/png']]);
    await waitForPending(page, 'pasted-screenshot');
    const pending = await pendingFiles(page);
    return [!pending.includes('rail-drop.log') && 'a dropped file isn\'t waiting in the rail', !pending.includes('pasted-screenshot') && 'a pasted image isn\'t waiting in the rail']
      .filter(Boolean);
  },
};

/** What the action page's rail holds, each in the rail: the facts, the article it was raised from, what else was
 * raised from that article, and the Files, with nothing wider than the rail's column. */
const actionRailHolds = [visible('#tide-rail .tide-facts'), visible('#tide-rail #rail-article-h'), visible('#tide-rail #rail-raised-h'),
  visible('#tide-rail #files'), hidden('.content .tide-facts'), hidden('.content #files'), fits('.tide-rail-body'), fits('.tide-rail-body > *')];

// --- the Actions list and board's context rail (option E 3.4) ----------------------------------------------------------

/** The board's selected card: its place among the cards, the one with focus, its lane, and what the address names. */
const selectedCard = page => page.evaluate(() => {
  const cards = [...document.querySelectorAll('.tide-kcard')];
  const on = cards.find(c => c.classList.contains('selected'));
  return {
    at: cards.indexOf(on), focused: cards.indexOf(document.activeElement), lane: on?.closest('.tide-kcol')?.dataset.status ?? null,
    number: on?.querySelector('.tide-action-num')?.innerText.trim() ?? null, card: new URL(location.href).searchParams.get('card'),
    many: on ? on.getAttribute('aria-current') === 'true' && on.tabIndex === 0 && cards.filter(c => c.tabIndex === 0).length === 1 : false,
  };
});

/** The rail is named for the selected row or card ("About A-12"), so it follows the selection. */
const railFollowsSelection = {
  name: 'the rail is about the selected action',
  check: page => page.evaluate(() => {
    const on = document.querySelector('.tide-action-row.selected, .tide-kcard.selected');
    const number = on?.querySelector('.tide-action-num')?.innerText.trim().replace(/\s+/g, '');
    const label = document.getElementById('tide-rail')?.getAttribute('aria-label');
    if (!on || !number) return ['nothing is selected'];
    return label === `About ${number}` ? [] : [`the rail is "${label}", not about ${number}`];
  }),
};

/**
 * The board's keys: the selected card is the one tab stop, and J, K and the arrows move it within and across lanes, focus
 * following and the address naming it (?card=); the rail follows.
 */
const boardKeys = {
  name: 'J, K and the arrows move the selected card, within and across lanes',
  check: async page => {
    const problems = [];
    const start = await selectedCard(page);
    if (start.at < 0) return ['no card is selected'];
    if (!start.many) problems.push('the selected card isn\'t the one tab stop (aria-current, tabindex 0)');
    await page.locator('.tide-kcard.selected').focus();
    const lanes = await page.$$eval('.tide-kcol', ls => ls.map(l => [l.dataset.status, l.querySelectorAll('.tide-kcard').length]));
    for (const key of ['j', 'ArrowDown', 'k', 'ArrowUp', 'ArrowRight', 'ArrowLeft']) {
      const before = await selectedCard(page);
      await page.keyboard.press(key);
      await idle(page);
      const now = await selectedCard(page);
      const moved = now.at !== before.at;
      const canMove = key === 'ArrowRight' ? lanes.some(([s, n], i) => n > 0 && i > lanes.findIndex(([l]) => l === before.lane))
        : key === 'ArrowLeft' ? lanes.some(([s, n], i) => n > 0 && i < lanes.findIndex(([l]) => l === before.lane))
          : ['j', 'ArrowDown'].includes(key) ? before.at < (await page.locator('.tide-kcard').count()) - 1 : before.at > 0;
      if (canMove && !moved) problems.push(`${key} left card ${before.number} selected`);
      if (['ArrowRight', 'ArrowLeft'].includes(key) && moved && now.lane === before.lane) problems.push(`${key} stayed in the ${now.lane} lane`);
      if (now.focused !== now.at) problems.push(`after ${key} card ${now.number} is selected but focus is elsewhere`);
      if (now.card !== now.number?.replace(/^A-/, '')) problems.push(`after ${key} the address says card=${now.card}, not ${now.number}`);
      problems.push(...await railFollowsSelection.check(page));
    }
    return problems;
  },
};

/** A click on a card (not on its links or buttons) selects it and gives it focus; the rail follows. */
const clickSelectsCard = {
  name: 'a click on a card selects it and focuses it',
  check: async page => {
    const cards = page.locator('.tide-kcard');
    if (await cards.count() < 2) return ['fewer than two cards'];
    const target = cards.nth(1);
    const number = (await target.locator('.tide-action-num').innerText()).trim();
    await target.locator('.tide-kcard-head').click({ position: { x: 4, y: 4 } });
    await idle(page);
    const now = await selectedCard(page);
    const problems = [];
    if (now.number !== number) problems.push(`clicking ${number} left ${now.number} selected`);
    if (now.focused !== now.at) problems.push(`clicking ${number} didn't give it focus`);
    problems.push(...await railFollowsSelection.check(page));
    return problems;
  },
};

/** Setup: a new action nobody has, raised from the form, and the board opened on it (the demo's own unowned actions are
 *  taken by the list's rules before this one runs). */
async function newUnownedCard(page) {
  const title = `Board take check ${Date.now().toString(36)}`;
  await page.goto(new URL('/actions/new', page.url()).href, { waitUntil: 'load' });
  await settle(page);
  await page.locator('#a-title').fill(title);
  await page.locator('#a-team').selectOption({ index: 1 });
  await idle(page);
  await page.getByRole('button', { name: 'Create action' }).click();
  await page.waitForURL(/actions\/\d+/);
  await page.goto(new URL(`/actions?view=open&layout=board&q=${encodeURIComponent(title)}`, page.url()).href, { waitUntil: 'load' });
}

/** T on the selected card takes it on: the toast says so. */
const boardTakeKey = {
  name: 'T takes the selected card on',
  check: async page => {
    const number = (await page.locator('.tide-kcard.selected .tide-action-num').innerText()).trim();
    await page.locator('.tide-kcard.selected').focus();
    await page.keyboard.press('t');
    try { await page.getByText(`You took ${number} on.`).waitFor({ timeout: 5000 }); } catch { return [`T on ${number} didn't take it on`]; }
    await idle(page);
    const note = await page.locator('.tide-kcard.selected .tide-kcard-note').allInnerTexts();
    return note.length ? [`the card ${number} the person took says "${note.join(' ').trim()}"`] : [];
  },
};

/** Every tab of the row `selector` is wholly in view, the row not scrolling inside itself: all five views fit. */
const everyTabShown = selector => ({
  name: `every tab shown whole: ${selector}`,
  check: page => page.$eval(selector, row => {
    const r = row.getBoundingClientRect(), problems = [];
    if (row.scrollWidth > row.clientWidth + 1) problems.push(`the tabs scroll inside their row (${row.scrollWidth - row.clientWidth}px more than it shows)`);
    for (const t of row.children) {
      const b = t.getBoundingClientRect();
      if (b.left < r.left - 1 || b.right > r.right + 1) problems.push(`"${t.innerText.replace(/\s+/g, ' ').trim()}" is cut off`);
    }
    return problems;
  }),
});

/** A table, not stacked rows: each heading shown sits over its column (its left edge within 2px of the first row's cell
 *  in the same column), and the header row is shown at all. */
const headingsOverColumns = {
  name: 'the list\'s headings sit over their columns',
  check: page => page.evaluate(() => {
    const table = document.querySelector('.tide-table:has(.tide-action-row)');
    const row = table?.querySelector('.tide-action-row');
    if (!table || !row) return ['no list of actions'];
    const shown = e => { const b = e.getBoundingClientRect(); return b.width > 0 && b.height > 0 && getComputedStyle(e).display !== 'none'; };
    const ths = [...table.querySelectorAll(':scope > thead > tr > th')].filter(shown), tds = [...row.children].filter(shown);
    if (!ths.length) return ['the header row isn\'t shown'];
    if (getComputedStyle(table).display !== 'table') return ['the list is laid out as stacked rows, not a table'];
    if (ths.length !== tds.length) return [`${ths.length} headings over ${tds.length} cells`];
    return ths.flatMap((th, i) => Math.abs(th.getBoundingClientRect().left - tds[i].getBoundingClientRect().left) > 2
      ? [`"${th.innerText.trim()}" starts at ${Math.round(th.getBoundingClientRect().left)}px, its column at ${Math.round(tds[i].getBoundingClientRect().left)}px`] : []);
  }),
};

/** Setup: the demo's analyst put on the first team (on none, My teams opens All open for them), then the list opened. */
async function analystOnATeam(page) {
  await page.goto(new URL('/admin/teams', page.url()).href, { waitUntil: 'load' });
  await settle(page);
  const pick = page.locator('select[aria-label^="Person to add to"]').first();
  if (await pick.locator('option', { hasText: 'Dev Analyst' }).count()) {
    await pick.selectOption({ label: 'Dev Analyst' });
    await pick.locator('xpath=following::button[normalize-space()="Add"][1]').click();
    await idle(page);
  }
  await page.goto(new URL('/actions?view=open&layout=list', page.url()).href, { waitUntil: 'load' });
}

/**
 * "Open here by default" keeps the layout: the default set to My teams on the board, then All open on the list (the List
 * tab pressed), then Actions from the sidebar and from a new tab: both open My teams on the board.
 */
const defaultKeepsLayout = {
  name: 'Open here by default opens that tab in that layout, from the sidebar and a new tab',
  check: async page => {
    const problems = [];
    const views = page.locator('[role=group][aria-label="Views"]');
    const layoutTab = name => page.locator('.tide-actions-layout').getByRole('button', { name });
    await views.getByRole('button', { name: /^My teams/ }).click();
    await idle(page);
    await layoutTab(/Board/).click();
    await idle(page);
    const star = page.locator('button#view-default');
    if (await star.count()) { await star.click(); await idle(page); }
    await views.getByRole('button', { name: /^All open/ }).click();
    await idle(page);
    await layoutTab(/List/).click();
    await idle(page);
    const opened = async (p, how) => {
      const view = (await p.locator('[role=group][aria-label="Views"] [aria-pressed="true"]').innerText()).replace(/\s+/g, ' ').trim();
      const layout = (await p.locator('.tide-actions-layout [aria-pressed="true"]').textContent()).trim();
      if (!view.startsWith('My teams') || layout !== 'Board') problems.push(`${how} opened ${view} as the ${layout}, not My teams on the board`);
    };
    await page.locator('#tide-nav-links a[href="actions"]').first().click();
    await idle(page);
    await opened(page, "the sidebar's Actions");
    const fresh = await page.context().newPage();
    await fresh.goto(new URL('/actions', page.url()).href, { waitUntil: 'load' });
    await settle(fresh);
    await opened(fresh, 'a new tab on /actions');
    await fresh.close();
    return problems;
  },
};

/** After a card is closed by dragging it, the address names the card now selected (?card=), not the one that left. */
const addressFollowsAfterClose = {
  name: 'after a drag-close the address names the selected card',
  check: async page => {
    const lane = page.locator('.tide-kcol[data-status="Open"] .tide-kcard[draggable="true"]');
    if (await lane.count() < 2) return ['the Open lane has fewer than two cards to move'];
    const card = lane.first();
    await card.locator('.tide-kcard-head').click({ position: { x: 4, y: 4 } });
    await idle(page);
    await card.dragTo(page.locator('.tide-kchip[data-status="Cancelled"]'));
    const confirm = page.getByRole('button', { name: 'Move to Cancelled' });
    await confirm.waitFor({ timeout: 5000 });
    await confirm.click();
    await idle(page);
    const now = await selectedCard(page);
    return now.card === now.number?.replace(/^A-/, '') ? [] : [`the address says card=${now.card}, but ${now.number} is selected`];
  },
};

/** The rail's time-in-column fact reads naturally: "In Open", "In progress", "Blocked", never "In In progress". */
const columnFactWording = {
  name: 'the rail says how long in its column without doubling "In"',
  check: page => page.$$eval('#tide-rail .tide-facts dt', dts => {
    const texts = dts.map(d => d.innerText.trim());
    return texts.some(t => /^In In\b/i.test(t)) ? [`the fact reads "${texts.find(t => /^In In\b/i.test(t))}"`] : [];
  }),
};

/** On the board, the rail puts "From the article" before the hunt query, as the board's design does. */
const articleBeforeHunt = {
  name: 'the board\'s rail: the article, then the hunt query',
  check: page => page.evaluate(() => {
    const a = document.querySelector('#tide-rail #rail-article-h'), h = document.querySelector('#tide-rail #rail-hunt-h');
    if (!a || !h) return [`the rail has no ${!a ? 'article' : 'hunt query'}`];
    return a.compareDocumentPosition(h) & Node.DOCUMENT_POSITION_FOLLOWING ? [] : ['the hunt query comes before the article'];
  }),
};

/** The average brightness of `clip` on the page, from a screenshot decoded in a page of its own (as dims does, above). */
async function luma(page, clip) {
  const shot = (await page.screenshot({ clip })).toString('base64');
  const scratch = await page.context().newPage();
  try {
    return await scratch.evaluate(async s => {
      const img = new Image();
      img.src = `data:image/png;base64,${s}`;
      await img.decode();
      const c = Object.assign(document.createElement('canvas'), { width: img.width, height: img.height }), g = c.getContext('2d');
      g.drawImage(img, 0, 0);
      const d = g.getImageData(0, 0, c.width, c.height).data;
      let sum = 0;
      for (let i = 0; i < d.length; i += 4) sum += .2126 * d[i] + .7152 * d[i + 1] + .0722 * d[i + 2];
      return sum / (d.length / 4);
    }, shot);
  } finally {
    await scratch.close();
  }
}

/**
 * With `open` (Save view, or a card's close asking how it closed) a modal is up: its backdrop covers the docked rail as it
 * covers the page, dimming both (each at least 25% darker), and the rail is under it.
 */
const scrimCoversRail = open => ({
  name: `the ${open} dialog's backdrop dims the docked rail`,
  check: async page => {
    const box = await page.locator('#tide-rail').boundingBox();
    const rail = { x: box.x + 20, y: box.y + box.height - 220, width: box.width - 40, height: 180 };
    const main = await page.locator('#main-content').boundingBox();
    const content = { x: main.x + 20, y: 780, width: 200, height: 180 };
    const before = [await luma(page, rail), await luma(page, content)];
    if (open === 'Save view') await page.locator('#save-view').click();
    else {
      await page.locator('.tide-kcard[draggable="true"] .tide-kcard-move').first().click();
      await page.locator('.tide-kcard-menu').getByRole('menuitem', { name: 'Cancelled' }).click();
    }
    await page.locator('.tide-modal-scrim').waitFor({ timeout: 5000 });
    await idle(page);
    const after = [await luma(page, rail), await luma(page, content)];
    const problems = [];
    ['the rail', 'the page'].forEach((what, i) => {
      const drop = (before[i] - after[i]) / Math.max(before[i], 1) * 100;
      if (drop < 25) problems.push(`${what} is only ${drop.toFixed(0)}% darker under the dialog (${before[i].toFixed(0)} to ${after[i].toFixed(0)})`);
    });
    const above = await page.evaluate(() => {
      const r = document.getElementById('tide-rail').getBoundingClientRect();
      return !!document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)?.closest('#tide-rail');
    });
    if (above) problems.push('the rail is above the dialog\'s backdrop');
    await page.keyboard.press('Escape');
    return problems;
  },
});

/** Enter on the selected card opens it. Run last on its page: it leaves it. */
const boardEnterOpens = {
  name: 'Enter opens the selected card',
  check: async page => {
    const number = (await page.locator('.tide-kcard.selected .tide-action-num').innerText()).trim().replace(/^A-/, '');
    await page.locator('.tide-kcard.selected').focus();
    await page.keyboard.press('Enter');
    try { await page.waitForURL(new RegExp(`/actions/${number}$`), { timeout: 5000 }); return []; } catch { return [`Enter on A-${number} left the page at ${page.url()}`]; }
  },
};

/**
 * Escape with the rail's Move to menu open closes the menu and leaves the rail open; the next Escape closes the overlay.
 * Run on a page whose setup opened the overlay.
 */
const escapeClosesTheMenuFirst = {
  name: 'Escape closes the rail\'s Move menu before the rail',
  check: async page => {
    const button = page.locator('#tide-rail').getByRole('button', { name: /^Move A-\d+ to$/ });
    if (!await button.count()) return ['the rail has no Move to'];
    await button.focus();
    await page.keyboard.press('Enter');
    await idle(page);
    const problems = [];
    if (!await page.evaluate(() => !!document.activeElement?.closest('#tide-rail [role="menu"]'))) problems.push('Move to didn\'t open its menu with focus in it');
    const out = await page.evaluate(() => {
      const m = document.querySelector('#tide-rail [role="menu"]')?.getBoundingClientRect(), r = document.querySelector('#tide-rail .tide-rail-body').getBoundingClientRect();
      return m && (m.left < r.left - 1 || m.right > r.right + 1) ? `the menu (${Math.round(m.left)}–${Math.round(m.right)}px) runs out of the rail (${Math.round(r.left)}–${Math.round(r.right)}px)` : null;
    });
    if (out) problems.push(out);
    await page.keyboard.press('Escape');
    await idle(page);
    if (await page.locator('#tide-rail [role="menu"]').count()) problems.push('Escape left the menu open');
    if (!await anyShown(page, '#tide-rail')) problems.push('Escape in the menu closed the rail too');
    await page.keyboard.press('Escape');
    await idle(page);
    if (await anyShown(page, '#tide-rail')) problems.push('a second Escape didn\'t close the rail');
    return problems;
  },
};

/** The selected card is wholly in view in its lane (which scrolls on its own), and has focus: a link to it lands on it. */
const selectedCardInView = {
  name: 'the selected card is in view in its lane, with focus',
  check: page => page.evaluate(() => {
    const card = document.querySelector('.tide-kcard.selected'), lane = card?.closest('.tide-kcol-cards');
    if (!card || !lane) return ['no card is selected'];
    const c = card.getBoundingClientRect(), l = lane.getBoundingClientRect(), problems = [];
    if (c.top < l.top - 1 || c.bottom > l.bottom + 1) problems.push(`the selected card (${Math.round(c.top)}–${Math.round(c.bottom)}px) is out of its lane's view (${Math.round(l.top)}–${Math.round(l.bottom)}px)`);
    if (document.activeElement !== card) problems.push('the selected card has no focus');
    return problems;
  }),
};

/**
 * The rail's Move to → Cancelled, from the open overlay, asks how it closed: the dialog is usable (the overlay gives way to
 * it), and Escape closes the dialog alone, the card staying where it was. Run on a page whose setup opened the overlay.
 */
const railMoveAsksFirst = {
  name: "the rail's Move to a closed status asks first, and Escape closes only the dialog",
  check: async page => {
    const number = (await page.locator('.tide-kcard.selected .tide-action-num').innerText()).trim();
    try { await page.locator('#tide-rail').getByRole('button', { name: /^Move A-\d+ to$/ }).click({ timeout: 5000 }); } catch { return [`the rail has no Move to for ${number}`]; }
    try { await page.locator('#tide-rail').getByRole('menuitem', { name: 'Cancelled' }).click({ timeout: 5000 }); } catch { return ['Move to offers no Cancelled to choose']; }
    const dialog = page.getByRole('dialog', { name: `Move ${number} to Cancelled` });
    try { await dialog.waitFor({ timeout: 5000 }); } catch { return [`Move to Cancelled didn't ask how ${number} closed`]; }
    const problems = [];
    if (!await page.evaluate(() => !!document.activeElement?.closest('.tide-modal'))) problems.push('focus is outside the dialog');
    await page.keyboard.press('Escape');
    await dialog.waitFor({ state: 'hidden', timeout: 5000 }).catch(() => problems.push('Escape left the dialog open'));
    await idle(page);
    if (!await page.locator(`.tide-kcol[data-status="Open"] .tide-kcard:has(.tide-action-num:text-is("${number}"))`).count()) problems.push(`${number} left its lane`);
    return problems;
  },
};

/** The four lanes sit side by side on one row (their tops line up). */
const lanesOnOneRow = {
  name: 'the four lanes on one row',
  check: page => page.evaluate(() => {
    const tops = [...document.querySelectorAll('.tide-kcol')].map(l => Math.round(l.getBoundingClientRect().top));
    return tops.length === 4 && Math.max(...tops) - Math.min(...tops) <= 1 ? [] : [`the lanes start at ${tops.join(', ')}px`];
  }),
};

/** What the rail holds for the selected action on the list or the board. */
const actionsRailHolds = [visible('#tide-rail [data-rail-next]'), visible('#tide-rail .tide-facts'), visible('#tide-rail #rail-activity-h'),
  flat('#tide-rail .tide-rail-body > section'), fits('.tide-rail-body'), fits('.tide-rail-body > *'), railFollowsSelection];

/** The action page's own column: its head, the tools and the query fit it, the query wrapping rather than scrolling. */
const actionPageFits = [noSidewaysScroll, fits('.tide-action-head'), fits('.tide-drawer'), visible('.tide-feed')];

/** A drag or paste event with `files` (and `text`, if given) fired at the first match of `selector`; whether the page took it. */
const fire = (page, type, selector, files, text) => page.evaluate(([type, selector, files, text]) => {
  const dt = new DataTransfer();
  if (text) dt.setData('text/plain', text);
  for (const [name, mime] of files) {
    const bytes = mime === 'image/png'
      ? Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=='), c => c.charCodeAt(0))
      : new TextEncoder().encode('proxy log line');
    dt.items.add(new File([bytes], name, { type: mime }));
  }
  const el = selector === 'body' ? document.body : document.querySelector(selector);
  const e = type === 'paste' ? new ClipboardEvent('paste', { bubbles: true, cancelable: true, clipboardData: dt })
    : new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer: dt });
  el.dispatchEvent(e);
  return e.defaultPrevented;
}, [type, selector, files, text]);

/** What waits to be added in the rail's Files box. */
const pendingFiles = page => page.locator('#tide-rail .tide-file-pending').innerText().catch(() => '');

/** Waits (up to 5s) for `name` to be waiting in the rail's Files box; the rule then says whether it came. */
const waitForPending = (page, name) => page.waitForFunction(name => document.querySelector('#tide-rail .tide-file-pending')?.textContent.includes(name), name,
  { timeout: 5000 }).catch(() => { });

/**
 * Files go to the action's Files from anywhere on the page, as the design says ("Drop them anywhere on the page, or
 * paste"): text pasted into a text field stays text there; a file dragged over the page shows the drop hint, and dropped
 * on the page (not on a Files or comment box) waits in Files, the rail opening to show it where it was closed; an image
 * pasted with nothing focused waits there too.
 */
const pageTakesFiles = {
  name: 'files dropped on the page, or pasted with nothing focused, wait in Files',
  check: async page => {
    const problems = [];
    await page.locator('#comment-new').focus();
    if (await fire(page, 'paste', '#comment-new', [['image.png', 'image/png']], 'IOC 203.0.113.45')) problems.push('text pasted into the comment box was taken as a file');
    await page.evaluate(() => document.activeElement?.blur());
    if (!(await fire(page, 'dragover', '.tide-feed-head', [['page-drop.log', 'text/plain']]))) problems.push('a file dragged over the page isn\'t taken');
    // The hint is drawn at once and goes when no more dragover comes (a real drag keeps sending them), so it's caught as it shows.
    if (!(await page.locator('.tide-page-drop').first().waitFor({ timeout: 2000 }).then(() => true, () => false))) problems.push('no drop hint shows while a file is dragged over the page');
    await fire(page, 'drop', '.tide-feed-head', [['page-drop.log', 'text/plain']]);
    await waitForPending(page, 'page-drop.log');
    if (await anyShown(page, '.tide-page-drop')) problems.push('the drop hint stays after the drop');
    if (!(await anyShown(page, '#tide-rail .tide-file-pending'))) problems.push('the dropped file isn\'t shown waiting in the rail');
    const dropped = await pendingFiles(page);
    if (!dropped.includes('page-drop.log')) problems.push('a file dropped on the page isn\'t waiting in Files');
    if (dropped.includes('image.png') || dropped.includes('pasted-screenshot')) problems.push('the comment box\'s pasted text left a file in Files');
    await page.evaluate(() => document.activeElement?.blur());
    await fire(page, 'paste', 'body', [['image.png', 'image/png']]);
    await waitForPending(page, 'pasted-screenshot');
    if (!(await pendingFiles(page)).includes('pasted-screenshot')) problems.push('an image pasted with nothing focused isn\'t waiting in Files');
    return problems;
  },
};

/** The middle of the first visible match of `selector` is the match itself (or inside it): nothing is drawn over it. */
const onTop = selector => holds(`nothing over: ${selector}`, selector => {
  const el = [...document.querySelectorAll(selector)].find(e => e.getBoundingClientRect().height > 0);
  if (!el) return [`nothing shown matches ${selector}`];
  const r = el.getBoundingClientRect(), at = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
  return el.contains(at) ? [] : [`${selector} is under ${at ? at.tagName.toLowerCase() + '.' + [...at.classList].join('.') : 'nothing'}`];
}, selector);

/** The editor's sections, in the order the sent brief reads them (BriefRenderer): the draft from the sources with the
 *  bottom line it writes, the tags last. */
const editorSections = ['sec-assessment', 'sec-draft', 'sec-bluf', 'sec-bullets', 'sec-why', 'sec-rec', 'sec-shots', 'sec-ind', 'sec-tags'];

/** The editor is one column in the brief's reading order: its section headings are in the page (so Tab and a screen
 *  reader meet them) in exactly that order, each card wholly below the one before, and the headings share a left edge. */
const editorReadingOrder = holds('one column, in reading order: the editor\'s sections', ids => {
  const heads = [...document.querySelectorAll('.tide-editor-main :is(h2, h3)[id^="sec-"]')].filter(e => e.getBoundingClientRect().height > 0);
  if (heads.map(h => h.id).join() !== ids.join()) return [`its sections are ${heads.map(h => h.id).join(', ')}, not ${ids.join(', ')}`];
  const cards = heads.map(h => h.closest('section').getBoundingClientRect()), lefts = heads.map(h => Math.round(h.getBoundingClientRect().left));
  const problems = cards.flatMap((r, i) => i && r.top < cards[i - 1].bottom - 1 ? [`${ids[i]}'s card isn't below ${ids[i - 1]}'s`] : []);
  if (Math.max(...lefts) - Math.min(...lefts) > 1) problems.push(`its headings start at different x: ${ids.map((id, i) => `${id} ${lefts[i]}px`).join(', ')}`);
  return problems;
}, editorSections);

/** The editor's prose boxes keep a readable measure however wide the window: app.css caps them at 85 characters, so
 *  none is wider than 90 (in the type around it). */
const editorMeasure = holds("readable measure: the editor's text boxes", () => {
  const boxes = [...document.querySelectorAll('#f-bluf, #f-why, #f-rec, [id^="f-bullet-"]')].map(e => [e, e.getBoundingClientRect().width]).filter(([, w]) => w > 0);
  if (!boxes.length) return ["nothing shown matches the editor's text boxes"];
  const probes = boxes.map(([e]) => e.parentElement.appendChild(Object.assign(document.createElement('span'), { style: 'position:absolute;visibility:hidden;width:90ch' })));
  const most = probes.map(p => p.getBoundingClientRect().width);
  probes.forEach(p => p.remove());
  return boxes.flatMap(([e, w], i) => w > most[i] + 1 ? [`#${e.id} is ${Math.round(w)}px wide, more than 90 characters (${Math.round(most[i])}px)`] : []);
});

/** A bullet starts a line high and grows with its text, to the box's cap: no inline height outranks field-sizing, even
 *  after its bottom corner, where a resize grip would be, is dragged. Puts the bullet's text back after. */
const bulletGrows = {
  name: 'a bullet grows with its text',
  check: async page => {
    const box = page.locator('#f-bullet-0'), was = await box.inputValue();
    await box.scrollIntoViewIfNeeded();
    const r = await box.boundingBox();
    await page.mouse.move(r.x + r.width - 3, r.y + r.height - 3);
    await page.mouse.down();
    await page.mouse.move(r.x + r.width - 3, r.y + 8, { steps: 5 });
    await page.mouse.up();
    const measure = async text => {
      await box.fill(text);
      return box.evaluate(e => ({ inline: e.style.height, client: e.clientHeight, scroll: e.scrollHeight, line: parseFloat(getComputedStyle(e).lineHeight) }));
    };
    const short = await measure('Short.'), long = await measure('A bullet long enough to take several lines of its box. '.repeat(8));
    await box.fill(was);
    const problems = [];
    if (long.inline) problems.push(`#f-bullet-0 has an inline height (${long.inline}), which outranks field-sizing`);
    if (long.scroll > long.client + 1) problems.push(`#f-bullet-0 doesn't grow with its text: ${long.client}px tall, its text ${long.scroll}px`);
    if (short.client >= 2 * short.line) problems.push(`#f-bullet-0 starts ${short.client}px tall, more than a line (${short.line}px)`);
    return problems;
  },
};

/** The editor's cards and its section index fit their boxes: the index's items each on one line, wrapping as a row. */
const editorFits = [visible('.tide-section-pills'), fits('.tide-section-pills'), oneLine('.tide-section-pills button'),
  fits('.tide-editor-main > .tide-section'), fits('.tide-sticky-save'), fits('.tide-page-head')];

/** The first visible matches of `a` and `b` are the same width (within 2px) and start at the same x: one column. */
const sameColumn = (a, b) => holds(`one column: ${a} / ${b}`, ([a, b]) => {
  const box = s => [...document.querySelectorAll(s)].map(e => e.getBoundingClientRect()).find(r => r.width > 0 && r.height > 0);
  const ra = box(a), rb = box(b);
  if (!ra || !rb) return [`nothing visible: ${!ra ? a : b}`];
  return Math.abs(ra.left - rb.left) <= 2 && Math.abs(ra.width - rb.width) <= 2 ? []
    : [`${a} (${Math.round(ra.left)}px, ${Math.round(ra.width)}px wide) and ${b} (${Math.round(rb.left)}px, ${Math.round(rb.width)}px wide) aren't one column`];
}, [a, b]);

/** Setup: wider letters, and five more people on the article than the seed has: the head's row of chips at its fullest. */
const crowdedHead = async page => {
  await wideFont(page);
  await page.evaluate(() => {
    const row = document.querySelector('.tide-page-head .tide-presence');
    for (const who of ['AB', 'CD', 'EF', 'GH']) row.insertAdjacentHTML('beforeend', `<span class="tide-presence-dot">${who}</span>`);
    row.insertAdjacentHTML('beforeend', '<span class="tide-presence-dot tide-presence-more">+3</span>');
  });
};

/** Setup: wider letters, then `edit` made in the editor and the pause for autosave waited out. */
const autosaveAfter = edit => async page => {
  await wideFont(page);
  await edit(page);
  // The save bar says when autosave has run (2s after the last edit): saved at a time, or held off until Save.
  await page.locator('.tide-sticky-save :is(.tide-saved.tide-autosave, .tide-autosave-note.is-held)').first().waitFor({ timeout: 10000 });
};

/** Setup: wider letters, then a word added to the article's sectors (it autosaves), and the pause waited out. */
const autosaved = autosaveAfter(async page => {
  const box = page.locator('#sectors');
  await box.click();
  await box.press('End');
  await box.pressSequentially(', energy');
});

/** Setup: wider letters, then the article's marking changed (autosave holds it for Save), and the pause waited out. */
const autosaveHeld = autosaveAfter(page => page.getByRole('combobox', { name: "The article's TLP" }).selectOption('Green'));

/** Setup: wider letters, then the Edit | Preview switch set to Preview. */
const showPreviewPane = async page => {
  await wideFont(page);
  await page.locator('.tide-ws-switch').getByRole('button', { name: 'Preview' }).click();
  // A timer, not a condition: the preview is drawn again 250ms after a change (ReaderPreview.Settle).
  await page.waitForTimeout(250);
  await idle(page);
};

// --- the Library's context rail (option E 3.8) --------------------------------------------------------------------------

/** What the Library's rail holds for the selected entry, each in the rail and laid out for its column: the facts, what
 * it fills in, the entry tried on an open action (with its preview) and where it was used. */
const libraryRailHolds = [visible('#tide-rail .tide-lib-facts'), visible('#tide-rail #lib-fills-h'), visible('#tide-rail #lib-try-h'),
  visible('#tide-rail #lib-try-text'), visible('#tide-rail #lib-used-h'), hidden('.content #lib-try-h'), fits('.tide-rail-body'),
  fits('.tide-rail-body > *'), fits('#tide-rail .tide-lib-try-tools'), flat('#tide-rail .tide-rail-body > section'), visible('#tide-rail .tide-rail-head kbd')];

/** The list and the entry beside it fit what the docked rail leaves: neither spills, the entry's head keeps its tools,
 * and both sit left of the rail. */
const libraryFitsBesideRail = [noSidewaysScroll, besides('#main-content', '#tide-rail'), allBesides('.tide-library-detail', '#tide-rail'),
  fits('.tide-library'), fits('.tide-library-list'), fits('.tide-library-detail'), fits('.tide-library-entry-head'), fits('.tide-library-filters'),
  visible('.tide-library-entry-name'), visible('.tide-library-entry-foot')];

/** The entry header's first chip (its TLP or kind) has room before the words after it ("Saved by…"). */
const chipHasRoom = holds('the entry header\'s chip stands clear of the words after it', () => {
  const line = [...document.querySelectorAll('.tide-library-entry-title > .small')].find(e => e.getBoundingClientRect().height > 0);
  const chip = line?.firstElementChild;
  if (!chip) return ['no entry header with a chip is shown'];
  let next = chip.nextSibling;
  while (next && !(next.textContent ?? '').trim()) next = next.nextSibling;
  if (!next) return ['nothing follows the chip'];
  const range = document.createRange();
  const text = next.nodeType === Node.TEXT_NODE ? next : next.firstChild ?? next;
  const at = text.nodeType === Node.TEXT_NODE ? (text.textContent.length - text.textContent.trimStart().length) : 0;
  if (text.nodeType === Node.TEXT_NODE) { range.setStart(text, at); range.setEnd(text, at + 1); } else range.selectNode(text);
  const r = range.getBoundingClientRect(), c = chip.getBoundingClientRect();
  if (r.top >= c.bottom - 2) return [];   // the words wrapped to the next line
  const gap = r.left - c.right;
  return gap >= 4 ? [] : [`the chip is ${Math.round(gap)}px from "${next.textContent.trim().slice(0, 20)}"`];
});

/** Each list row is two lines, as the design draws it: its name with how often it was used at the right of the same
 * line, then what it's for, under both. */
const rowsTwoLines = [sameLine('.tide-library-row.selected .tide-library-row-name', '.tide-library-row.selected .tide-library-row-meta'),
  above('.tide-library-row.selected .tide-library-row-meta', '.tide-library-row.selected .tide-library-row-about'),
  holds('the rows\' use is at their right', () => [...document.querySelectorAll('.tide-library-row')].filter(r => r.getBoundingClientRect().height > 0).flatMap((r, i) => {
    const m = r.querySelector('.tide-library-row-meta').getBoundingClientRect(), b = r.getBoundingClientRect();
    return b.right - m.right <= 24 ? [] : [`row ${i + 1}'s use ends ${Math.round(b.right - m.right)}px from its right`];
  })), fits('.tide-library-row')];

/** The selected row's name, its place, whether it has focus, and the address's row. */
const libraryRow = page => page.evaluate(() => {
  const rows = [...document.querySelectorAll('.tide-library-row')];
  const at = rows.findIndex(r => r.getAttribute('aria-current') === 'true');
  return {
    at, name: rows[at]?.querySelector('.tide-library-row-name')?.textContent, current: rows.filter(r => r.getAttribute('aria-current') === 'true').length,
    focused: document.activeElement === rows[at], tabStops: rows.filter(r => r.tabIndex === 0).length, row: new URL(location.href).searchParams.get('row'),
  };
});

/**
 * The Library's keys are every list's (rows.js): J and K, and the arrows, move the selected row (aria-current, the one
 * tab stop) and focus with it; the address names the entry in place, so a reload comes back to it; Enter goes into the
 * entry shown. The rail's keys stay the rail's.
 */
const libraryKeys = {
  name: 'J, K, the arrows and Enter move through the Library as through every list, and ?row= follows',
  check: async page => {
    const problems = [];
    const expect = async (at, what) => {
      await idle(page);
      const s = await libraryRow(page);
      if (s.at !== at || !s.focused || s.current !== 1 || s.tabStops !== 1) problems.push(`${what}: row ${s.at} selected (focused ${s.focused}, ${s.current} current, ${s.tabStops} tab stops), not row ${at}`);
      return s;
    };
    await page.locator('.tide-library-row.selected').focus();
    await page.keyboard.press('j');
    await expect(1, 'J');
    await page.keyboard.press('j');
    await expect(2, 'J again');
    await page.keyboard.press('k');
    await expect(1, 'K');
    await page.keyboard.press('ArrowDown');
    const s = await expect(2, 'the down arrow');
    await page.keyboard.press('ArrowUp');
    await expect(1, 'the up arrow');
    await page.keyboard.press('ArrowDown');
    await expect(2, 'the down arrow again');
    if (!s.row) problems.push('the address names no row');
    await page.reload({ waitUntil: 'load' });
    await settle(page);
    const back = await libraryRow(page);
    if (back.name !== s.name) problems.push(`a reload selects "${back.name}", not "${s.name}"`);
    await page.locator('.tide-library-row.selected').focus();
    await page.keyboard.press('Enter');
    await idle(page);
    if (!(await page.evaluate(() => document.activeElement?.classList.contains('tide-library-detail')))) problems.push('Enter on a row doesn\'t go into the entry');
    return problems;
  },
};

/**
 * A real file dragged from the desktop and dropped at (700, 500), through the browser's own input (CDP), where nothing on
 * the page takes files: the browser doesn't open it, in this tab or another.
 */
const fileDropStays = {
  name: 'a file dropped where nothing takes it doesn\'t open in the browser',
  check: async page => {
    const file = path.join(os.tmpdir(), 'tide-dropme.txt');
    writeFileSync(file, 'dropped by the layout check');
    const [before, tabs] = [page.url(), page.context().pages().length];
    const cdp = await page.context().newCDPSession(page);
    const data = { items: [], files: [file], dragOperationsMask: 1 };
    for (const type of ['dragEnter', 'dragOver', 'drop']) await cdp.send('Input.dispatchDragEvent', { type, x: 700, y: 500, data });
    // Waits for something not to happen (the browser opening the file), so only time will do.
    await page.waitForTimeout(1500);
    const problems = [];
    if (page.url() !== before) problems.push(`the tab went to ${page.url()}`);
    if (page.context().pages().length !== tabs) problems.push('the file opened in another tab');
    await cdp.detach();
    return problems;
  },
};

/** Setup: wider letters, then a file dropped on the action page, waiting in Files (the rail opening for it below 1500px). */
const fileStaged = async page => {
  await wideFont(page);
  await fire(page, 'drop', '.tide-feed-head', [['staged.log', 'text/plain']]);
  await waitForPending(page, 'staged.log');
};

/** A file waiting in Files isn't dropped without a word: leaving the page by a link (`link`) asks first, and Stay stays,
 *  the file still there. */
const stagedFilesAsk = link => ({
  name: 'leaving with a file waiting in Files asks first',
  check: async page => {
    const at = page.url();
    await page.locator(link).first().click();
    const leave = page.locator('.tide-modal[role="alertdialog"]');
    if (!(await leave.waitFor({ timeout: 3000 }).then(() => true, () => false))) return ['leaving with a file waiting in Files didn\'t ask'];
    await leave.getByRole('button', { name: 'Stay', exact: true }).click();
    await idle(page);
    const problems = [];
    if (page.url() !== at) problems.push(`Stay left the page for ${page.url()}`);
    if (!(await pendingFiles(page)).includes('staged.log')) problems.push('Stay lost the file waiting in Files');
    return problems;
  },
});

/** A dialog opened from inside the open overlay (`how`: a rail link that asks before leaving, or Ctrl K's palette)
 *  closes the overlay; Escape then closes the dialog, and focus is back on the Context button, not dropped on <body>. */
const dialogFromOverlay = how => ({
  name: `Escape from the ${how === 'link' ? 'leave dialog' : 'palette'} opened in the overlay: focus on Context`,
  check: async page => {
    if (how === 'link') await page.locator('#tide-rail .tide-rail-article-title').click();
    else { await page.locator(railLink).first().focus(); await page.keyboard.press('Control+k'); }
    const dialog = page.locator(how === 'link' ? '.tide-modal[role="alertdialog"]' : '.tide-palette');
    if (!(await dialog.waitFor({ timeout: 3000 }).then(() => true, () => false))) return [`no ${how === 'link' ? 'leave dialog' : 'palette'} opened`];
    // Open once focus is in it. Not idle: the leave dialog's question holds the navigation, so the server's work stays in
    // hand until it's answered.
    await page.waitForFunction(sel => !!document.activeElement?.closest(sel), how === 'link' ? '.tide-modal[role="alertdialog"]' : '.tide-palette', { timeout: 3000 }).catch(() => {});
    await page.keyboard.press('Escape');
    await idle(page);
    const at = await page.evaluate(() => document.activeElement === document.body ? '<body>' : document.activeElement?.id || document.activeElement?.tagName);
    return at === 'tide-rail-toggle' ? [] : [`focus is on ${at}, not the Context button`];
  },
});

/** Write one by hand, typed and not left: the first real click on Add the query adds it (the button was enabled as the
 *  text was typed, not only once the box lost focus). Changes the demo: run late. */
const writtenQueryAddsOnFirstClick = {
  name: 'Add the query works on the first click',
  check: async page => {
    const before = await page.locator('.tide-hunt pre.tide-query').count();
    await page.locator('#hunt-write').click();
    await page.locator('textarea[aria-label="Your query"]').click();
    await page.keyboard.type('DeviceProcessEvents | where FileName == "php-fpm"');
    const button = page.getByRole('button', { name: 'Add the query', exact: true });
    await button.evaluate(b => b.scrollIntoView({ block: 'center' }));
    const add = await button.boundingBox();
    await page.mouse.click(add.x + add.width / 2, add.y + add.height / 2);
    await idle(page);
    const after = await page.locator('.tide-hunt pre.tide-query').count();
    return after > before ? [] : ['the first click on Add the query added nothing'];
  },
};

/** One way to add a query open at a time: opening another closes the first, and Escape closes the one open, focus back
 *  on its button. */
const oneAdderAtATime = {
  name: 'one way to add a query open at a time; Escape closes it',
  check: async page => {
    const problems = [];
    await page.locator('#hunt-write').click();
    await page.locator('#hunt-pick').click();
    await page.locator('#hunt-saved-picker').waitFor({ timeout: 3000 }).catch(() => { });
    if (await anyShown(page, 'textarea[aria-label="Your query"]')) problems.push('Write one by hand stays open beside Add a saved query');
    await page.locator('#hunt-add-iocs').click();
    await idle(page);
    if (await anyShown(page, '#hunt-saved-picker')) problems.push('Add a saved query stays open beside Add indicators');
    await page.locator('#hunt-indicators :is(textarea, input)').first().focus();
    await page.keyboard.press('Escape');
    await idle(page);
    if (await anyShown(page, '#hunt-indicators')) problems.push('Escape leaves Add indicators open');
    const at = await page.evaluate(() => document.activeElement?.id);
    if (at !== 'hunt-add-iocs') problems.push(`after Escape focus is on ${at || 'the page'}, not Add indicators`);
    return problems;
  },
};

/** "Also raised from it": each row leads with its number, its title beside it, and its status at the row's right. */
const raisedRows = holds('raised-from-it rows: number first, status at the right', () => {
  const rows = [...document.querySelectorAll('#tide-rail section[aria-labelledby="rail-raised-h"] li')].filter(li => li.getClientRects().length);
  if (!rows.length) return ['no "Also raised from it" row is shown'];
  return rows.flatMap((li, i) => {
    const box = li.getBoundingClientRect();
    const num = li.querySelector('.tide-action-num')?.getBoundingClientRect(), title = li.querySelector('a')?.getBoundingClientRect();
    const status = [...li.querySelectorAll('.tide-badge')].at(-1)?.getBoundingClientRect();
    if (!num || !title || !status) return [`row ${i + 1} lacks a number, a title or a status`];
    const found = [];
    if (!(num.right <= title.left + 1 && num.top < title.bottom && title.top < num.bottom)) found.push(`row ${i + 1}: the number isn't first, beside the title`);
    if (box.right - status.right > 2 || status.left < title.left) found.push(`row ${i + 1}: the status isn't at the right (${Math.round(box.right - status.right)}px short)`);
    return found;
  });
});

/** The hunt drawer as designed: Write one by hand, Add a saved query and Add indicators beside Copy; "What did it find?"
 *  under them, with Attach a screenshot. */
const huntCopyRow = sameLine('.tide-hunt [data-copy-target]', '#hunt-write', '#hunt-pick', '#hunt-add-iocs');
const huntFound = [visible('.tide-hunt-found > h3'), above('.tide-hunt [data-copy-target]', '.tide-hunt-found > h3'), visible('.tide-hunt-found [data-pick="files-input"]')];
const huntAsDesigned = [huntCopyRow, ...huntFound];

/** The header's badges: its kind, and its due date said with how it stands ("Due Thu Nov 12 · due soon"), one badge. */
const actionHeadAsDesigned = [visible('.tide-action-badges .tide-action-head-kind'), says('.tide-action-badges .tide-action-due', '^Due \\w{3} \\w{3} \\d+ · '),
  hidden('.tide-action-badges > .tide-badge-warn:not(.tide-action-due)')];

/** Setup: wider letters, and the demo's "Every indicator sweep (XQL)" chosen from the list (it fills in from any action
 * with indicators, as the design shows it). */
const everyIndicatorSweep = async page => {
  await wideFont(page);
  await page.locator('.tide-library-row', { hasText: 'Every indicator sweep (XQL)' }).click();
  await page.locator('#lib-try-text').waitFor({ timeout: 5000 }).catch(() => { });
};

/** Setup: that entry, then the rail opened where it's an overlay (the Context button). */
const libraryOverlay = async page => {
  await everyIndicatorSweep(page);
  await page.locator('#tide-rail-toggle').click();
  await page.locator('#tide-rail').waitFor();
  await idle(page);
};

/** No word in a visible match of `selector` is split across lines: each word's text sits on one line box (a line
 *  ending at a hyphen doesn't split a word). */
const wordsWhole = selector => holds(`no word split across lines: ${selector}`, selector => {
  const found = [];
  for (const el of document.querySelectorAll(selector)) {
    if (!el.getClientRects().length) continue;
    const walk = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
    for (let node = walk.nextNode(); node; node = walk.nextNode()) {
      // A hyphen is a place a line may break ("remote-/access"), as in print: the parts either side are the words here.
      for (const m of node.data.matchAll(/[^\s-]+-?/g)) {
        const range = document.createRange();
        range.setStart(node, m.index);
        range.setEnd(node, m.index + m[0].length);
        const lines = new Set([...range.getClientRects()].filter(r => r.width > 0).map(r => Math.round(r.top)));
        if (lines.size > 1) found.push(`"${m[0]}" in ${selector} is split across ${lines.size} lines`);
      }
    }
  }
  return found;
}, selector);

/** Triage's rail: its sections flat under its header (with the ] key), each within the rail's column; the rail's, not the
 *  page's. */
const triageRailFits = [visible('#tide-rail .tide-rail-head kbd'), flat('#tide-rail .tide-rail-body > section'), fits('.tide-rail-body'),
  fits('#tide-rail .tide-rail-body > section'), visible('#tide-rail #rail-named-h'), hidden('.content #rail-named-h')];

/** Beside Triage's docked rail, the list, the reading pane and the rail sit side by side, and the pane's decision bar is
 *  on the first screen, its buttons within it. From 1800px the bar is one row and the list 440px; under that the list
 *  takes up to 42% of the page and the bar's buttons wrap to a second row, as the design has them. */
const triageBesideTheRail = [noSidewaysScroll, visible('#tide-rail'), attr('#tide-rail', 'role', null), besides('#main-content', '#tide-rail'),
  besides('.tide-reading', '#tide-rail'), besides('.tide-triage-layout > .tide-card:first-child', '.tide-reading'), visible('.tide-reading'),
  widerThan('.tide-triage-layout > .tide-card:first-child', 340), widerThan('.tide-reading', 440), onFirstScreen('.tide-reading .tide-triage-decide'),
  fits('.tide-triage-decide'), fits('.tide-triage-acts'), fits('.tide-reading-head'), fits('.tide-triage-row'), windowDoesntScroll,
  // The pane's title has the pane's width (its tools under it where the pane is narrow): whole words, three lines at most.
  wordsWhole('.tide-reading-title'), oneLine('.tide-reading-title', 3),
  [1800, oneRow('.tide-triage-decide .tide-triage-acts')], [1800, widthNear('.tide-triage-layout > .tide-card:first-child', 440, 16)]];
// --- Search's context rail (option E 3.6) ------------------------------------------------------------------------------

/** What a lookup's rail holds, in the rail and not the page: the hunt query (for an indicator), who is sweeping for it,
 * and the export, each section within the rail's column; the query wrapping rather than scrolling sideways. */
const searchRailHolds = (query = true) => [
  ...(query ? [visible('#tide-rail #lookup-query'), fits('#tide-rail #lookup-query'), visible('#tide-rail [role=group][aria-label="Query language"]')] : []),
  visible('#tide-rail #lookup-sweeps-h'), visible('#tide-rail #lookup-export-h'), hidden('.content #lookup-query'), hidden('.content #lookup-sweeps-h'),
  hidden('.content #lookup-export-h'), fits('.tide-rail-body'), fits('.tide-rail-body > *')];

/** The lookup's own column: its one card (the answer, the articles, the actions and "Seen with") fits, the articles'
 * table too, and nothing scrolls sideways. */
const searchPageFits = [noSidewaysScroll, visible('.content #lookup-summary'), visible('.tide-lookup-articles'), fits('.content .tide-card'),
  fits('.tide-lookup-articles'), headAtTop('.content .tide-card', 24), sameBox('.tide-card', '#lookup-summary', '.tide-lookup-articles'),
  sameBox('.tide-card', '.tide-lookup-articles', '#lookup-actions'), sameBox('.tide-card', '#lookup-actions', '#lookup-seen-with'), above('#lookup-actions', '#lookup-seen-with')];

// --- the pages: route, optional setup, widths (each 900px tall, or [width, height]) and rules ([minWidth, rule] from that
// width up) -------------------------------------------------------------------------------------------------------------

/** Where, in the pages, those that change the demo begin (see shard). */
const CHANGES_THE_DEMO = Symbol('changes the demo');

/** What's new's dialog: shown whole in the window, its parts within it, focus on Got it; its footer line and Take the
 *  tour beside Got it, each whole in the window. */
const whatsNewRules = [noSidewaysScroll, visible('.tide-whatsnew'), inWindow('.tide-whatsnew'), fits('.tide-whatsnew'),
  fits('.tide-whatsnew-body'), visible('.tide-whatsnew .tide-modal-actions'), focusOn('.tide-whatsnew .btn-primary'),
  visible('.tide-whatsnew-more'), visible('#whatsnew-take-tour'), fits('.tide-whatsnew > .tide-modal-actions'),
  inWindow('.tide-whatsnew .tide-modal-actions .btn'), inWindow('.tide-whatsnew-more')];

/** A tour step: a dialog shown whole in the window, its parts within it, focus on Next, nothing else offered with it. */
const tourRules = [noSidewaysScroll, visible('.tide-tour'), inWindow('.tide-tour'), fits('.tide-tour'), fits('.tide-tour .tide-modal-actions'),
  focusOn('.tide-tour .btn-primary'), hidden('.tide-whatsnew'), hidden('.tide-tip'), visible('.tide-tour-scrim'), inClient('.tide-tour'), belowTopBar('.tide-tour')];

/** A step of Learn to present, in rehearsal: a tour step's rules, the rehearsal said in the top bar, no Start the
 *  meeting, the top bar within the window, and the step's control ringed. */
const rehearsalRules = [...tourRules, visible('.tide-rehearsal'), inWindow('.tide-rehearsal'), hidden('.tide-present-tools .btn-primary'),
  fits('.tide-present-top'), visible('.tide-tip-target')];

/** A page guide's step: a tour step's rules, with the control it's about ringed. */
const guideRules = [...tourRules, visible('.tide-tip-target')];

/** As guideRules, on a page with a context rail: the step keeps off the rail where it's docked. */
const railGuideRules = [...guideRules, [1500, allBesides('.tide-tour', '#tide-rail')]];

/** Show me around in a page's header: shown, within the window, and the header still fits. */
const guideLinkRules = [noSidewaysScroll, visible('#tide-guide-start'), inWindow('#tide-guide-start'), fits('.tide-page-head')];

/** The page is still `path` (from the app's root: "/" for Today). */
const stillAt = path => holds(`still at ${path}`, path => new URL(document.baseURI).pathname.replace(/\/$/, '') + path === location.pathname
  ? [] : [`the page is ${location.pathname}`], path);

/** A page's tip: shown whole in the window, its parts within it, ringing what it's about, with no dialog left open. */
const tipRules = [noSidewaysScroll, visible('.tide-tip'), inWindow('.tide-tip'), fits('.tide-tip'), visible('.tide-tip-target'),
  hidden('.tide-whatsnew'), inClient('.tide-tip'), [1500, allBesides('.tide-tip', '#tide-rail')]];

const pages = [
  { name: 'today', route: '/', widths: [1440, 1000, 780],
    rules: [noSidewaysScroll, visible('.tide-steps'), fits('.tide-steps > li'), fits('.tide-page-head')] },
  // Today's side cards are in the context rail. From 1500px it's docked beside the page, open, and the page keeps its
  // step strip on one row and its head and rows within their boxes, in what's left (about 870px at 1500); below 1500
  // it's closed until asked for. Wider letters throughout, so each holds by construction.
  { name: 'today-rail', route: '/', setup: wideFont, widths: [1920, 1600, 1500],
    rules: [noSidewaysScroll, visible('#tide-rail .tide-card'), besides('#main-content', '#tide-rail'), oneRow('.tide-steps'), fits('.tide-steps > li'),
      fits('.tide-page-head'), fits('.tide-order-row'), fits('.tide-rail-body'), fits('#tide-rail .tide-card'), hidden('.content .tide-card[aria-labelledby="meeting-h"]'),
      ringOnFocus('#tide-rail #analyst, #tide-rail #intro, #tide-rail [data-submit], #tide-rail a.btn')] },
  // Beside the docked rail, the page head keeps its buttons beside the title, and each step says where it stands in
  // full on up to two lines, Triage's KEV count a flag beside it, the strip one row of steps. My actions and what's not
  // on a brief yet sit in the page. The rail's sections are flat, under a header with the ] key, and laid out for a
  // narrow column: the edition check's two editions one under the other.
  { name: 'today-rail-fit', route: '/', setup: wideFont, widths: [1920, 1600, 1500],
    rules: [oneRow('.tide-page-head'), oneRow('.tide-brief-head > :last-child'), oneRow('.tide-steps'), oneLine('.tide-step-detail', 2), visible('.tide-steps .tide-step-flag'),
      visible('.content section[aria-labelledby="my-actions-h"]'), visible('.content section[aria-labelledby="recent-h"]'),
      flat('#tide-rail .tide-rail-body > section'), visible('#tide-rail .tide-rail-head kbd'), attr('#tide-rail', 'role', null),
      stacked('#tide-rail .tide-ed-cols'), fits('#tide-rail .tide-ed-col'),
      fits('#tide-rail section[aria-labelledby="edition-check-h"]'), fits('#tide-rail section[aria-labelledby="meeting-h"]')] },
  // The meeting's form, opened from Change presenter: the title slide line's label on one line, the form in its column.
  { name: 'today-rail-meeting', route: '/', setup: openMeeting, widths: [1920, 1500, 1440],
    rules: [visible('#tide-rail'), visible('#tide-rail #analyst'), oneLine('#tide-rail label[for="intro"]'), fits('#tide-rail section[aria-labelledby="meeting-h"]'), focusIn('#tide-rail #analyst'),
      ringOnFocus('#tide-rail #analyst, #tide-rail #intro, #tide-rail [data-submit]')] },
  // The sidebar's menu: in a short window (852px tall, as on a laptop) all of it fits without scrolling, each link a
  // target of 24px or more, wider letters and collapsed to icons too. Shorter still it scrolls, thinly, with the current
  // page's link in view, on load and after navigating to it.
  { name: 'nav-short', route: '/', setup: wideFont, widths: [[1920, 852], [1440, 852], [1440, 1080]], rules: [navFits, navTargets] },
  { name: 'nav-short-collapsed', route: '/', setup: collapseNav, widths: [[1440, 852], [1440, 600]], rules: [navTargets] },
  { name: 'nav-very-short', route: '/admin', widths: [[1440, 600], [1920, 500]], rules: [navTargets, navScrollsToActive] },
  { name: 'nav-very-short-navigate', route: '/', setup: navigateToAdmin, widths: [[1440, 600]], rules: [navScrollsToActive] },
  // The top bar keeps each control in its own room, whatever the width and the fonts.
  { name: 'top-bar-today', route: '/', setup: wideFont, widths: [1920, 1100, 900, 780, 726, 700, 660, 641, 400],
    rules: [noSidewaysScroll, noOverlap('.top-row a, .top-row button, .top-row input'), fits('.top-row'),
      oneLine('.top-row > span:first-child'), insideOf('.top-row > span:first-child', '.top-row')] },
  // No page scrolls sideways where the top bar is at its tightest. Coverage (its matrix table) and an article's page (a
  // table in the reader preview) are left out: their own content is wider than 700px with wider letters, not the bar.
  ...['/triage', '/articles', '/actions', '/library', '/stack', '/briefs', '/search', '/rotation', '/workload', '/trends', '/published',
    '/reports', '/stories', '/adversaries', '/notifications', '/briefs/{sent}', '/actions/{huntAction}']
    .map(route => ({ name: `narrow ${route}`, route, setup: wideFont, widths: [700, 641], rules: [noSidewaysScroll] })),
  // Each step's detail stays within two lines at every width (the strip compact where it's narrow), and Triage's KEV
  // flag sits on the line of its words where there's room.
  // Today's page, top to bottom: the running order, the opening note, My actions and Not on a brief yet, the numbers.
  { name: 'today-order', route: '/', widths: [1920, 780],
    rules: [inOrder('#running-order-h', '#team-note-h', '#my-actions-h', '.content .tide-card:has(.tide-collapse-btn[aria-controls$="-body"])')] },
  // Under the step strip, Triage's word on the feeds not pulled since the morning's cutoff (the demo's stale feeds, on a
  // working day: nothing is said on a day off), with Pull all: snug under the strip, its words and button on one row where
  // there's room, wrapping within its box where there isn't, the button whole on one line.
  { name: 'today-feeds', route: '/', setup: wideFont, widths: [1920, 1440, 780], when: demo => demo.FeedsDue !== false,
    rules: [noSidewaysScroll, visible('.tide-feeds-nudge'), fits('.tide-feeds-nudge'), closeBelow('.tide-feeds-nudge', '.tide-steps', 16),
      [1440, oneRow('.tide-feeds-nudge')], oneLine('.tide-feeds-nudge .tide-pull-all > button'), notSqueezed('.tide-feeds-nudge .tide-pull-all > button')] },
  { name: 'today-steps', route: '/', setup: wideFont, widths: [1920, 1600, 1500, 1440, 1000, 780, 641, 400],
    rules: [oneRow('.tide-steps'), oneLine('.tide-step-detail', 2), [1001, inlineIn('.tide-step-flag', '.tide-step-detail')],
      visible('.tide-steps .tide-step-flag'), notClipped('.tide-steps .tide-step-flag')] },
  // In the rail's edition check, each edition's heading fits the column: its name on one line beside its TLP chip, the
  // chip at full width, the count after it or under it.
  { name: 'today-editions', route: '/', setup: wideFont, widths: [1920, 1500],
    rules: [fits('#tide-rail .tide-ed-col > h3'), oneLine('#tide-rail .tide-ed-col > h3 > a'), sameLine('#tide-rail .tide-ed-col > h3 > a', '#tide-rail .tide-ed-sub .tide-tlp'),
      notSqueezed('#tide-rail .tide-ed-sub .tide-tlp'), oneLine('#tide-rail .tide-ed-count')] },
  // Focus after the meeting's form closes goes back to the button that opened it; the note's Edit puts it in the
  // editor, and its Cancel and Save back on Edit.
  { name: 'today-meeting-saved', route: '/', setup: meetingClosedBy('Save'), widths: [1920],
    rules: [focusOn('#tide-rail section[aria-labelledby="meeting-h"] button.tide-meeting-presenter')] },
  { name: 'today-meeting-cancelled', route: '/', setup: meetingClosedBy('Cancel'), widths: [1440],
    rules: [focusOn('#tide-rail section[aria-labelledby="meeting-h"] button.tide-meeting-presenter')] },
  { name: 'today-note-edit', route: '/', setup: noteEdited(null), widths: [1920], rules: [focusOn('#team-note')] },
  { name: 'today-note-cancelled', route: '/', setup: noteEdited('Cancel'), widths: [1920],
    rules: [focusOn('section[aria-labelledby="team-note-h"] button.tide-note-edit')] },
  // The open overlay's backdrop visibly dims the page under it, light and dark.
  { name: 'today-scrim', route: '/', setup: toggleRail, widths: [1440, 780], rules: [dims(15, true)] },
  { name: 'today-scrim-dark', route: '/', setup: darkWide, widths: [1440, 780], rules: [dims(15, true)] },
  { name: 'today-rail-closed', route: '/', setup: toggleRail, widths: [1920, 1500],
    rules: [noSidewaysScroll, hidden('#tide-rail'), visible('#tide-rail-toggle'), oneRow('.tide-steps'), fits('.tide-page-head')] },
  { name: 'today-rail-shut', route: '/', setup: wideFont, widths: [1440, 780],
    rules: [noSidewaysScroll, hidden('#tide-rail'), visible('#tide-rail-toggle'), oneRow('.tide-steps'), fits('.tide-page-head')] },
  // The open overlay holds focus as a dialog does, at every width it's an overlay: Tab never reaches the page under it.
  { name: 'today-rail-over', route: '/', setup: toggleRail, widths: [1440, 780, 400],
    rules: [noSidewaysScroll, visible('#tide-rail .tide-card'), fits('.tide-rail-body'), fits('#tide-rail .tide-card'), focusIn('#tide-rail'),
      attr('#tide-rail', 'role', 'dialog'), attr('#tide-rail', 'aria-modal', 'true'), attr('#tide-rail', 'aria-label', 'Beside the brief'),
      underIsInert, ...tabWraps('#tide-rail', 40)] },
  // Another day's brief draws the same panel in its rail.
  { name: 'day-rail', route: '/briefs/{sent}', setup: wideFont, widths: [1920, 1500],
    rules: [noSidewaysScroll, visible('#tide-rail .tide-card[aria-labelledby="meeting-h"]'), besides('#main-content', '#tide-rail'), fits('.tide-page-head'), fits('#tide-rail .tide-card')] },
  // The top bar with the brief chip in it: one row from a narrow window up, nothing pushed out of its group (the Go to
  // button under the chip), and the search box never squeezed for the chip: the chip is in view from 900px, and
  // wherever it shows the search box keeps 200px. On a phone nothing pushes the page sideways.
  { name: 'top-bar', route: '/articles', widths: [1440, 1100, 1000, 900, 780, 375],
    rules: [noSidewaysScroll, [900, visible('.tide-brief-chip')], [780, oneRow('.top-row')], [780, oneRow('.tide-top-end')],
      [780, fits('.top-row')], [780, fits('.tide-top-find')], roomBeside('.tide-top-search', 200, '.tide-brief-chip')] },
  { name: 'article', route: '/articles/{vpnArticle}', widths: [1920, 1500],
    rules: [noSidewaysScroll, visible('#tide-rail .tide-reader-sum'), visible('#tide-rail [role=tablist][aria-label="Beside the article"]')] },
  { name: 'article-actions-tab', route: '/articles/{vpnArticle}?side=actions', widths: [1920, 800],
    rules: [noSidewaysScroll, visible('#reader-panel .tide-action-ref')] },
  // The article editor: a section index across the top, then the form as one column in the order the sent brief reads
  // (Assessment, Bottom line, Bullets, Why it matters, Recommended, Screenshots, Indicators, then Tags & ATT&CK), its
  // prose boxes a readable measure however wide the window, and what readers get in the context rail, docked open from
  // 1500px. Wider letters throughout, so each holds by construction.
  { name: 'editor-rail', route: '/articles/{vpnArticle}', setup: wideFont, widths: [1920, 1600, 1500],
    rules: [noSidewaysScroll, visible('#tide-rail .tide-reader'), besides('#main-content', '#tide-rail'), attr('#tide-rail', 'role', null),
      attr('#tide-rail', 'aria-label', 'Context · what readers get'), ...editorFits, editorReadingOrder, editorMeasure,
      fits('.tide-rail-body'), fits('#tide-rail .tide-reader'), fits('#tide-rail .tide-reader-head'), fits('#tide-rail .tide-tabbar')] },
  // Below 1500px the rail is closed until asked for, and the form is the same one column, nothing scrolling sideways.
  { name: 'editor-rail-shut', route: '/articles/{vpnArticle}', setup: wideFont, widths: [1440, 1000, 780],
    rules: [noSidewaysScroll, hidden('#tide-rail'), visible('#tide-rail-toggle'), ...editorFits, editorReadingOrder, editorMeasure,
      [901, hidden('.tide-ws-switch')]] },
  // A bullet starts a line high and grows with its text, whatever was dragged at its corner.
  { name: 'editor-bullet-grows', route: '/articles/{vpnArticle}', setup: wideFont, widths: [1920], rules: [bulletGrows] },
  // `]` opens it over the page there: a modal dialog with the preview in it, named for what it holds.
  { name: 'editor-rail-over', route: '/articles/{vpnArticle}', setup: toggleRail, widths: [1440, 780],
    rules: [noSidewaysScroll, visible('#tide-rail .tide-reader'), fits('.tide-rail-body'), focusIn('#tide-rail'), attr('#tide-rail', 'role', 'dialog'),
      attr('#tide-rail', 'aria-label', 'Context · what readers get')] },
  // On a narrow screen the Edit | Preview switch stays: Preview shows what readers get in the page, in place of the
  // fields, with the save bar still on screen.
  { name: 'editor-phone-preview', route: '/articles/{vpnArticle}', setup: showPreviewPane, widths: [780, 400],
    rules: [noSidewaysScroll, visible('.tide-ws-switch'), visible('#tide-rail .tide-reader'), hidden('section[aria-labelledby="sec-assessment"]'),
      hidden('#tide-rail .tide-rail-head'), visible('.tide-sticky-save'), onFirstScreen('.tide-sticky-save'), onTop('.tide-sticky-save'), fits('.tide-rail-body'), fits('#tide-rail .tide-reader-head')] },
  // There, `]` leaves the preview in the page: no dialog over the switch.
  { name: 'editor-phone-preview-key', route: '/articles/{vpnArticle}', setup: async page => { await showPreviewPane(page); await page.locator('body').press(']'); await idle(page); },
    widths: [780, 400], rules: [attr('#tide-rail', 'role', null), onTop('.tide-ws-switch'), hidden('#tide-rail .tide-rail-head'), visible('#tide-rail .tide-reader')] },
  // The head's badges wrap under the people here rather than run off a phone's edge, for an analyst and a reviewer.
  ...['', '?roles=Viewer'].map(roles => ({ name: `editor-head-crowded${roles ? '-viewer' : ''}`, route: `/articles/{vpnArticle}${roles}`, setup: crowdedHead,
    widths: [390, 780], rules: [noSidewaysScroll, fits('.tide-page-head-main'), fits('.tide-page-head-main > .d-flex')] })),
  // Someone who can't edit reads the preview as the page: one column with the Review card above it, not a narrow strip.
  { name: 'editor-viewer', route: '/articles/{vpnArticle}?roles=Viewer', setup: wideFont, widths: [1920, 1440, 780],
    rules: [noSidewaysScroll, visible('.tide-editor-side .tide-reader'), sameColumn('.tide-review-card', '.tide-editor-side'), fits('.tide-editor-side')] },
  // Autosave in the save bar, first in it: "Saved at" after a pause in typing, or why it held off; the bar one row on a
  // wide window, and wrapping within the window on a phone, its words whole.
  { name: 'editor-autosaved', route: '/articles/{vpnArticle}', setup: autosaved, widths: [1920, 390],
    rules: [noSidewaysScroll, visible('.tide-sticky-save .tide-autosave'), says('.tide-sticky-save .tide-autosave', /^Saved at \d{1,2}:\d\d [AP]M$/),
      fits('.tide-sticky-save'), [1000, oneRow('.tide-sticky-save')], onTop('.tide-sticky-save .tide-autosave')] },
  { name: 'editor-autosave-held', route: '/articles/{vpnArticle}', setup: autosaveHeld, widths: [1920, 390],
    rules: [noSidewaysScroll, visible('.tide-sticky-save .tide-autosave-note'), says('.tide-sticky-save .tide-autosave-note', /Save to keep it\.$/),
      fits('.tide-sticky-save'), fits('.tide-sticky-save .tide-autosave-note'), wordsWhole('.tide-sticky-save .tide-autosave-note'),
      [1000, oneLine('.tide-sticky-save .tide-autosave-note')], onTop('.tide-sticky-save .tide-autosave-note')] },
  { name: 'editor-phone-edit', route: '/articles/{vpnArticle}', setup: wideFont, widths: [780, 400],
    rules: [noSidewaysScroll, visible('.tide-ws-switch'), hidden('#tide-rail'), visible('section[aria-labelledby="sec-assessment"]'), fits('.tide-section-pills'),
      editorReadingOrder] },
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
      onFirstScreen('.tide-kstrip'), lanesFillTheHeight, chipsAreDropTargets, [1440, oneRow('.tide-kstrip')]],
  })),
  // A narrow window: the lanes keep room to read a card, and the page scrolls.
  { name: 'actions-board-narrow', route: '/actions?view=open&layout=board', widths: [700, 641, [400, 800]], rules: [noSidewaysScroll, lanesTallEnough] },
  // Who has an action can be an external team's ticket reference: the longest wraps within its card and its row rather
  // than widening them, on the board and the list.
  { name: 'actions-board-long-ticket', route: '/actions?view=open&layout=board', setup: longTicket, widths: [[1440, 900], [780, 900]],
    rules: [noSidewaysScroll, visible('.tide-kcard .tide-holder'), fits('.tide-kcard'), fits('.tide-kanban')] },
  { name: 'actions-list-long-ticket', route: '/actions?view=open&layout=list', setup: longTicket, widths: [1440, 780],
    rules: [noSidewaysScroll, visible('.tide-action-row .tide-holder'), fits('.tide-dt-scroll')] },
  { name: 'today-long-ticket', route: '/', setup: longQueueTicket, widths: [1920, 1440, 780],
    rules: [noSidewaysScroll, visible('.tide-queue-meta'), fits('.tide-queue-meta'), fits('.tide-queue-row')] },
  // With the rail closed (]), the board takes the whole window again.
  { name: 'actions-board-rail-closed', route: '/actions?view=open&layout=board', setup: toggleRail, widths: [[1920, 1080]],
    rules: [noSidewaysScroll, hidden('#tide-rail'), widerThan('.content', 1400), lanesFillTheHeight] },
  // The selected action is in the context rail, on the list and the board. From 1500px it's docked beside the page, which
  // has about 873px at 1500: the list's table and the board's four lanes and closed strip still fit, nothing scrolls
  // sideways, and the strip is on the first screen. Below 1500px it's closed until ] opens it over the page. Wider letters.
  { name: 'actions-rail-list', route: '/actions?view=open&layout=list', setup: wideFont, widths: [[1920, 1000], [1680, 1000], [1600, 1000], [1500, 1000]],
    rules: [noSidewaysScroll, visible('#tide-rail'), besides('#main-content', '#tide-rail'), visible('.tide-action-row.selected'), fits('.tide-dt-scroll'),
      everyTabShown('[role=group][aria-label="Views"]'), headingsOverColumns, columnFactWording,
      besideTheTabs('[role=group][aria-label="Views"]', '#view-default', '#save-view'),
      visible('#tide-rail #rail-about-h'), ...actionsRailHolds] },
  { name: 'actions-rail-board', route: '/actions?view=open&layout=board', setup: wideFont, widths: [[1920, 1000], [1600, 1000], [1500, 1000]],
    rules: [noSidewaysScroll, visible('#tide-rail'), besides('.tide-board', '#tide-rail'), fits('.tide-kanban'), lanesOnOneRow, onFirstScreen('.tide-kstrip'),
      lanesFillTheHeight, oneRow('.tide-kcard-head'), visible('.tide-kcard.selected'), hidden('#tide-rail #rail-about-h'), ...actionsRailHolds] },
  ...['list', 'board'].map(layout => ({
    name: `actions-rail-${layout}-narrow`, route: `/actions?view=open&layout=${layout}`, setup: wideFont, widths: [[1440, 900], [780, 900]],
    rules: [noSidewaysScroll, hidden('#tide-rail'), visible('#tide-rail-toggle'), visible(layout === 'list' ? '.tide-action-row.selected' : '.tide-kcard.selected')],
  })),
  // Open over the page: what it holds, Tab held in it, and on the board Escape closing its Move menu before the rail.
  ...['list', 'board'].map(layout => ({
    name: `actions-rail-${layout}-overlay`, route: `/actions?view=open&layout=${layout}`, setup: toggleRail, widths: [[1440, 900], [780, 900]],
    rules: [visible('#tide-rail'), ...actionsRailHolds, noSidewaysScroll, focusIn('#tide-rail'), attr('#tide-rail', 'role', 'dialog'),
      ...(layout === 'board' ? [escapeClosesTheMenuFirst] : [tabStaysIn('#tide-rail', 30)])],
  })),
  { name: 'actions-rail-board-overlay-move', route: '/actions?view=open&layout=board', setup: toggleRail, widths: [[1440, 900]], rules: [railMoveAsksFirst] },
  // The board's selection: the keys, a click, T and Enter. The list's keys and Status menu beside the docked rail.
  { name: 'actions-board-keys', route: '/actions?view=open&layout=board', widths: [[1920, 1000], [1440, 900]], rules: [boardKeys, clickSelectsCard, boardEnterOpens] },
  // A link to a card (?card=, the demo's sweep, low in the Open lane) lands on it: selected, in view and focused.
  { name: 'actions-board-card-link', route: '/actions?view=open&layout=board&card={huntAction}', widths: [[1600, 1000], [1440, 900]],
    rules: [selectedCardInView, railFollowsSelection, articleBeforeHunt, columnFactWording] },
  { name: 'actions-board-card-link-a', route: '/actions?view=open&layout=board&card=A-{huntAction}', widths: [[1600, 1000]], rules: [selectedCardInView, railFollowsSelection] },
  // A modal's backdrop covers the docked rail: Save view on the list, a card's close on the board.
  ...['light', 'dark'].flatMap(theme => [['save', 'list', 'Save view'], ['close', 'board', 'close']].map(([name, layout, open]) => ({
    name: `actions-rail-scrim-${name}-${theme}`, route: `/actions?view=open&layout=${layout}`, context: { theme }, widths: [[1920, 1000], [1600, 1000]],
    rules: [scrimCoversRail(open)],
  }))),

  { name: 'actions-board-take-key', route: '/actions?view=open&layout=board', setup: newUnownedCard, widths: [[1600, 1000]], rules: [boardTakeKey] },
  { name: 'actions-rail-list-keys', route: '/actions?view=open&layout=list', widths: [[1920, 1000]], rules: [actionKeys, railFollowsSelection, statusMenuByKeyboard] },
  // A card dragged with the mouse onto each chip asks how it closed.
  { name: 'actions-board-drop', route: '/actions?view=open&layout=board', widths: [[1440, 900]], rules: [dropOnEachChip] },
  // Each chip is a link to the actions it counts as well as a drop target, and the strip still keeps to one row, with
  // wider letters (LETTER_SPACING) too. A click on one opens Recently closed on the list at that status.
  ...['', '-wide-letters'].map(suffix => ({
    name: `actions-board-closed-links${suffix}`, route: '/actions?view=open&layout=board', setup: suffix ? wideFont : undefined,
    widths: [[1920, 1080], [1440, 900], [1280, 800]],
    rules: [noSidewaysScroll, oneRow('.tide-kstrip'), chipsAreDropTargets, chipsAreLinks, ringOnFocus('.tide-kchip-link')],
  })),
  { name: 'actions-board-closed-link-opens', route: '/actions?view=open&layout=board', widths: [[1440, 900]], rules: [chipLinkOpensTheList] },
  // A closed action's rail on Recently closed offers Reopen beside Open, on one row, with wider letters.
  { name: 'actions-rail-reopen', route: '/actions?view=closed&layout=list', setup: wideFont, widths: [[1920, 1000], [1600, 1000], [1500, 1000]],
    rules: [noSidewaysScroll, visible('#tide-rail .tide-rail-reopen'), reopenNamed, oneRow('#tide-rail .tide-rail-buttons')] },
  // A sweep's query is read whole, in the wide column: its long lines wrap, so nothing scrolls sideways inside its box.
  { name: 'action-hunt', route: '/actions/{huntAction}', widths: [1920, 1440],
    rules: [noSidewaysScroll, visible('.tide-action-main .tide-hunt pre.tide-query'), fits('.tide-hunt pre.tide-query'),
      visible('.tide-hunt-sweeping'), visible('[role=group][aria-label="Query language"]'), visible('#hunt-window'),
      // The language and the window on the tab row, at its right; the whole story's indicators on offer; Copy on the first screen.
      sameLine('.tide-drawer-tabs', '[role=group][aria-label="Query language"]'), sameLine('.tide-drawer-tabs', '#hunt-window'),
      visible('.tide-hunt-story'), startsAbove('.tide-hunt [data-copy-target]', 900),
      // A-1's query is KQL: the control says the language of the query on the action, on the live page too.
      later(says('.tide-drawer-head [aria-label="Query language"] [aria-pressed="true"]', '^KQL$'))] },
  // The action page's context rail. From 1500px it's docked open beside the page, which has about 873px for the sweep's
  // card: the query wraps, nothing scrolls sideways and Copy is on the first screen. Below 1500px it's closed until `]`
  // opens it over the page. A sweep (its query) and an action of another kind (its checklist).
  { name: 'action-rail-sweep', route: '/actions/{huntAction}', setup: wideFont, widths: [[1920, 1000], [1600, 1000], [1500, 1000]],
    rules: [visible('#tide-rail'), ...actionRailHolds, ...actionPageFits, fits('.tide-hunt pre.tide-query'), onFirstScreen('.tide-hunt [data-copy-target]'),
      ...huntAsDesigned, ...actionHeadAsDesigned, raisedRows, sameLine('#files-h', '#tide-rail .tide-files-head [data-pick]')] },
  // Files from anywhere on the page: docked, and where the rail is closed until it opens over the page to show them.
  { name: 'action-page-files', route: '/actions/{huntAction}', setup: wideFont, widths: [[1920, 1000], [1440, 900]], rules: [pageTakesFiles] },
  // A file waiting in Files asks before the page is left; a dialog opened from the overlay gives focus back to Context.
  { name: 'action-staged-leave', route: '/actions/{huntAction}', setup: fileStaged, widths: [[1920, 1000]], rules: [stagedFilesAsk('.sidebar a[href="triage"]')] },
  { name: 'action-overlay-leave', route: '/actions/{huntAction}', setup: fileStaged, widths: [[1440, 900]], rules: [dialogFromOverlay('link')] },
  { name: 'action-overlay-palette', route: '/actions/{huntAction}', setup: openRail, widths: [[1440, 900]], rules: [dialogFromOverlay('palette')] },
  { name: 'action-hunt-adders', route: '/actions/{huntAction}', setup: wideFont, widths: [[1440, 900]], rules: [oneAdderAtATime] },
  // A file dropped where nothing takes it stays put: on pages with no drop target, and for whoever can't add files.
  ...[['drop-today', '/'], ['drop-triage', '/triage'], ['drop-action-viewer', '/actions/{huntAction}?as=dropviewer&roles=Viewer']].map(([name, route]) => (
    { name, route, widths: [[1440, 900]], rules: [fileDropStays] })),
  { name: 'action-rail-other', route: '/actions/{detailAction}', setup: wideFont, widths: [[1920, 1000], [1500, 1000]],
    rules: [visible('#tide-rail'), visible('#tide-rail .tide-facts'), visible('#tide-rail #files'), hidden('.content #files'), fits('.tide-rail-body > *'),
      ...actionPageFits] },
  { name: 'action-rail-narrow', route: '/actions/{huntAction}', setup: wideFont, widths: [[1440, 900], [780, 900]],
    rules: [hidden('#tide-rail'), visible('#tide-rail-toggle'), ...actionPageFits, fits('.tide-hunt pre.tide-query'), ...actionHeadAsDesigned,
      [1440, huntCopyRow], ...huntFound, hidden('.tide-page-drop')] },
  { name: 'action-rail-closed', route: '/actions/{huntAction}', setup: toggleRail, widths: [[1600, 1000]],
    rules: [hidden('#tide-rail'), widerThan('.tide-action-main', 1100), ...actionPageFits] },
  // Open over the page it's a modal dialog: named, the page dimmed behind it, Tab held in it; Escape, Close and a click
  // on the backdrop each close it and give focus back to the Context button; its Files box still takes a drop and a paste.
  { name: 'action-rail-overlay', route: '/actions/{huntAction}', setup: toggleRail, widths: [[1440, 900], [780, 900], [400, 800]],
    rules: [visible('#tide-rail'), ...actionRailHolds, noSidewaysScroll, focusIn('#tide-rail'), attr('#tide-rail', 'role', 'dialog'),
      attr('#tide-rail', 'aria-modal', 'true'), attr('#tide-rail', 'aria-label', 'About this action'), underIsInert, [780, dims(15, true)],
      ...tabWraps('#tide-rail', 40)] },
  ...['Escape', 'Close', 'backdrop'].map(how => ({ name: `action-rail-overlay-${how.toLowerCase()}`, route: '/actions/{huntAction}', setup: toggleRail,
    widths: how === 'backdrop' ? [[1440, 900], [780, 900]] : [[1440, 900], [780, 900], [400, 800]], rules: [overlayClosesBy(how)] })),
  { name: 'action-rail-overlay-files', route: '/actions/{detailAction}', setup: toggleRail, widths: [[1440, 900]], rules: [railFilesTakeDropAndPaste] },
  // Triage: the keys on one line, each row's flags fitting its line, and the reading pane open by itself from 1400px.
  { name: 'triage', route: '/triage', widths: [1920, 1440, 780],
    rules: [noSidewaysScroll, fits('.tide-page-head'), visible('.tide-triage-keys'), oneRow('.tide-triage-keys'), visible('.tide-triage-row.selected'), fits('.tide-triage-row'),
      fits('.tide-triage-flags'), [1400, visible('.tide-reading')]] },
  // The Sources tab's head: what the tab is, and Pull all beside it where there's room (under it where there isn't),
  // the button whole on one line, nothing past the card.
  { name: 'triage-pull-all', route: '/triage?tab=sources', setup: wideFont, widths: [1920, 1440, 780],
    rules: [noSidewaysScroll, visible('.tide-pull-head .tide-pull-all > button'), fits('.tide-pull-head'), fits('.content .tide-card-scroll'), [1440, oneRow('.tide-pull-head')],
      oneLine('.tide-pull-head .tide-pull-all > button'), notSqueezed('.tide-pull-head .tide-pull-all > button')] },
  { name: 'triage-narrow', route: '/triage', widths: [1300, 780], rules: [hidden('.tide-reading')] },
  { name: 'triage-account-menu-arrow', route: '/triage', setup: accountMenuArrow, widths: [1440], rules: [arrowIntoAccountMenu] },
  // Coverage: its tables scroll inside their cards at every width, the page itself never sideways.
  { name: 'coverage', route: '/coverage', widths: [1440, 780, 700, 660, 641, 400], rules: [noSidewaysScroll] },
  // The reading pane open by itself stays below the top bar as the page scrolls.
  { name: 'triage-scrolled', route: '/triage', setup: scrollDown, widths: [1440], rules: [visible('.tide-reading'), belowTopBar('.tide-reading')] },
  // The decision bar, as designed: the list a column of about 440px, the selected item's buttons in a bar at the foot of
  // the reading pane (not in the row), opening with the line that says why its main button is the main one; the bar's
  // buttons one row, by construction (letters spaced wider); Tab from the row goes to them.
  { name: 'triage-decision', route: '/triage', setup: wider, widths: [[1920, 1080], [1440, 900]],
    rules: [noSidewaysScroll, widthNear('.tide-triage-layout > .tide-card:first-child', 440, 16), visible('.tide-reading .tide-triage-decide'),
      sameBox('.tide-reading', '.tide-triage-decide', '.tide-reading-title'), above('.tide-reading-title', '.tide-triage-decide'),
      above('.tide-triage-why', '.tide-triage-acts'), oneRow('.tide-triage-decide .tide-triage-acts'), fits('.tide-triage-decide'), fits('.tide-triage-acts'),
      hidden('.tide-triage-row.selected .tide-triage-decide'), fits('.tide-triage-row'), tabReachesBar,
      // The pane as tall as what it holds, the bar straight under a short text, on the first screen; Attach to… on the
      // bar's one row with the rest; Read it here or Read again and Open the site in its head, beside the title.
      barFollowsText(32), controlsOneRow('.tide-reading .tide-triage-decide'),
      sameLine('.tide-reading .tide-triage-decide .tide-attach > button', '.tide-reading .tide-triage-decide .tide-triage-promote'),
      belowTopBar('.tide-reading'), onFirstScreen('.tide-reading .tide-triage-decide'),
      sameBox('.tide-reading-head', '.tide-reading-title', '.tide-reading-tools .btn'), above('.tide-reading-tools', '.tide-triage-decide')] },
  // An item not read here: its excerpt, then the bar straight under it, its buttons one row. Moving on with J from one
  // such item to the next, the bar's buttons stay where they were, so nothing jumps under the mouse.
  { name: 'triage-decision-unread', route: '/triage', setup: async page => { await wideFont(page); await selectUnread(page); }, widths: [[1920, 1080], [1440, 900]],
    rules: [barFollowsText(32), controlsOneRow('.tide-reading .tide-triage-decide'), fits('.tide-triage-decide'), onFirstScreen('.tide-reading .tide-triage-decide')] },
  { name: 'triage-decision-steady', route: '/triage', setup: selectUnread, widths: [[1920, 1080], [1440, 900]], rules: [barStaysPut(6, 4)] },
  // With a long text read, the text scrolls inside the pane and the bar stays at its foot on the first screen.
  { name: 'triage-decision-long', route: '/triage', setup: longKeptText, widths: [[1920, 1080], [1440, 900]],
    rules: [noSidewaysScroll, reachesBottom('.tide-reading', 24), belowTopBar('.tide-reading'), scrollsInside('.tide-reading-text'),
      onFirstScreen('.tide-reading .tide-triage-decide'), oneRow('.tide-triage-decide .tide-triage-acts'), fits('.tide-reading'),
      sameBox('.tide-reading-head', '.tide-reading-title', '.tide-reading-tools .btn'), oneRow('.tide-reading-tools'), fits('.tide-reading-head'),
      windowDoesntScroll] },
  // The full-height page doesn't scroll the window into blank space: not by End, nor the wheel over the pane or the
  // header; the bar stays on the first screen. At 1600x1000 too.
  { name: 'triage-decision-still', route: '/triage', setup: endAndWheel, widths: [[1920, 1080], [1600, 1000], [1440, 900]],
    rules: [windowDoesntScroll, onFirstScreen('.tide-reading .tide-triage-decide'), belowTopBar('.tide-reading'), onFirstScreen('.top-row')] },
  // A covered row's Attach and Promote, and a covered group's Attach all and Promote as one, share the bar's row. A
  // covered report has seven controls; where even their least widths don't fit (1440, letters spaced wider) Dismiss
  // and its reason go to a second row together, rather than spill out of the pane.
  { name: 'triage-covered', route: '/triage', setup: selectCovered(), widths: [1440, 1920],
    rules: [visible('.tide-reading'), sameLine('.tide-triage-decide .tide-triage-attach', '.tide-triage-decide .tide-triage-promote', '.tide-triage-decide .tide-attach > .btn'),
      [1800, oneRow('.tide-triage-decide .tide-triage-acts')], sameLine('.tide-triage-decide .tide-triage-end > select', '.tide-triage-decide .tide-triage-end > .btn'),
      fits('.tide-triage-decide'), fits('.tide-triage-row.selected'), mainButtonKeepsItsWords] },
  // A covered group's Attach all and Promote as one share a line too (the demo's covered group: Demo:TriageFiller below).
  { name: 'triage-covered-group', route: '/triage', setup: selectCovered(true), widths: [1440, 1920],
    rules: [visible('.tide-reading'), sameLine('.tide-triage-decide .tide-triage-attach', '.tide-triage-decide .tide-triage-promote'),
      oneRow('.tide-triage-decide .tide-triage-acts'), fits('.tide-triage-decide'), fits('.tide-triage-group.selected'), mainButtonKeepsItsWords] },
  // Below 1400px the pane that opened by itself is hidden and the decision stays in the selected row, reason and all.
  { name: 'triage-decision-narrow', route: '/triage', setup: wider, widths: [1000, 780],
    rules: [noSidewaysScroll, hidden('.tide-reading'), visible('.tide-triage-row.selected .tide-triage-decide'), visible('.tide-triage-row.selected .tide-triage-why'),
      visible('.tide-triage-row.selected .tide-triage-promote'), fits('.tide-triage-row.selected')] },
  // With the pane closed (by V, or below 1400px where it's hidden), the selected row stays a row: its excerpt a readable
  // measure of at most three lines, its why line and its buttons, Attach to… with them, one compact row, and its flags
  // on its meta line. Letters spaced wider. Each width signs in as its own person, as the pane's state is kept.
  ...[1920, 1440, 1280].map(w => ({ name: `triage-closed-${w}`, route: `/triage?as=c${w}${run}`, widths: [w],
    setup: async page => { await wideFont(page); if (w >= 1400) await pressV(1)(page); },
    rules: [hidden('.tide-reading'), controlsOneRow('.tide-triage-row.selected .tide-triage-decide'), excerptMeasure(3), rowDecisionCompact(76),
      fits('.tide-triage-row.selected'), fits('.tide-triage-flags'), sameLine('.tide-triage-row.selected .tide-triage-meta > span', '.tide-triage-row.selected .tide-triage-flags')] })),
  // A line's coverage is one compact line: "Covered Nov 11 · " and the article's title, cut with an ellipsis, whole in
  // its tooltip; We run stays on the CVEs' line. On every list: the pane open and closed (V), at each width, for a
  // report's line and a group's. Letters spaced wider. Each signs in as its own person, as the pane's state is kept.
  ...[1920, 1440, 1280].flatMap(w => [0, 1].map(v => {
    const open = (w >= 1400) === !v;
    return { name: `triage-coverage-${w}-${open ? 'open' : 'closed'}`,
      route: `/triage?as=cv${w}${v}${run}`, widths: [w], setup: async page => { await wideFont(page); await pressV(v)(page); },
      rules: [open ? visible('.tide-reading') : hidden('.tide-reading'), visible('.tide-triage-group .tide-triage-cov'),
        oneLine('.tide-triage-cov'), coverageCutWhole, weRunBesideTheCves, fits('.tide-triage-covers'), fits('.tide-triage-row'), noSidewaysScroll] };
  })),
  // An open group's reports don't flag each other "Looks like": the group says so already. Duplicate (D) stays.
  { name: 'triage-group-open', route: '/triage', setup: openGroup, widths: [1440], rules: [noLooksLikeInItsGroup] },
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
  // The decision's Attach to… (in the selected row, or under the reading pane where the pane shows) and the account
  // menu close when done with, and their Escape is theirs alone: Triage's own (the reading pane's) doesn't hear it. A
  // press on a part of one that takes no focus leaves it open. Signed in as someone of this run's own, as the pane's
  // state is kept. The Attach panel stays inside the page, however narrow.
  { name: 'triage-attach-popout', route: `/triage?as=a1${run}`, widths: [1440, 800],
    rules: popoutCloses('Attach to…', { wrap: '.tide-triage-decide .tide-attach', opener: '> button', panel: '.tide-attach-panel', inside: 'input', bare: '.tide-attach-panel .form-text' }) },
  { name: 'triage-attach-fits', route: '/triage', setup: openFirst('.tide-triage-decide .tide-attach > button', '.tide-attach-panel'),
    widths: [1920, 1440, 1000, 800, 400], rules: [insidePage('.tide-attach-panel')] },
  { name: 'triage-account-popout', route: `/triage?as=a2${run}`, widths: [1440, 800],
    rules: popoutCloses('the account menu', { wrap: '.tide-profile', opener: '.tide-profile-btn', panel: '.tide-popover', inside: '.tide-popover button' }) },
  { name: 'brief-export-popout', route: '/briefs/{today}', widths: [1440],
    rules: popoutCloses('Export indicators', { wrap: '.tide-export', opener: '> button', panel: '.tide-export-panel', inside: '.tide-export-panel a', bare: '.tide-export-panel .tide-field-label' }) },
  // The menu buttons and + Filter the same way: the Actions list's Status menu, + Filter there, and an action's More.
  { name: 'actions-status-popout', route: '/actions?view=open&layout=list', widths: [1440],
    rules: popoutCloses('the Status menu', { wrap: '.tide-table .tide-menubutton', opener: '.tide-menubutton-btn', panel: '.tide-menubutton-pop', inside: '[role=menuitem]' }) },
  { name: 'actions-filter-popout', route: '/actions?view=open&layout=list', widths: [1440, 800],
    rules: popoutCloses('+ Filter', { wrap: '.tide-filter-menu', opener: '.tide-filter-add', panel: '.tide-filter-pop', inside: '[role=menuitem]' }) },
  { name: 'action-more-popout', route: '/actions/{huntAction}', widths: [1440],
    rules: popoutCloses('the More menu', { wrap: '.tide-menubutton', opener: '.tide-menubutton-btn', panel: '.tide-menubutton-pop', inside: '[role=menuitem]' }) },
  // Each popout opens in place on a narrow or short window: no sideways scroll, nothing wider than what shows, inside
  // the window top to bottom (upward where there's more room above), never over its button, and nothing of it cut off.
  ...[
    ['triage', '/triage', 'Attach to…', '.tide-triage-decide .tide-attach > button', '.tide-attach-panel'],
    ['account', '/triage', 'the account menu', '.tide-profile-btn', '.tide-profile .tide-popover'],
    ['export', '/briefs/{today}', 'Export indicators', '.tide-export > button', '.tide-export-panel'],
    ['status', '/actions?view=open&layout=list', 'the Status menu', '.tide-table .tide-menubutton-btn', '.tide-menubutton-pop'],
    ['filter', '/actions?view=open&layout=list', '+ Filter', '.tide-filter-menu .tide-filter-add', '.tide-filter-pop'],
    ['action-more', '/actions/{huntAction}', 'the More menu', '.tide-menubutton-btn', '.tide-menubutton-pop'],
    ['library-more', '/library/queries', 'the entry’s More menu', '.tide-library-entry .tide-menubutton-btn', '.tide-menubutton-pop'],
  ].map(([name, route, label, opener, panel]) => ({ name: `popout-in-place-${name}`, route, widths: [[1024, 900], [780, 900], [780, 380], [400, 800], [400, 330]],
    rules: [opensInPlace(label, opener, panel)] })),
  // Focus after a dialog opened from a popout: back on More once Delete is cancelled, and on + Filter after the palette.
  { name: 'library-delete-focus', route: '/library/queries', widths: [1440], rules: [deleteCancelledFocusesMore] },
  { name: 'palette-over-filter', route: '/actions?view=open&layout=list', widths: [1440], rules: [paletteGivesFocusBack] },
  // Save view: Enter in its Name box saves it, and the new view's menu is named for what it offers. Someone of this
  // run's own, so no one else's Actions shows the view.
  { name: 'save-view-enter', route: `/actions?view=open&layout=list&as=sv${run}`, widths: [1440], rules: [saveViewByEnter] },
  // Triage's context rail: what TIDE knows about the selected item. From 1500px it's docked open beside the list and the
  // reading pane (triageBesideTheRail): for a covered report its coverage and story, for a covered group the other
  // reports with Attach all and Not the same, for a report naming a CVE its facts.
  { name: 'triage-rail', route: '/triage', setup: railOn('covered'), widths: [[1920, 1080], [1600, 1000], [1500, 1000]],
    rules: [...triageBesideTheRail, ...triageRailFits, visible('#tide-rail #rail-covered-h'), visible('#tide-rail #rail-story-h'),
      visible('#tide-rail section[aria-labelledby="rail-covered-h"] .tide-rail-list a')] },
  { name: 'triage-rail-group', route: '/triage', setup: railOn('group'), widths: [[1920, 1080], [1600, 1000], [1500, 1000]],
    rules: [...triageBesideTheRail, ...triageRailFits, visible('#tide-rail #rail-same-h'),
      visible('#tide-rail section[aria-labelledby="rail-same-h"] .tide-triage-rail-acts button'), oneRow('#tide-rail .tide-triage-rail-acts')] },
  { name: 'triage-rail-cve', route: '/triage', setup: railOn('cve'), widths: [[1920, 1080], [1500, 1000]],
    rules: [...triageBesideTheRail, ...triageRailFits, visible('#tide-rail #rail-cve-h'), oneRow('#tide-rail .tide-triage-rail-facts')] },
  { name: 'triage-rail-dark', route: '/triage', setup: railOn('covered'), context: { theme: 'dark' }, widths: [[1600, 1000]],
    rules: [...triageBesideTheRail, ...triageRailFits] },
  // `]` closes the docked rail, and the page takes the width back: the list 440px again and the bar one row.
  { name: 'triage-rail-closed', route: '/triage', setup: toggleRail, widths: [[1600, 1000], [1500, 1000]],
    rules: [noSidewaysScroll, hidden('#tide-rail'), visible('#tide-rail-toggle'), visible('.tide-reading'),
      widthNear('.tide-triage-layout > .tide-card:first-child', 440, 16), oneRow('.tide-triage-decide .tide-triage-acts'), onFirstScreen('.tide-reading .tide-triage-decide')] },
  // Where it's docked, Escape on the list is still Triage's own: it closes the reading pane, and the rail stays.
  { name: 'triage-rail-docked-escape', route: `/triage?as=r2${run}`, setup: escapeOnRow, widths: [[1600, 1000]],
    rules: [hidden('.tide-reading'), visible('#tide-rail'), visible('.tide-triage-row.selected .tide-triage-decide')] },
  // Below 1500px it's closed until `]` opens it over the page, as Triage was before it.
  { name: 'triage-rail-shut', route: '/triage', setup: wideFont, widths: [[1440, 900], [780, 900]],
    rules: [noSidewaysScroll, hidden('#tide-rail'), visible('#tide-rail-toggle'), [1400, visible('.tide-reading')], [1400, oneRow('.tide-triage-decide .tide-triage-acts')]] },
  // Open over the page it's a modal dialog, named, holding focus; Escape, Close and a click on the backdrop each close it
  // and give focus back to the Context button (and Escape leaves the reading pane alone: triage-rail-keys).
  { name: 'triage-rail-overlay', route: '/triage', setup: railOverOn('covered'), widths: [[1440, 900], [780, 900]],
    rules: [noSidewaysScroll, visible('#tide-rail'), ...triageRailFits, visible('#tide-rail #rail-covered-h'), focusIn('#tide-rail'),
      attr('#tide-rail', 'role', 'dialog'), attr('#tide-rail', 'aria-label', 'What TIDE knows'), underIsInert, [780, dims(15, true)],
      ...tabWraps('#tide-rail', 30)] },
  ...['Escape', 'Close', 'backdrop'].map(how => ({ name: `triage-rail-overlay-${how.toLowerCase()}`, route: '/triage', setup: railOverOn('covered'),
    widths: [[1440, 900], [780, 900]], rules: [overlayClosesBy(how), visible('.tide-triage-row.selected')] })),
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
  // Our stack's context rail: a row is selected, and the selected product is in the rail. From 1500px it's docked open
  // beside the page, which has about 873px: the table fits beside it with no sideways scroll, and the rail's flat
  // sections (facts, the buttons, in triage now, articles, open actions, notes) each fit the column. Below 1500px it's
  // closed until `]` opens it over the page, where it's a modal dialog. The 780px stacked rows stay.
  { name: 'stack-rail', route: '/stack', setup: wideFont, widths: [1920, 1600, 1500],
    rules: [noSidewaysScroll, visible('#tide-rail .tide-card'), besides('#main-content', '#tide-rail'), fits('.tide-stack-list'), fits('.tide-stack-table td'),
      visible('.tide-stack-item.selected'), attr('.tide-stack-item.selected', 'aria-current', 'true'), attr('#tide-rail', 'role', null),
      visible('#tide-rail .tide-rail-head kbd'), visible('#tide-rail-close'), flat('#tide-rail .tide-rail-body > section'),
      fits('.tide-rail-body'), fits('#tide-rail .tide-rail-body > section'), fits('#tide-rail .tide-stack-rail-buttons'), fits('#tide-rail .tide-facts'),
      visible('#tide-rail section[aria-labelledby="rail-triage-h"]'), visible('#tide-rail section[aria-labelledby="rail-articles-h"]'),
      visible('#tide-rail section[aria-labelledby="rail-open-actions-h"]'), visible('#tide-rail section[aria-labelledby="rail-notes-h"]'),
      linksAlike('#tide-rail .tide-rail-list a'),
      ringOnFocus('#tide-rail .tide-stack-rail-buttons a, #tide-rail .tide-stack-rail-buttons button')] },
  // Stacked on a narrow window, the selected row's accent sits clear of the text of each of its lines.
  { name: 'stack-rail-stacked', route: '/stack', setup: wideFont, widths: [780, 641], rules: [clearOfEdge('.tide-stack-item.selected', 5)] },
  { name: 'stack-rail-shut', route: '/stack', setup: wideFont, widths: [1440, 780],
    rules: [noSidewaysScroll, hidden('#tide-rail'), visible('#tide-rail-toggle'), fits('.tide-stack-list'), visible('.tide-stack-item.selected'),
      [901, fits('.tide-stack-table td')]] },
  { name: 'stack-rail-closed', route: '/stack', setup: toggleRail, widths: [1920, 1500],
    rules: [noSidewaysScroll, hidden('#tide-rail'), visible('#tide-rail-toggle'), fits('.tide-stack-list')] },
  { name: 'stack-rail-over', route: '/stack', setup: toggleRail, widths: [1440, 780],
    rules: [noSidewaysScroll, visible('#tide-rail .tide-card'), fits('.tide-rail-body'), fits('#tide-rail .tide-rail-body > section'), focusIn('#tide-rail'),
      attr('#tide-rail', 'role', 'dialog'), attr('#tide-rail', 'aria-modal', 'true'), tabStaysIn('#tide-rail', 40)] },
  // J and K move the selected row and the rail follows; a click selects; Enter opens the product.
  { name: 'stack-keys', route: '/stack', widths: [1920, 1440], rules: [stackKeys] },
  // Adversaries' context rail: on the list, the selected profile (its facts, Open profile and Watch, Lately, open
  // actions, top techniques, We run); on a profile, its facts and what can be done to it (Watch, the Navigator layer,
  // Retire), the articles to link, We run and Hunting, the page keeping the story. Docked from 1500px, beside the page
  // with no sideways scroll; below, closed until `]` opens it over the page as a modal dialog. Never the notes.
  { name: 'adversaries-rail', route: '/adversaries', setup: wideFont, widths: [1920, 1600, 1500],
    rules: [...adversaryRailHolds, fits('.tide-dt-scroll'), visible('.tide-adv-item.selected'), attr('.tide-adv-item.selected', 'aria-current', 'true'),
      visible('#tide-rail section[aria-labelledby="rail-lately-h"]'), visible('#tide-rail section[aria-labelledby="rail-open-actions-h"]'),
      visible('#tide-rail section[aria-labelledby="rail-techniques-h"]'), fits('#tide-rail .tide-adv-rail-techniques'), linksAlike('#tide-rail .tide-rail-list a')] },
  { name: 'adversaries-rail-over', route: '/adversaries', setup: toggleRail, widths: [1440],
    rules: [...adversaryRailOverHolds, fits('.tide-dt-scroll')] },
  { name: 'adversaries-keys', route: '/adversaries', widths: [1920, 1440], rules: [adversaryKeys] },
  { name: 'adversary-rail', route: '/adversaries/{adversary}', setup: wideFont, widths: [1920, 1600, 1500],
    rules: [...adversaryRailHolds, hidden('.content .tide-facts'), visible('#tide-rail section[aria-labelledby="rail-do-h"]'),
      visible('#tide-rail a[href$="/navigator.json"]'), visible('.content #adv-timeline-h'), visible('.content #adv-about-h'), fits('.content .tide-adv-heat')] },
  { name: 'adversary-rail-over', route: '/adversaries/{adversary}', setup: toggleRail, widths: [1440],
    rules: adversaryRailOverHolds },
  // The Library: one header, the one search on the tab row beside the tabs, and the list high on the page; its findings,
  // tab by tab, for a search's words.
  { name: 'library', route: '/library/queries', setup: wideFonts, widths: [1920, 1440, 780],
    rules: [noSidewaysScroll, visible('#library-find'), [1440, sameLine('.tide-library-tabrow .tide-tabbar-page', '#library-find')],
      [1440, closeBelow('.tide-library-list', '.tide-library-tabrow', 120)], visible('.tide-library-group'),
      // The entry's Copy, Edit and More at its top right, the entry the height of the window, and Show retired a checkbox.
      [1000, sameLine('.tide-library-entry h3', '.tide-library-entry [data-copy-target]')], [1440, reachesBottom('.tide-library-entry', 24)],
      visible('#sq-retired'), hidden('.form-switch #sq-retired')] },
  // The entry's More menu opens wholly in view: Retire and Delete aren't cut off by the scrolling detail panel.
  { name: 'library-more', route: '/library/queries', setup: openEntryMore, widths: [1920, 1440, 780, 400], rules: [menuItemsVisible, insidePage('.tide-menubutton-pop')] },
  // The entry's More menu closes when done with, as every popout does.
  { name: 'library-more-popout', route: '/library/queries', widths: [1920, 1440, 780, 400],
    rules: popoutCloses('the entry’s More menu', { wrap: '.tide-library-entry .tide-menubutton', opener: '.tide-menubutton-btn', panel: '.tide-menubutton-pop', inside: '[role=menuitem]' }) },
  { name: 'library-search', route: '/library/queries?q=sweep', setup: wideFonts, widths: [1440, 780], rules: [noSidewaysScroll, visible('#library-find'), visible('.tide-library-list')] },
  // The selected entry's context in the rail: docked and open from 1500px, with the list and the entry fitting beside
  // it (about 870px); closed below that until asked for, and never a sideways scroll. Wider letters (LETTER_SPACING).
  { name: 'library-rail', route: '/library/queries', setup: everyIndicatorSweep, widths: [1920, 1600, 1500],
    rules: [...libraryRailHolds, ...libraryFitsBesideRail, visible('.tide-library-fold'), chipHasRoom, ...rowsTwoLines] },
  { name: 'library-rail-closed', route: '/library/queries', setup: wideFont, widths: [1440, 780],
    rules: [noSidewaysScroll, hidden('#tide-rail'), visible('#tide-rail-toggle'), visible('.tide-library-entry-name'), fits('.tide-library-detail')] },
  // Below 1500px the rail opens over the page as a dialog, holds what it holds there too, and Escape closes it.
  { name: 'library-rail-overlay', route: '/library/queries', setup: libraryOverlay, widths: [1440, 780],
    rules: [noSidewaysScroll, ...libraryRailHolds, attr('#tide-rail', 'role', 'dialog'), overlayClosesBy('Escape')] },
  // Open over the Library, the page under it is dimmed and inert, and Tab and Shift+Tab wrap within the rail.
  { name: 'library-rail-overlay-focus', route: '/library/queries', setup: libraryOverlay, widths: [1440, 780],
    rules: [underIsInert, dims(15, true), ...tabWraps('#tide-rail', 30)] },
  // Each tab's entry has its rail (the demo has no open outreach action, so a template's isn't tried there).
  { name: 'library-rail-templates', route: '/library/templates', setup: wideFont, widths: [1920, 1500],
    rules: [visible('#tide-rail .tide-lib-facts'), visible('#tide-rail #lib-fills-h'), visible('#tide-rail #lib-used-h'), ...libraryFitsBesideRail.slice(0, 6), chipHasRoom, ...rowsTwoLines] },
  { name: 'library-rail-playbooks', route: '/library/playbooks', setup: wideFont, widths: [1920, 1500],
    rules: [visible('#tide-rail .tide-lib-facts'), visible('#tide-rail #lib-used-h'), ...libraryFitsBesideRail.slice(0, 6), chipHasRoom, ...rowsTwoLines] },
  // Someone who works actions but has none open of the entry's kind (a new sign-in, in no team): Try it says so in its
  // section, one short line within the rail, rather than the section going.
  ...['queries', 'templates', 'playbooks'].map(tab => ({ name: `library-rail-none-${tab}`, route: `/library/${tab}?as=Nobody${run}&roles=ActionOwner`,
    setup: wideFont, widths: [1920, 1440, 780], rules: [[1500, visible('#tide-rail #lib-try-h')], [1500, visible('#tide-rail .tide-lib-try-none')],
      [1500, oneLine('#tide-rail .tide-lib-try-none', 2)], [1500, fits('#tide-rail .tide-lib-try')], hidden('#lib-try-action'), noSidewaysScroll] })),
  // Copy filled in puts exactly the preview on the clipboard.
  { name: 'library-rail-copy', route: '/library/queries', setup: everyIndicatorSweep, widths: [1920], context: clipboard,
    rules: [copiesExactly('[data-copy-target="lib-try-text"]', '#lib-try-text')] },
  // The keys, as on Triage and the Actions list.
  { name: 'library-keys', route: '/library/queries', widths: [1920, 1440], rules: [libraryKeys] },
  // The More menu opens wholly in view beside the docked rail, and at the overlay's widths.
  { name: 'library-more-rail', route: '/library/queries', setup: openEntryMore, widths: [1600, 1500, 1440], rules: [menuItemsVisible] },
  // Search for an indicator or a CVE: the answer in the page, in one card, and the hunt query, who is sweeping for it and
  // the export in the context rail. From 1500px the rail is docked open and the page has about 873px, which the articles'
  // table fits; the query wraps, and Copy puts exactly its text on the clipboard. Below 1500px the rail is closed until
  // `]` opens it over the page, as a modal dialog. Wider letters throughout.
  ...[['search-rail-ip', '203.0.113.45'], ['search-rail-domain', 'update-check.example.net']].map(([name, q]) => (
    { name, route: `/search?q=${q}`, setup: wideFont, widths: [[1920, 1000], [1600, 1000], [1500, 1000]], context: clipboard,
      rules: [visible('#tide-rail'), ...searchRailHolds(), ...searchPageFits, copiesExactly('#tide-rail [data-copy-target="lookup-query"]', '#lookup-query')] })),
  { name: 'search-rail-cve', route: '/search?q=CVE-2026-0001', setup: wideFont, widths: [[1920, 1000], [1600, 1000], [1500, 1000]],
    rules: [visible('#tide-rail'), ...searchRailHolds(false), ...searchPageFits, hidden('#lookup-query')] },
  { name: 'search-rail-narrow', route: '/search?q=203.0.113.45', setup: wideFont, widths: [[1440, 900], [780, 900]],
    rules: [hidden('#tide-rail'), visible('#tide-rail-toggle'), ...searchPageFits] },
  { name: 'search-rail-closed', route: '/search?q=203.0.113.45', setup: toggleRail, widths: [[1600, 1000]],
    rules: [hidden('#tide-rail'), widerThan('.content', 1100), ...searchPageFits] },
  { name: 'search-rail-overlay', route: '/search?q=203.0.113.45', setup: toggleRail, widths: [[1440, 900], [780, 900]], context: clipboard,
    rules: [visible('#tide-rail'), ...searchRailHolds(), noSidewaysScroll, focusIn('#tide-rail'), attr('#tide-rail', 'role', 'dialog'),
      attr('#tide-rail', 'aria-label', 'Do something with it'), underIsInert, ...tabWraps('#tide-rail', 30),
      copiesExactly('#tide-rail [data-copy-target="lookup-query"]', '#lookup-query')] },
  // A search for words has no rail, and its page is as before.
  { name: 'search-words', route: '/search?q=gateway', widths: [1920, 780], rules: [noSidewaysScroll, hidden('#tide-rail'), hidden('#tide-rail-toggle')] },
  { name: 'trends', route: '/trends', widths: [1920], rules: [noSidewaysScroll, widerThan('.content', 1400)] },
  // The article slide is the room's: each recommended line (one row, the action it became beside a raised one: the
  // sample's hotfix line is A-2) and the article's other actions, with none of the presenter's controls (the rail has
  // them, shut at these widths).
  { name: 'slide-article', route: '/present/{today}', setup: toArticleSlide, widths: [1440, 1100, 900],
    rules: [
      [1001, oneRow('.tide-reco li')],
      visible('.tide-reco li.is-raised .tide-reco-done'), fits('.tide-reco'),
      [1001, oneRow('.tide-slide-actlist > li')],
      hidden('.tide-slide :is(select, input, .tide-quick-add)'),
      // An other action's team and due stay readable beside its title.
      notSqueezed('.tide-slide-actlist .tide-present-help'),
    ] },
  // The article slide shows its indicators, defanged chips the room reads without a click (a chip opens the larger card),
  // beside its tags and CVEs: none, two, or a dozen (ten shown, then "+2 more") with a dozen CVEs and a long hash and
  // address. The chips wrap within the slide, and the slide still fits. Wider letters.
  ...[[0, 'two', 2], [1, 'none', 0], [2, 'dozen', 10]].map(([n, name, chips]) => (
    { name: `slide-iocs-${name}`, route: '/present/{today}', setup: articleSlide(n), widths: [1920, 1440, 1100, 900, 780],
      rules: [noSidewaysScroll, slideFits, fits('.tide-slide-body'), fits('.tide-present-top'),
        ...(chips === 0 ? [hidden('.tide-slide-iocsec')] : [fits('.tide-slide-iocs'), shownCount('.tide-slide-iocs .tide-slide-ioc', chips)]),
        ...(chips === 10 ? [visible('.tide-slide-iocs .tide-slide-ioc-more'), fits('.tide-slide-cves'), slideLinksReadable] : [])] })),
  // The card a slide's "+N more" opened (the indicators', the CVEs') gives focus back to it as it closes, by Escape or
  // its Close.
  { name: 'slide-iocs-card-escape', route: '/present/{today}', setup: cardClosed('.tide-slide-ioc-more', '#present-iocs-title', 'Escape'), widths: [1920, 1440],
    rules: [hidden('#present-iocs-title'), focusOn('button.tide-slide-ioc-more')] },
  { name: 'slide-cves-card-close', route: '/present/{today}', setup: cardClosed('.tide-slide-cve-more:not(.tide-slide-ioc-more)', '#present-cves-title', 'Close the CVEs'), widths: [1920, 1440],
    rules: [hidden('#present-cves-title'), focusOn('.tide-slide-cve-more:not(.tide-slide-ioc-more)')] },
  { name: 'slide-cves-card-escape', route: '/present/{today}', setup: cardClosed('.tide-slide-cve-more:not(.tide-slide-ioc-more)', '#present-cves-title', 'Escape'), widths: [1440],
    rules: [hidden('#present-cves-title'), focusOn('.tide-slide-cve-more:not(.tide-slide-ioc-more)')] },
  // The slide stays dark in the light theme too: its "+N more" links read on it in both.
  { name: 'slide-iocs-dozen-dark', route: '/present/{today}', setup: articleSlide(2), context: { theme: 'dark' }, widths: [1440], rules: [slideLinksReadable] },
  // The footer names each slide (cut short as the room allows, the whole name in its title), on the title slide, which
  // says what comes before the articles. Wider letters.
  { name: 'slide-footer-names', route: '/present/{today}', setup: wideFont, widths: [1920, 1440, 1100, 900, 780],
    rules: [noSidewaysScroll, visible('.tide-progress-name'), visible('.tide-agenda-first'), fits('.tide-progress'), fits('.tide-present-bottom'),
      inWindow('.tide-present-bottom :is(button, select)'), slideFits] },
  // The presenter deals with the day's loose ends on their slides (Demo:SlideFiller carries the opening note over): the
  // note's Still current and Edit, and a missing number's box and Save, in the window, and the slide still fits.
  { name: 'slide-note-presenter', route: '/present/{today}', setup: slideWith('.tide-slide-note'), widths: [1920, 1440, 1100, 900, 780],
    rules: [noSidewaysScroll, visible('.tide-slide-presenter'), fits('.tide-slide-presenter'), inWindow('.tide-slide-presenter :is(button, input)'), slideFits] },
  // Keyboard and focus in the presenter's strips: Edit puts focus in the note's box; Escape there cancels, back on Edit,
  // still on the slide; Still current leaves focus on the slide's heading as the strip goes. A number that isn't one is
  // marked on its box with why; Enter saves one, and focus goes to the slide's heading. Late: these change the demo's day.
  // Actions that need review: each action with why it's there (late, escalated, quiet too long: the demo's earlier
  // brief's are quiet two business days), its last update, and the one thing to do, in its columns; a title that wraps
  // flows on from its priority badge. Read only: no meeting, nothing saved.
  { name: 'slide-open-actions', route: '/present/{today}', setup: slideWith('.tide-slide-open'), widths: [1920, 1440, 1100, 780],
    rules: [noSidewaysScroll, slideFits, fits('.tide-slide-body'), visible('.tide-slide-open .tide-slide-next'), notSqueezed('.tide-slide-open .tide-slide-late'),
      [1100, oneLine('.tide-slide-open .tide-slide-next')], visible('.tide-slide-open .tide-slide-why'), visible('.tide-slide-open .tide-slide-waited'),
      notSqueezed('.tide-slide-open .tide-slide-waited'), quietRow, badgeWithTitle] },
  // An action opened from its row and closed with Escape: focus is back on that row's title.
  { name: 'slide-open-actions-closed', route: '/present/{today}', setup: reviewActionClosed, widths: [1920],
    rules: [hidden('#present-action-title'), focusOn('.tide-slide-open tbody tr:first-child .tide-slide-row-open')] },
  // The organization's settings: the two business-day limits for Actions that need review, each box with its unit.
  { name: 'admin-organization', route: '/admin/organization', widths: [1920, 1440, 1100, 780],
    rules: [noSidewaysScroll, fits('.tide-card'), visible('#org-stale-open'), visible('#org-stale-blocked'),
      oneRow('.input-group:has(#org-stale-open)'), oneRow('.input-group:has(#org-stale-blocked)')] },
  // Help: each release's highlights for the reader's roles, the full notes folded under them, linked to as "See all
  // changes" (that release's notes open).
  { name: 'help', route: '/help?release=1.13.0', setup: wideFont, widths: [1920, 1440, 780],
    rules: [noSidewaysScroll, fits('.tide-help .tide-card'), visible('.tide-help-notes[open] .tide-md')] },
  // Help's Guides: each guide's name, what it shows and Start, within its row.
  { name: 'help-guides', route: '/help', setup: wideFont, widths: [1920, 1440, 780],
    rules: [noSidewaysScroll, visible('#help-guides'), fits('.tide-help-guides > li'), fits('.tide-help-guide-text'), visible('#help-guide-present')] },
  // Learn to present, in rehearsal (it changes nothing): the title slide's step beside the slides' bar; the rail's step
  // on the rail docked, or on Rail where it's shut; raising, pointed at on the rail's Raise (disabled) and kept off the
  // rail; ending, on End the meeting (disabled); Escape ends the guide and focus is on the page's heading.
  { name: 'rehearsal-title', route: '/help', setup: rehearsal(), widths: [1920, 1440, 780],
    rules: [...rehearsalRules, visible('.tide-progress.tide-tip-target'), ...tabWraps('.tide-tour', 4)] },
  { name: 'rehearsal-title-dark', route: '/help', context: { theme: 'dark' }, setup: rehearsal(), widths: [1920, 1440, 780], rules: rehearsalRules },
  { name: 'rehearsal-rail', route: '/help', setup: rehearsal('Enter'), widths: [1920, 1440, 780],
    rules: [...rehearsalRules, visible('#present-rail.tide-tip-target, #present-rail-toggle.tide-tip-target'), [1500, visible('#present-rail.is-docked')]] },
  { name: 'rehearsal-raise', route: '/help', setup: rehearsal('Enter', 'Enter'), widths: [1920, 1440, 780],
    rules: [...rehearsalRules, [1500, visible('#present-rail .tide-tip-target:disabled')], [1500, allBesides('.tide-tour', '#present-rail')]] },
  { name: 'rehearsal-end', route: '/help', setup: rehearsal('Enter', 'Enter', 'Enter', 'Enter', 'Enter', 'Enter'), widths: [1920, 1440, 780],
    rules: [...rehearsalRules, visible('.tide-wrap'), [1500, visible('#present-end.tide-tip-target:disabled')]] },
  { name: 'rehearsal-escape', route: '/help', setup: rehearsal('Escape'), widths: [1440],
    rules: [hidden('.tide-tour'), hidden('.tide-tour-scrim'), focusOn('h1'), visible('.tide-rehearsal')] },
  // Each page's Show me around, in its header, at three widths.
  ...[['today', '/'], ['triage', '/triage'], ['article', '/articles/{vpnArticle}'], ['actions', '/actions'], ['action', '/actions/{detailAction}'],
    ['metrics', '/metrics'], ['trends', '/trends'], ['stories', '/stories'], ['adversaries', '/adversaries'], ['coverage', '/coverage'],
    ['library', '/library'], ['stack', '/stack'], ['search', '/search'], ['workload', '/workload'], ['rotation', '/rotation']]
    .map(([key, route]) => ({ name: `guide-link-${key}`, route, setup: wideFont, widths: [1920, 1440, 780], rules: guideLinkRules })),
  // A page's guide, its steps beside what they point at: on the page's own controls; on the context rail docked (1920) or
  // on its Context button where it's shut (1440, 780); and a lookup's export in the rail.
  { name: 'guide-metrics', route: '/metrics', setup: guideOn(), widths: [1920, 1440, 780], rules: [...guideRules, belowTopBar('.tide-tip-target'), ...tabWraps('.tide-tour', 4)] },
  { name: 'guide-triage-decide', route: '/triage', setup: guideOn('Enter', 'Enter', 'Enter'), widths: [1920, 1440, 780], rules: guideRules },
  { name: 'guide-actions-rail', route: '/actions', setup: guideOn('Enter', 'Enter', 'Enter'), widths: [1920, 1440, 780],
    rules: [...railGuideRules, [1500, visible('#tide-rail.tide-tip-target')]] },
  { name: 'guide-today-rail', route: '/', setup: guideOn('Enter', 'Enter', 'Enter', 'Enter'), widths: [1920, 1440, 780],
    rules: [...railGuideRules, visible('#tide-rail.tide-tip-target, #tide-rail-toggle.tide-tip-target')] },
  { name: 'guide-search-export', route: '/search?q=203.0.113.45', setup: guideOn('Enter', 'Enter', 'Enter'), widths: [1920, 1440, 780],
    rules: [...railGuideRules, [1500, visible('#lookup-export-h.tide-tip-target')]] },
  { name: 'guide-rotation', route: '/rotation', setup: guideOn('Enter'), widths: [1920, 1440, 780], rules: [...guideRules, belowTopBar('.tide-tip-target')] },
  // Workload's first step rings its tall grid of teams: from the grid's top, below the top bar, with the step clear of the
  // bar and the search box in it.
  { name: 'guide-workload', route: '/workload', setup: guideOn(), widths: [1920, 1440, 780],
    rules: [...guideRules, belowTopBar('.tide-tip-target'), noOverlap('.tide-tour, .tide-top-find')] },
  { name: 'guide-dark', route: '/stack', context: { theme: 'dark' }, setup: guideOn(), widths: [1440], rules: guideRules },
  // Started from Help's Guides: its page opens, the step on its control; the article editor's on a real article.
  { name: 'guide-from-help', route: '/help', setup: guideFromHelp('trends'), widths: [1440], rules: [...guideRules, stillAt('/trends')] },
  // Escape from it: Help's Start isn't on this page, so focus is on the page's heading.
  { name: 'guide-from-help-escape', route: '/help', setup: guideFromHelp('trends', 'Escape'), widths: [1440], rules: [hidden('.tide-tour'), focusOn('main h1')] },
  // Started from low on Help: the new page opens scrolled, and its step's control is brought into view below the top bar.
  { name: 'guide-from-help-scrolled', route: '/help', setup: guideFromHelp('metrics'), widths: [1920, 1440, 780],
    rules: [...guideRules, stillAt('/metrics'), belowTopBar('.tide-tip-target'), inWindow('.tide-tip-target')] },
  { name: 'guide-from-help-editor', route: '/help', setup: guideFromHelp('article'), widths: [1920], rules: guideRules },
  // Escape ends it, and focus is back on Show me around.
  { name: 'guide-escape', route: '/library', setup: guideOn('Escape'), widths: [1440],
    rules: [hidden('.tide-tour'), hidden('.tide-tour-scrim'), focusOn('#tide-guide-start')] },
  // From here on, pages change the demo for those after them: a sharded run keeps them all in its last shard, in order.
  CHANGES_THE_DEMO,
  // What's new, as someone who already uses TIDE opens it after the upgrade (asked for on the demo with ?onboarding=on;
  // it's marked seen as it opens, so a different person at each width): a dialog in the window, its highlights
  // scrolling between the title and the buttons, focus on Got it, in both themes.
  { name: 'whatsnew', route: '/help', setup: whatsNewAs({ 1920: 'Robin', 1440: 'Sam', 780: 'Jordan&roles=Viewer' }), widths: [1920, 1440, 780],
    rules: whatsNewRules },
  { name: 'whatsnew-dark', route: '/help', context: { theme: 'dark' }, setup: whatsNewAs({ 1920: 'Dana', 1440: 'Lee', 780: 'Kim&roles=Viewer' }),
    widths: [1920, 1440, 780], rules: whatsNewRules },
  // Today's tip, once What's new is closed: a card in the window, beside the Context button it rings, with nothing
  // modal left over; and ] still shows and hides the rail with the tip up.
  { name: 'tip', route: '/help', setup: tipAs({ 1920: 'Tia', 1440: 'Tom', 780: 'Tess&roles=Viewer' }), widths: [1920, 1440, 780],
    rules: tipRules },
  { name: 'tip-dark', route: '/help', context: { theme: 'dark' }, setup: tipAs({ 1920: 'Uma', 1440: 'Ugo', 780: 'Una&roles=Viewer' }),
    widths: [1920, 1440, 780], rules: tipRules },
  { name: 'tip-keys', route: '/help', setup: tipAs({ 1920: 'Vic' }, ']'), widths: [1920],
    rules: [visible('.tide-tip'), attr('html', 'data-rail', 'closed')] },
  // The first-sign-in tour: a step's dialog in the window, its parts within it, focus on Next, Tab kept in it; the
  // second step (Next, by Enter) beside the Context button it rings.
  { name: 'tour', route: '/help', setup: tourAs({ 1920: 'Wes', 1440: 'Wyn', 780: 'Wim&roles=Viewer' }), widths: [1920, 1440, 780],
    rules: [...tourRules, ...tabWraps('.tide-tour', 6)] },
  { name: 'tour-dark', route: '/help', context: { theme: 'dark' }, setup: tourAs({ 1920: 'Xan', 1440: 'Xia', 780: 'Xu&roles=Viewer' }),
    widths: [1920, 1440, 780], rules: tourRules },
  { name: 'tour-step-two', route: '/help', setup: tourAs({ 1920: 'Yan', 1440: 'Yul', 780: 'Yve&roles=Viewer' }, 'Enter'),
    widths: [1920, 1440, 780], rules: [...tourRules, visible('#tide-rail-toggle.tide-tip-target')] },
  // What's new's Take the tour: What's new goes, and the tour's first step is up on Today, focus on Next.
  { name: 'tour-from-whatsnew', route: '/help', setup: tourFromWhatsNew({ 1920: 'Abe', 1440: 'Ada', 780: 'Ari&roles=Viewer' }), widths: [1920, 1440, 780],
    rules: [...tourRules, stillAt('/')] },
  // Escape from it: What's new's Take the tour is gone, so focus is on the page's heading.
  { name: 'tour-from-whatsnew-escape', route: '/help', setup: async page => { await tourFromWhatsNew({ 1440: 'Abi' })(page); await page.keyboard.press('Escape'); await idle(page); },
    widths: [1440], rules: [hidden('.tide-tour'), focusOn('main h1')] },
  // The tour's last step is on Help, ringing its Guides (or Take the tour, for someone with none).
  { name: 'tour-last-step', route: '/help', setup: tourAs({ 1920: 'Lia', 1440: 'Lou', 780: 'Lyn' }, ...Array(7).fill('Enter')), widths: [1920, 1440, 780],
    rules: [...tourRules, stillAt('/help'), visible('#help-guides.tide-tip-target, #help-take-tour.tide-tip-target')] },
  // A click on the page under a step (the sidebar's Actions) does nothing: the scrim takes it, and the tour stays put.
  { name: 'tour-click-page', route: '/help', setup: tourClick({ 1440: 'Zed' }, '.sidebar a[href="actions"]'), widths: [1440],
    rules: [visible('.tide-tour'), stillAt('/'), visible('.tide-tour-count:text("Step 1 of")'), focusOn('.tide-tour .btn-primary')] },
  // Escape ends the tour, and focus is on the page's heading.
  { name: 'tour-escape', route: '/help', setup: tourAs({ 1440: 'Zia' }, 'Escape'), widths: [1440],
    rules: [hidden('.tide-tour'), hidden('.tide-tour-scrim'), focusOn('main h1')] },
  // Started from Help and ended there: focus is back on Take the tour.
  { name: 'tour-from-help', route: '/help', setup: tourFromHelp({ 1440: 'Zoe' }), widths: [1440],
    rules: [hidden('.tide-tour'), stillAt('/help'), focusOn('#help-take-tour')] },
  // With the rail open over the page (]), the tip stays beside it, not over what it shows.
  { name: 'tip-rail-over', route: '/help', setup: tipAs({ 1440: 'Ola', 780: 'Oz' }, ']'), widths: [1440, 780],
    rules: [visible('.tide-tip'), visible('#tide-rail'), allBesides('.tide-tip', '#tide-rail'), inClient('.tide-tip')] },
  // Search's tip keeps off the page's own search box and its description.
  { name: 'tip-search', route: '/help', setup: tipOn('/search', { 1920: 'Pia', 1440: 'Pat', 780: 'Pip&roles=Viewer' }), widths: [1920, 1440, 780],
    rules: [visible('.tide-tip'), noOverlap('.tide-tip, form[role="search"], .tide-page-head .tide-sub'), inClient('.tide-tip')] },
  // Triage's tip is beside the reading pane, and not shown where there's none beside the list.
  { name: 'tip-triage', route: '/help', setup: tipOn('/triage', { 1920: 'Quin' }), widths: [1920],
    rules: [visible('.tide-tip'), visible('.tide-reading.tide-tip-target'), inClient('.tide-tip')] },
  { name: 'tip-triage-narrow', route: '/help', setup: tipOn('/triage', { 780: 'Quill' }), widths: [780], rules: [hidden('.tide-tip')] },
  // Got it from the keyboard: the tip goes, and focus is on the page's heading, not lost.
  { name: 'tip-got-it', route: '/help', setup: tipGotIt({ 1440: 'Rio' }), widths: [1440], rules: [hidden('.tide-tip'), focusOn('main h1')] },
  { name: 'slide-note-edit-focus', route: '/present/{today}', setup: noteStrip('edit'), widths: [1440], rules: [focusOn('#slide-note-text')] },
  { name: 'slide-note-escape', route: '/present/{today}', setup: noteStrip('escape'), widths: [1440],
    rules: [hidden('#slide-note-text'), visible('.tide-slide-note'), focusIn('.tide-slide-presenter')] },
  { name: 'slide-number-refused', route: '/present/{today}', setup: numberEntered('lots'), widths: [1440],
    rules: [attr('.tide-slide-presenter input', 'aria-invalid', 'true'), visible('.tide-slide-presenter-error'), focusOn('.tide-slide-presenter input')] },
  { name: 'slide-numbers-presenter', route: '/present/{today}', setup: slideWith('.tide-slide-presenter input'), widths: [1920, 1440, 1100, 900, 780],
    rules: [noSidewaysScroll, visible('.tide-slide-presenter input'), fits('.tide-slide-presenter'), inWindow('.tide-slide-presenter :is(button, input)'), slideFits] },
  // The title's Enter beside the numbers missing: the numbers slide, focus in the first missing number's box. Before the
  // pages that enter it.
  { name: 'present-rail-number-enter', route: '/present/{today}', setup: missingNumberEntered, widths: [1920, 1440],
    rules: [focusOn('#present-rail input[inputmode="decimal"]')] },
  // Still current, and a number entered with Enter, in the rail open over the slide (modal, the slide inert): the flag
  // goes and focus stays in the rail, on the note's Edit, or on the rail's close button once no number is left. After the strips'
  // other pages, as they change the demo's day (the note is kept, the number entered), and before the pages that change
  // the note or the numbers themselves.
  { name: 'slide-note-kept-focus', route: '/present/{today}', setup: noteStrip('keep'), widths: [1440], rules: [hidden('.tide-slide-presenter-flag'), focusOn('#present-rail .tide-slide-presenter button')] },
  { name: 'slide-number-saved', route: '/present/{today}', setup: numberEntered('7'), widths: [1440], rules: [hidden('.tide-slide-presenter'), focusOn('#present-rail .tide-present-rail-head button')] },
  // The wrap-up, the presenter's (no meeting): three lists, with the rail beside them where it's docked.
  { name: 'slide-wrap-presenter', route: '/present/{today}', setup: wrapUp('presenter'), widths: [1920, [1920, 480], 1440, [1440, 420], 1100, 780],
    rules: [...wrapUpRules, visible('.tide-wrap-list[data-wrap="owners"] .tide-wrap-item li')] },
  // The presenter's rail: docked beside the slide from 1500px (the slide's column narrows, and the slide still fits in
  // it), over the slide below that, as a dialog, once `]` opens it; shut there by default. It holds the presenter's
  // controls (the quick add among them), and the slide none of them. Wider letters.
  { name: 'present-rail-docked', route: '/present/{today}', setup: presenterRail(), widths: [[1920, 1000], [1600, 900], [1500, 900]],
    rules: [noSidewaysScroll, visible('#present-rail'), besides('#present-rail', '.tide-slide'), fits('#present-rail'), slideFits,
      visible('#present-rail .tide-quick-add'), hidden('.tide-slide .tide-quick-add'), attr('#present-rail-toggle', 'aria-expanded', 'true'),
      railPlaceholdersReadable, railSelectsWhole, fits('#present-rail .tide-present-rail-raise'), fits('#present-rail .tide-action-assign'),
      sameLook('#present-rail .tide-action-assign > input', '#present-rail .tide-action-assign > select', ['backgroundColor', 'color', 'fontSize']),
      buttonsNamed('Take it', 'Take A-')] },
  { name: 'present-rail-overlay', route: '/present/{today}', setup: presenterRail(true), widths: [1440, 1100, 780],
    rules: [noSidewaysScroll, visible('#present-rail'), attr('#present-rail', 'role', 'dialog'), attr('#present-rail', 'aria-modal', 'true'), fits('#present-rail'), slideFits,
      visible('#present-rail .tide-quick-add'), visible('.tide-present-scrim'), railSelectsWhole, tabStaysIn('#present-rail', 25), tabStaysIn('#present-rail', 6, 'Shift+Tab'), slideKeysHeld] },
  // A click on the scrim beside the rail open over the slide closes it, and focus goes back to Rail.
  { name: 'present-rail-scrim', route: '/present/{today}', setup: railScrimClicked, widths: [1440],
    rules: [hidden('#present-rail'), hidden('.tide-present-scrim'), focusOn('#present-rail-toggle')] },
  { name: 'present-rail-shut', route: '/present/{today}', setup: presenterRail(), widths: [1440, 780],
    rules: [hidden('#present-rail'), visible('#present-rail-toggle'), attr('#present-rail-toggle', 'aria-expanded', 'false'), hidden('.tide-slide .tide-quick-add'), slideFits] },
  { name: 'metrics-grid', route: '/metrics?chart=group:alert-queue,group:vulnerability-sla&view=grid', widths: [1920, 1000, 700],
    rules: [noSidewaysScroll, visible('.tide-mc-cell'), visible('.tide-chart-band-warn'), oneRow('.tide-mc-cell-head'), [1800, widerThan('.content', 1400)]] },
  // Add on "Not on a brief yet", from the keyboard: the article leaves the card and focus stays in it, on the next
  // article's Add. Last, as it changes today's brief.
  // A colleague takes the row that has focus in the rail's queue: focus moves to the next row's Take it. Last too: it
  // puts people on a team.
  { name: 'today-queue-live', route: '/', setup: colleagueTakesFocusedRow, widths: [1920],
    rules: [focusIn('#tide-rail section[aria-labelledby="teams-queue-h"] .tide-queue-row')] },
  // A colleague saves what the analyst is typing (not yet left the field): the analyst's text stays, and the page says
  // so. Then a toast while the rail is docked sits beside it, not over its buttons. Late: these save the brief.
  { name: 'today-typing-intro', route: '/', setup: colleagueSavesWhileTyping('intro'), widths: [1920], rules: [keptWhatsTyped('intro', '#tide-rail section[aria-labelledby="meeting-h"]')] },
  { name: 'today-typing-note', route: '/', setup: colleagueSavesWhileTyping('team-note'), widths: [1920], rules: [keptWhatsTyped('team-note', '#main-content .tide-page-head')] },
  { name: 'today-toast', route: '/', setup: toastBesideRail, widths: [1920, 1600], rules: [allBesides('.tide-toast', '#tide-rail')] },
  { name: 'today-rail-add', route: '/', setup: addFromRail, widths: [1920],
    rules: [focusIn('section[aria-labelledby="recent-h"] li')] },
  // Closing a card from its Move menu, or by dragging it while it had focus, keeps the person's place in the lane.
  // Last, as each closes one of the demo's actions.
  // A clicked decision in Triage's bar moves focus on to the next selected row, as its key does. Late, as each sets
  // one of the demo's new items aside.
  { name: 'triage-click-shortlist', route: '/triage', setup: selectReport, widths: [1440], rules: [clickFocusesNextRow('Shortlist')] },
  { name: 'triage-click-dismiss', route: '/triage', setup: selectReport, widths: [1440], rules: [clickFocusesNextRow('Dismiss')] },
  // Mark checked in the rail: the button goes (it's checked today), and focus goes to the Checked fact it changed, not to
  // the page's body. Late, as it marks the demo's first product checked.
  { name: 'stack-mark-checked', route: '/stack', setup: markCheckedFromRail, widths: [1920], rules: [focusIn('#tide-rail [data-rail-checked]')] },
  { name: 'actions-board-close-menu', route: '/actions?view=open&layout=board', widths: [[1440, 900]], rules: [closeKeepsPlace('menu')] },
  { name: 'actions-board-close-drag', route: '/actions?view=open&layout=board', widths: [[1440, 900]], rules: [closeKeepsPlace('drag')] },
  // Last: it adds a query to the demo's sweep.
  { name: 'action-hunt-write', route: '/actions/{huntAction}', widths: [[1440, 900]], rules: [writtenQueryAddsOnFirstClick] },
  { name: 'actions-board-close-click-drag', route: '/actions?view=open&layout=board', widths: [[1600, 1000]], rules: [closeKeepsPlace('click-drag')] },
  { name: 'actions-board-close-address', route: '/actions?view=open&layout=board', widths: [[1600, 1000]], rules: [addressFollowsAfterClose] },
  // Last: it changes the demo analyst's default tab and layout.
  { name: 'actions-default-layout', route: '/actions?view=open&layout=list', setup: analystOnATeam, widths: [[1600, 1000]], rules: [defaultKeepsLayout] },
  // A live meeting of eight, with none, two or five hands up, seen by the presenter and by someone following: who is
  // here and whose hands are up sit in the top bar. By construction, for any number of people and hands: the dots stop
  // at five and a "+N"; how many are here is always whole; the hands show as their count, then as many whole names as
  // fit; and the tools never leave the window (the top bar takes a second row, at most, instead). The footer no longer
  // has them; the slide still fits between the two. Wider letters. Last of all: the meeting stays live after.
  ...['presenter', 'follower'].flatMap(as => [0, 2, 5].map(hands => (
    { name: `present-people-${as}-${hands}`, route: '/present/{today}', setup: meetingOf(as, hands), widths: [1920, 1440, 1100, 1000, 901, 900, 780],
      rules: [noSidewaysScroll, fits('.tide-present-top'), fits('.tide-present-title'), fits('.tide-present-people'), ...(hands ? [] : [titleTakesRoom]),
        inWindow('.tide-present-top :is(button, a, select)'), rowsAtMost('.tide-present-top > *', 2),
        visible('.tide-present-top .tide-present-people .tide-presence-dot'), shownCount('.tide-present-people .tide-presence-dot', 6), countWhole,
        ...(hands ? [visible('.tide-hands-n'), namesWhole] : [hidden('.tide-hands-btn')]),
        hidden('.tide-present-bottom .tide-presence'), hidden('.tide-present-bottom .tide-hands-btn'),
        ...(as === 'follower' ? [hidden('#present-rail'), hidden('#present-rail-toggle')] : []),
        inOrder('.tide-present-top', '.tide-slide', '.tide-present-bottom'), slideFits] }))),
  // The title takes the room there is before it's cut: a
  // meeting of three, no hands, as presenter and follower.
  ...['presenter', 'follower'].map(as => (
    { name: `present-title-room-${as}`, route: '/present/{today}', setup: meetingOf(as, 0, 3), widths: [1920, 1440],
      rules: [titleTakesRoom, fits('.tide-present-top')] })),
  // The list of hands closes as the shared popouts do: Escape (focus back on its count), a press outside; and it shuts
  // when the last hand goes down, never opening again by itself.
  { name: 'present-hands-escape', route: '/present/{today}', setup: handsListEscaped, widths: [1440], rules: [hidden('#present-hands'), focusOn('.tide-hands-btn')] },
  { name: 'present-hands-outside', route: '/present/{today}', setup: handsListPressedOutside, widths: [1440], rules: [hidden('#present-hands')] },
  { name: 'present-hands-emptied', route: '/present/{today}', setup: handsListAfterEmpty, widths: [1440], rules: [hidden('#present-hands'), attr('.tide-hands-btn', 'aria-expanded', 'false')] },
  // The wrap-up as someone following sees it: the same three lists, and none of the presenter's rail. Before the pages
  // that leave an action open in today's meeting.
  { name: 'slide-wrap-follower', route: '/present/{today}', setup: wrapUp('follower'), widths: [1920, 1440, 1100, 780],
    rules: [...wrapUpRules, hidden('#present-rail'), hidden('#present-rail-toggle')] },
  // The presenter's list of hands, opened from the top bar: each hand in order with its Lower, and Clear all, in the window.
  { name: 'present-hands-list', route: '/present/{today}', setup: handsListOpen, widths: [1920, 1100, 901, 780],
    rules: [inWindow('#present-hands :is(button, li)'), shownCount('#present-hands li', 5), fits('#present-hands'), hidden('#present-hands button')] },
  // An action opened beside the slide (Org:ActionBesideSlide, on for this run): from 1200px a column between the slide
  // and the presenter's rail, as wide for a follower as for the presenter, whose slide, fit and scroll the follower's
  // match; with the presenter's rail docked, slide, action and rail side by side; narrower, over the slide. Last: they
  // leave an action open in today's meeting.
  { name: 'present-beside-follower', route: '/present/{today}', setup: actionBeside('follower'), widths: [[1920, 1000], [1600, 900], 1440, 1200],
    rules: [noSidewaysScroll, visible(BESIDE), besides(BESIDE, '#present-root > .tide-slide'), hidden('#present-rail'), fits(BESIDE),
      inWindow(`${BESIDE} button`), besideFactsRoomy, besideStatusFilled, contrastAtLeast(`${BESIDE} .tide-fact :is(dt, dd)`, 4.5), oneLine(`${BESIDE} .tide-present-action-head .tide-present-help`), notSqueezed(`${BESIDE} .tide-present-action-head .btn`), slideFits, matchesPresenter] },
  { name: 'present-beside-presenter', route: '/present/{today}', setup: actionBeside('presenter'), widths: [[1920, 1000], [1600, 900], [1500, 900]],
    rules: [noSidewaysScroll, visible(BESIDE), visible('#present-rail.is-docked'), besides(BESIDE, '#present-root > .tide-slide'),
      besides('#present-rail', BESIDE), fits(BESIDE), fits('#present-rail'), inWindow(`${BESIDE} button`), besideFactsRoomy, besideStatusFilled,
      oneLine(`${BESIDE} .tide-present-action-head .tide-present-help`), notSqueezed(`${BESIDE} .tide-present-action-head .btn`), slideFits, widerThan('#present-root > .tide-slide', 559)] },
  { name: 'present-beside-narrow', route: '/present/{today}', setup: actionBeside('presenter'), widths: [1100, 780],
    rules: [noSidewaysScroll, actionOver, slideFits] },
  // The rail's Add over a team's limit, the question that asks why cancelled: focus is back on Add, not on the page.
  // Last of all: it leaves the first team at its limit, which would ask on every later page that adds to it.
  { name: 'present-rail-over-limit-cancel', route: '/present/{today}', setup: overLimitCancelled, widths: [1440],
    rules: [hidden('.tide-modal'), focusOn('#present-rail .tide-quick-add .btn-primary')] },
  // A rehearsal asked for while today's meeting is live: refused, with no slides, rail or guide, and the page not in the
  // meeting. Last: it leaves the meeting live.
  { name: 'rehearsal-while-live', route: '/help', setup: rehearsalWhileLive, widths: [1440],
    rules: [visible('h2:text("The meeting is live")'), hidden('.tide-tour'), hidden('#present-rail'), hidden('.tide-slide-body .tide-agenda'), hidden('.tide-rehearsal')] },
];

// --- the run -----------------------------------------------------------------------------------------------------------

const wanted = pick(shard(pages, process.env.SHARD), 'page');

/**
 * `SHARD=i/n` (1-based) runs one of n slices of the pages, in their order, so n runs side by side (each on its own fresh
 * demo) check them all. The pages before CHANGES_THE_DEMO change nothing a later page reads, so they're cut into slices
 * of about as many widths each; the pages from it on stay together, in order, at the end of the last slice. With no
 * SHARD, every page, in one run.
 */
function shard(list, spec) {
  const changesAt = list.indexOf(CHANGES_THE_DEMO);
  if (changesAt < 0) throw new Error('The pages have lost their CHANGES_THE_DEMO marker.');
  const all = list.filter(p => p !== CHANGES_THE_DEMO);
  if (!spec) return all;
  const [i, n] = spec.split('/').map(Number);
  if (!(Number.isInteger(i) && Number.isInteger(n) && i >= 1 && i <= n)) throw new Error(`SHARD=${spec} isn't i/n with 1 <= i <= n.`);
  const head = list.slice(0, changesAt), tail = list.slice(changesAt + 1);
  // A width of a Present page (a meeting to start, people to seat) takes about twice as long as one elsewhere.
  const weight = ps => ps.reduce((sum, p) => sum + p.widths.length * (p.route.startsWith('/present') ? 2 : 1), 0);
  // Each slice about as long, the last with the tail in it: when the tail is a slice's worth or more, the first n-1 share
  // the head between them.
  const share = Math.min((weight(head) + weight(tail)) / n, weight(head) / Math.max(1, n - 1));
  // The first n-1 slices take the head in turn until each has its share; the last has what's left of it, then the tail.
  const slices = [[]];
  for (const p of head) {
    if (slices.length < n && weight(slices.at(-1)) >= share) slices.push([]);
    slices.at(-1).push(p);
  }
  while (slices.length < n) slices.push([]);
  slices[n - 1].push(...tail);
  return slices[i - 1];
}

let server;
try {
  // Enough more new items for Triage to have a second page (the paging keys' rules), a slide with a dozen indicators
  // and CVEs (the slide's fit), and a meeting's action beside the slide (only the present-beside pages open one). Polling
  // on, with feeds the scheduled pass leaves alone (never fetched) last pulled the day before, and a cutoff at midnight,
  // so Today says the feeds aren't pulled whenever the run is on a working day.
  server = await startTide(['--Demo:TriageFiller=55', '--Demo:SlideFiller=true', '--Org:ActionBesideSlide=true',
    '--Demo:StaleFeeds=true', '--Intake:Enabled=true', '--Intake:PulledBy=00:00']);
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
      if (p.when && !p.when(demo)) {
        console.log(`  ${p.name}: not checked today (what it needs isn't shown on this day)`);
        continue;
      }
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
