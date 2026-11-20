// Cuts layout.mjs's pages into slices that take about as long as each other, by what each page took in CI
// (layout-weights.json, refreshed with layout-weights.mjs). Plain functions over the pages list, with no browser, so the
// split can be printed and checked without starting anything.

import { readFileSync } from 'node:fs';

const weights = JSON.parse(readFileSync(new URL('./layout-weights.json', import.meta.url), 'utf8'));

/**
 * A mark in the pages list: the pages after it, up to the next mark, change the demo, and are a chain that runs whole,
 * in order, after its slice's other pages. Every slice starts on its own fresh demo, so a chain only needs what its own
 * pages did before it, never another chain's; it can't break a chain to run where another chain hasn't. `what` names
 * the chain in PLAN=1's output.
 */
export const changesTheDemo = what => ({ changesTheDemo: what });

const isMark = e => typeof e === 'object' && e !== null && 'changesTheDemo' in e;

/**
 * How many seconds a page is expected to take: its measured time, or, for a page CI hasn't timed yet (a new one, or one
 * renamed), its widths at the measured median per width, a Present page's at Present's (a meeting to start, people to
 * seat). A new page never breaks the split; it's only placed less exactly until the weights are refreshed.
 */
export function cost(page) {
  const measured = weights.pages[page.name];
  if (typeof measured === 'number') return measured;
  return page.widths.length * (page.route.startsWith('/present') ? weights.perPresentWidth : weights.perWidth);
}

const total = pages => pages.reduce((sum, p) => sum + cost(p), 0);

/** The pages before the first mark (they change nothing a later page reads), and the chains: each mark's name and the
 *  pages after it, up to the next mark. */
export function chains(list) {
  const at = list.findIndex(isMark);
  if (at < 0) throw new Error('The pages have lost their changesTheDemo marks.');
  const out = [];
  for (const e of list.slice(at)) {
    if (isMark(e)) out.push({ what: e.changesTheDemo, pages: [] });
    else out.at(-1).pages.push(e);
  }
  return { head: list.slice(0, at), chains: out };
}

/**
 * The n slices of `list`. Each chain and each page before the first mark is placed by cost, longest first, on the slice
 * with least so far (the earlier slice on a tie, the earlier in the list between equal costs), which is deterministic
 * and moves little when a page is added. A slice holds its pages in the list's own order, so its chains come after its
 * other pages, each chain whole and in order.
 */
export function slices(list, n) {
  const { head, chains: tail } = chains(list);
  const items = [...head.map(p => ({ pages: [p] })), ...tail.filter(c => c.pages.length)]
    .map(item => ({ ...item, at: list.indexOf(item.pages[0]), c: total(item.pages) }))
    .sort((a, b) => b.c - a.c || a.at - b.at);
  const load = Array(n).fill(0);
  const slot = new Map();
  for (const { pages, c } of items) {
    const s = load.indexOf(Math.min(...load));
    load[s] += c;
    for (const p of pages) slot.set(p, s);
  }
  return Array.from({ length: n }, (_, s) => list.filter(p => !isMark(p) && slot.get(p) === s));
}

/**
 * Throws unless the slices hold every page exactly once (the marks aside), each slice in the list's order with its
 * chains' pages after its others, and each chain whole in one slice.
 */
export function checkSlices(list, cut) {
  const all = list.filter(p => !isMark(p));
  const seen = new Map();
  cut.forEach((slice, s) => slice.forEach(p => seen.set(p, [...(seen.get(p) ?? []), s + 1])));
  const twice = [...seen].filter(([, ss]) => ss.length > 1).map(([p, ss]) => `${p.name} (slices ${ss.join(', ')})`);
  const missing = all.filter(p => !seen.has(p)).map(p => p.name);
  const strange = [...seen.keys()].filter(p => !all.includes(p)).map(p => p.name ?? String(p));
  const names = all.map(p => p.name), dupNames = names.filter((x, i) => names.indexOf(x) !== i);
  const { head, chains: tail } = chains(list);
  const split = tail.filter(c => new Set(c.pages.map(p => seen.get(p)?.[0])).size > 1).map(c => c.what);
  const late = [], unordered = [];
  cut.forEach((slice, s) => {
    const firstTail = slice.findIndex(p => !head.includes(p));
    const after = firstTail < 0 ? [] : slice.slice(firstTail).filter(p => head.includes(p));
    if (after.length) late.push(`${after.map(p => p.name).join(', ')} (slice ${s + 1})`);
    const at = slice.map(p => list.indexOf(p));
    const i = at.findIndex((x, k) => k > 0 && x < at[k - 1]);
    if (i > 0) unordered.push(`${slice[i - 1].name} before ${slice[i].name}, which the list has first (slice ${s + 1})`);
  });
  const wrong = [
    twice.length && `in more than one slice: ${twice.join(', ')}`,
    missing.length && `in no slice: ${missing.join(', ')}`,
    strange.length && `in a slice but not a page: ${strange.join(', ')}`,
    dupNames.length && `two pages share a name: ${[...new Set(dupNames)].join(', ')}`,
    split.length && `a chain split across slices: ${split.join('; ')}`,
    late.length && `after a chain that changes the demo, though they read it: ${late.join('; ')}`,
    unordered.length && `out of the list's order: ${unordered.join('; ')}`,
  ].filter(Boolean);
  if (wrong.length) throw new Error(`The slices don't hold every page once, in order. ${wrong.join('; ')}.`);
}

/**
 * `spec` is `i/n` (1-based): slice i of n, checked as checkSlices does across the n. With no spec, every page.
 */
export function shard(list, spec) {
  if (!spec) return list.filter(p => !isMark(p));
  const [i, n] = spec.split('/').map(Number);
  if (!(Number.isInteger(i) && Number.isInteger(n) && i >= 1 && i <= n)) throw new Error(`SHARD=${spec} isn't i/n with 1 <= i <= n.`);
  const cut = slices(list, n);
  checkSlices(list, cut);
  return cut[i - 1];
}

/** One line per slice: its pages, the seconds the weights expect it to take, and the chains it runs. */
export function describe(list, n) {
  const cut = slices(list, n);
  checkSlices(list, cut);
  const { chains: tail } = chains(list);
  const untimed = list.filter(p => !isMark(p) && typeof weights.pages[p.name] !== 'number').map(p => p.name);
  return [
    ...cut.map((s, k) => {
      const runs = tail.filter(c => c.pages.length && s.includes(c.pages[0])).map(c => `${c.what} (${c.pages.length}, ${Math.round(total(c.pages))}s)`);
      return `  ${k + 1}/${n}: ${s.length} pages, about ${Math.round(total(s))}s${runs.length ? `; then ${runs.join(', ')}` : ''}`;
    }),
    ...(untimed.length ? [`  not timed yet (counted by their widths): ${untimed.join(', ')}`] : []),
  ];
}
