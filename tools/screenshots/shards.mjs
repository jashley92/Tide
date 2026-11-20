// Cuts layout.mjs's pages into slices that take about as long as each other, by what each page took in CI
// (layout-weights.json, refreshed with layout-weights.mjs). Plain functions over the pages list, with no browser, so the
// split can be printed and checked without starting anything.

import { readFileSync } from 'node:fs';

const weights = JSON.parse(readFileSync(new URL('./layout-weights.json', import.meta.url), 'utf8'));

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

/**
 * The n slices of `list` (the pages, with the `marker` where those that change the demo begin). The pages before the
 * marker change nothing a later page reads, so they're placed by cost: longest first, each on the slice with least
 * so far (the earlier slice on a tie, the earlier page between equal costs), which is deterministic and moves few pages
 * when one is added. The pages from the marker on stay together, in order, at the end of the last slice, which counts
 * them before the rest are placed. Each slice keeps the pages' own order.
 */
export function slices(list, n, marker) {
  const at = list.indexOf(marker);
  if (at < 0) throw new Error('The pages have lost their CHANGES_THE_DEMO marker.');
  const head = list.slice(0, at), tail = list.slice(at + 1);
  const load = Array(n).fill(0);
  load[n - 1] = tail.reduce((sum, p) => sum + cost(p), 0);
  const slot = new Map();
  const longestFirst = head.map((p, i) => ({ p, i, c: cost(p) })).sort((a, b) => b.c - a.c || a.i - b.i);
  for (const { p, c } of longestFirst) {
    const s = load.indexOf(Math.min(...load));
    load[s] += c;
    slot.set(p, s);
  }
  const out = Array.from({ length: n }, (_, s) => head.filter(p => slot.get(p) === s));
  out[n - 1].push(...tail);
  return out;
}

/** Throws unless the slices hold every page exactly once (the marker aside). */
export function checkSlices(list, cut, marker) {
  const all = list.filter(p => p !== marker);
  const seen = new Map();
  cut.forEach((slice, s) => slice.forEach(p => seen.set(p, [...(seen.get(p) ?? []), s + 1])));
  const twice = [...seen].filter(([, ss]) => ss.length > 1).map(([p, ss]) => `${p.name} (slices ${ss.join(', ')})`);
  const missing = all.filter(p => !seen.has(p)).map(p => p.name);
  const strange = [...seen.keys()].filter(p => !all.includes(p)).map(p => p.name ?? String(p));
  const names = all.map(p => p.name), dupNames = names.filter((x, i) => names.indexOf(x) !== i);
  const wrong = [
    twice.length && `in more than one slice: ${twice.join(', ')}`,
    missing.length && `in no slice: ${missing.join(', ')}`,
    strange.length && `in a slice but not a page: ${strange.join(', ')}`,
    dupNames.length && `two pages share a name: ${[...new Set(dupNames)].join(', ')}`,
  ].filter(Boolean);
  if (wrong.length) throw new Error(`The slices don't hold every page once. ${wrong.join('; ')}.`);
}

/**
 * `spec` is `i/n` (1-based): slice i of n, checked to hold every page once across the n. With no spec, every page.
 */
export function shard(list, spec, marker) {
  if (!spec) return list.filter(p => p !== marker);
  const [i, n] = spec.split('/').map(Number);
  if (!(Number.isInteger(i) && Number.isInteger(n) && i >= 1 && i <= n)) throw new Error(`SHARD=${spec} isn't i/n with 1 <= i <= n.`);
  const cut = slices(list, n, marker);
  checkSlices(list, cut, marker);
  return cut[i - 1];
}

/** One line per slice: its pages and the seconds the weights expect it to take. */
export function describe(list, n, marker) {
  const cut = slices(list, n, marker);
  checkSlices(list, cut, marker);
  const untimed = list.filter(p => p !== marker && typeof weights.pages[p.name] !== 'number').map(p => p.name);
  return [
    ...cut.map((s, k) => `  ${k + 1}/${n}: ${s.length} pages, about ${Math.round(s.reduce((sum, p) => sum + cost(p), 0))}s`),
    ...(untimed.length ? [`  not timed yet (counted by their widths): ${untimed.join(', ')}`] : []),
  ];
}
