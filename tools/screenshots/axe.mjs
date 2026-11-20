// axe-core's accessibility scan, for layout.mjs: report only. Each page is scanned once, at its widest width, after its
// layout rules; what axe finds is written to .axe/ and printed, and never fails the run (README.md, "Accessibility scan").

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

/** WCAG 2.0 and 2.1, levels A and AA. */
export const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

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
  // Passed as a string, which Playwright runs through the browser's debugging protocol, outside the page's policy; an
  // eval() in the page would be refused like a script tag.
  if (!(await page.evaluate(() => !!window.axe))) await page.evaluate(source);
  return page.evaluate(async tags => {
    const r = await window.axe.run(document, { runOnly: { type: 'tag', values: tags }, resultTypes: ['violations'] });
    return r.violations.map(v => ({
      id: v.id, impact: v.impact, help: v.help, helpUrl: v.helpUrl,
      targets: v.nodes.map(n => n.target.map(t => Array.isArray(t) ? t.join(' >>> ') : t).join(' ')),
    }));
  }, TAGS);
}

/** Collects each page's scan and writes the slice's report: .axe/axe-<label>.json and .md. */
export class AxeReport {
  constructor(label) {
    this.label = label;
    this.pages = [];
    this.seconds = 0;
  }

  add(name, route, width, height, violations, seconds) {
    this.pages.push({ page: name, route, width, height, violations });
    this.seconds += seconds;
    const nodes = violations.reduce((n, v) => n + v.targets.length, 0);
    console.log(`  ${name} at ${width}x${height}: axe, ${violations.length} rule${violations.length === 1 ? '' : 's'} broken on ${nodes} element${nodes === 1 ? '' : 's'} (report only)`);
  }

  failed(name, width, height, message) {
    this.pages.push({ page: name, width, height, error: message, violations: [] });
    console.log(`  ${name} at ${width}x${height}: axe couldn't run (${message})`);
  }

  /** Each rule broken, with how many elements on how many pages, most elements first. */
  byRule() {
    const rules = new Map();
    for (const p of this.pages) for (const v of p.violations) {
      const r = rules.get(v.id) ?? { id: v.id, impact: v.impact, help: v.help, helpUrl: v.helpUrl, elements: 0, pages: [] };
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
    writeFileSync(`${file}.json`, JSON.stringify({ slice: this.label, axe: version, tags: TAGS, seconds: Math.round(this.seconds), rules, pages: this.pages }, null, 2));

    const elements = rules.reduce((n, r) => n + r.elements, 0);
    const lines = [
      `### Accessibility scan, slice ${this.label.replace('-of-', '/')} (axe-core ${version}, report only)`,
      '',
      `${scanned.length} page${scanned.length === 1 ? '' : 's'} scanned at their widest width against WCAG 2.1 A and AA, in ${Math.round(this.seconds)}s: ` +
        (rules.length ? `${rules.length} rule${rules.length === 1 ? '' : 's'} broken on ${elements} element${elements === 1 ? '' : 's'}, on ` +
          `${scanned.filter(p => p.violations.length).length} page${scanned.filter(p => p.violations.length).length === 1 ? '' : 's'}.` : 'nothing broken.'),
      '',
    ];
    if (rules.length) {
      lines.push('| Rule | Impact | Elements | Pages | First pages |', '| --- | --- | ---: | ---: | --- |');
      for (const r of rules) {
        const pages = [...new Set(r.pages)];
        lines.push(`| [${r.id}](${r.helpUrl}) | ${r.impact ?? ''} | ${r.elements} | ${pages.length} | ${pages.slice(0, 3).join(', ')}${pages.length > 3 ? ', ...' : ''} |`);
      }
      lines.push('');
    }
    const errors = this.pages.filter(p => p.error);
    if (errors.length) lines.push(`Not scanned (axe couldn't run): ${errors.map(p => p.page).join(', ')}.`, '');
    lines.push(`Each element's selector is in \`axe-${this.label}.json\`, in the run's axe artifact.`, '');
    writeFileSync(`${file}.md`, lines.join('\n'));
    console.log(`\naxe: ${scanned.length} pages scanned in ${Math.round(this.seconds)}s, ${rules.length} rules broken on ${elements} elements (report only): ${path.relative(process.cwd(), file)}.md`);
  }
}
