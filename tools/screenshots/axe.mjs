// axe-core's accessibility scan, for layout.mjs. Each page is scanned once, at its widest width, after its layout rules.
// A WCAG A or AA rule broken, and not in axe-baseline.json, fails the run; best-practice rules are reported only. What
// axe finds is written to .axe/ and printed (README.md, "Accessibility scan").

import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
// The locked axe-core's own build, run in the page by Playwright rather than added as a script tag, which TIDE's
// Content-Security-Policy would refuse.
const source = readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');
const version = JSON.parse(readFileSync(require.resolve('axe-core/package.json'), 'utf8')).version;

/** WCAG 2.0, 2.1 and 2.2, levels A and AA: a rule with one of these tags that a page breaks fails the run. */
export const GATE_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];
/** Reported, never failing the run (the plan promotes them to the gate once they're clean). */
export const REPORT_TAGS = ['best-practice'];
const TAGS = [...GATE_TAGS, ...REPORT_TAGS];

/** What's known and allowed to break, each with why: `{ rule, page, target?, reason }`. `page` may be `*` (the shell,
 *  on every page); `target`, when given, is part of the element's selector. Empty is the aim: fix rather than add. */
const baseline = JSON.parse(readFileSync(path.join(here, 'axe-baseline.json'), 'utf8'));

/** `AXE=0` turns the scan off (CI scans at one letter spacing only: the other would find the same). */
export const axeOn = !/^(0|false|no)$/i.test(process.env.AXE ?? '');

/** The size a page is scanned at: its widest, the taller of two as wide; the first of equals. */
export function widest(widths) {
  const dims = widths.map(s => Array.isArray(s) ? s : [s, 900]);
  return widths[dims.reduce((best, d, i) => (d[0] > dims[best][0] || (d[0] === dims[best][0] && d[1] > dims[best][1]) ? i : best), 0)];
}

/** Runs axe on the page as it is now (the top document; frames aren't scanned). Returns the rules it breaks, each with the
 *  selector of every element that breaks it. */
export async function scan(page) {
  // Bounded, so a scan that hangs can't hold the slice to its job's time limit and fail it: the page is skipped instead.
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(`timed out after ${SCAN_SECONDS}s`)), SCAN_SECONDS * 1000);
  });
  try {
    return await Promise.race([run(page), timeout]);
  } finally {
    clearTimeout(timer);
  }
}

/** The longest one page's scan may take; a page's scan takes well under a second. */
const SCAN_SECONDS = 60;

async function run(page) {
  // Passed as a string, which Playwright runs through the browser's debugging protocol, outside the page's policy; an
  // eval() in the page would be refused like a script tag.
  if (!(await page.evaluate(() => !!window.axe))) await page.evaluate(source);
  return page.evaluate(async tags => {
    const r = await window.axe.run(document, { runOnly: { type: 'tag', values: tags }, resultTypes: ['violations'] });
    return r.violations.map(v => ({
      id: v.id, impact: v.impact, help: v.help, helpUrl: v.helpUrl, tags: v.tags,
      targets: v.nodes.map(n => n.target.map(t => Array.isArray(t) ? t.join(' >>> ') : t).join(' ')),
      // What axe says is wrong with each, in the same order: which check failed, and on what.
      why: v.nodes.map(n => n.failureSummary),
    }));
  }, TAGS);
}

/** Whether breaking this rule fails the run (a WCAG A or AA rule) rather than being reported. */
export const gates = v => v.tags.some(t => GATE_TAGS.includes(t));

/** The baseline entry that allows this element to break this rule on this page, if any. */
function allowed(page, rule, target) {
  return baseline.find(b => b.rule === rule && (b.page === page || b.page === '*') && (!b.target || target.includes(b.target)));
}

/** Collects each page's scan and writes the slice's report: .axe/axe-<label>.json and .md. */
export class AxeReport {
  constructor(label) {
    this.label = label;
    this.pages = [];
    this.seconds = 0;
    this.used = new Set();
  }

  /** Records a page's scan, and returns what fails the run: each element that breaks a WCAG rule, unless the baseline
   *  allows it. */
  add(name, route, width, height, violations, seconds) {
    this.pages.push({ page: name, route, width, height, violations });
    this.seconds += seconds;
    const failures = [];
    for (const v of violations) {
      v.gate = gates(v);
      for (const target of v.targets) {
        const entry = allowed(name, v.id, target);
        if (entry) this.used.add(entry);
        else if (v.gate) failures.push(`axe: ${v.id} (${v.impact}: ${v.help}) on ${target}`);
      }
    }
    const nodes = violations.reduce((n, v) => n + v.targets.length, 0);
    const reported = violations.filter(v => !v.gate).length;
    console.log(`  ${name} at ${width}x${height}: axe, ` + (violations.length
      ? `${violations.length} rule${violations.length === 1 ? '' : 's'} broken on ${nodes} element${nodes === 1 ? '' : 's'}` +
        (failures.length ? `, ${failures.length} failing` : '') + (reported ? `, ${reported} best practice (report only)` : '')
      : 'nothing broken'));
    return failures;
  }

  /** Baseline entries for a page this slice scanned that no longer occur there: fixed, so they should go. Entries for
   *  every page (`*`) can only be judged over every slice, so they aren't listed. */
  unused() {
    const scanned = new Set(this.pages.filter(p => !p.error).map(p => p.page));
    return baseline.filter(b => scanned.has(b.page) && !this.used.has(b));
  }

  failed(name, width, height, message) {
    this.pages.push({ page: name, width, height, error: message, violations: [] });
    console.log(`  ${name} at ${width}x${height}: axe couldn't run (${message})`);
  }

  /** Each rule broken, with how many elements on how many pages, most elements first. */
  byRule() {
    const rules = new Map();
    for (const p of this.pages) for (const v of p.violations) {
      const r = rules.get(v.id) ?? { id: v.id, impact: v.impact, help: v.help, helpUrl: v.helpUrl, gate: gates(v), elements: 0, pages: [] };
      r.elements += v.targets.length;
      r.pages.push(p.page);
      rules.set(v.id, r);
    }
    return [...rules.values()].sort((a, b) => b.elements - a.elements || b.pages.length - a.pages.length || a.id.localeCompare(b.id));
  }

  write() {
    const dir = path.join(here, '.axe');
    mkdirSync(dir, { recursive: true });
    const file = path.join(dir, `axe-${this.label}`);
    const rules = this.byRule();
    const scanned = this.pages.filter(p => !p.error);
    const unused = this.unused();
    writeFileSync(`${file}.json`, JSON.stringify({ slice: this.label, axe: version, gateTags: GATE_TAGS, reportTags: REPORT_TAGS, seconds: Math.round(this.seconds), rules, unusedBaseline: unused, pages: this.pages }, null, 2));

    const elements = rules.reduce((n, r) => n + r.elements, 0);
    const lines = [
      `### Accessibility scan, slice ${this.label.replace('-of-', '/')} (axe-core ${version})`,
      '',
      `${scanned.length} page${scanned.length === 1 ? '' : 's'} scanned at their widest width against WCAG 2.2 A and AA (a gate) and best practice (report only), in ${Math.round(this.seconds)}s: ` +
        (rules.length ? `${rules.length} rule${rules.length === 1 ? '' : 's'} broken on ${elements} element${elements === 1 ? '' : 's'}, on ` +
          `${scanned.filter(p => p.violations.length).length} page${scanned.filter(p => p.violations.length).length === 1 ? '' : 's'}.` : 'nothing broken.'),
      '',
    ];
    if (rules.length) {
      lines.push('| Rule | Kind | Impact | Elements | Pages | First pages |', '| --- | --- | --- | ---: | ---: | --- |');
      for (const r of rules) {
        const pages = [...new Set(r.pages)];
        lines.push(`| [${r.id}](${r.helpUrl}) | ${r.gate ? 'WCAG (gate)' : 'best practice'} | ${r.impact ?? ''} | ${r.elements} | ${pages.length} | ${pages.slice(0, 3).join(', ')}${pages.length > 3 ? ', ...' : ''} |`);
      }
      lines.push('', 'A WCAG rule fails the run unless `axe-baseline.json` allows that element on that page.', '');
    }
    if (unused.length) {
      const named = unused.map(b => `${b.rule} on ${b.page}${b.target ? ` (${b.target})` : ''}`);
      lines.push(`In \`axe-baseline.json\` but no longer found, so they can go: ${named.join('; ')}.`, '');
      for (const n of named) console.log(`::notice::axe-baseline.json: ${n} no longer occurs; remove the entry`);
    }
    const errors = this.pages.filter(p => p.error);
    if (errors.length) lines.push(`Not scanned (axe couldn't run): ${errors.map(p => p.page).join(', ')}.`, '');
    lines.push(`Each element's selector is in \`axe-${this.label}.json\`, in the run's axe artifact.`, '');
    writeFileSync(`${file}.md`, lines.join('\n'));
    console.log(`\naxe: ${scanned.length} pages scanned in ${Math.round(this.seconds)}s, ${rules.length} rules broken on ${elements} elements: ${path.relative(process.cwd(), file)}.md`);
  }
}
