#!/usr/bin/env node
// Cuts src/Tide.Web/wwwroot/app.css into ordered parts under wwwroot/css/, one per area, so two UI changes to different
// areas stop touching the same file. A pure move: the parts, joined in file-name order, are app.css byte for byte, and
// App.razor loads them in that order, so the cascade is the one the single file had.
//
// The cuts are found by content, not by line number: each part after the first starts at its area's section heading
// (a "/* ---- ... ---- */" comment at the top level of the file), so the split still lands in the right places after
// other changes have added or moved rules. A heading that's missing, found twice, found out of order, or found inside a
// rule or an @media block stops the run without writing anything.
//
// Usage (from the repository root):
//   node tools/css/split-app-css.mjs                       report where it would cut, and check; writes nothing
//   node tools/css/split-app-css.mjs --write [--version V] write the parts, App.razor's <link>s (?v=V, default
//                                                          <today>-split) and tools/css/split-manifest.json, and
//                                                          delete app.css
//
// It runs on app.css, so it runs before the split, not after: to run it again on a newer main, see tools/css/README.md.
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const root = join(repo, 'src', 'Tide.Web', 'wwwroot');
const source = join(root, 'app.css');
const outDir = join(root, 'css');
const appRazor = join(repo, 'src', 'Tide.Web', 'Components', 'App.razor');
const manifestPath = join(repo, 'tools', 'css', 'split-manifest.json');

// The parts, in cascade order, and the heading each starts at (the start of its first line). The first part starts at
// the top of the file. Names sort in this order, so a directory listing shows the order they load in.
const parts = [
  { file: '00-tokens.css', starts: null, holds: ':root, the dark theme, the Bootstrap retint, base elements' },
  { file: '10-shell.css', starts: '/* ---- Shell ----', holds: 'the shell, page parts, badges and chips' },
  { file: '20-editor.css', starts: '/* ---- Article editor ----', holds: 'the article editor, brief builder, edition preview' },
  { file: '25-feedback.css', starts: '/* ---- Toasts, reconnect ----', holds: 'toasts, reconnect, lightbox' },
  { file: '30-present.css', starts: '/* ---- Present mode:', holds: 'present mode' },
  { file: '31-metrics.css', starts: '/* ---- Opening metrics:', holds: 'metrics and charts' },
  { file: '32-distribution.css', starts: '/* ---- Distribution:', holds: 'send and published' },
  { file: '40-actions.css', starts: '/* ---- Actions: SLA and status pills', holds: 'actions' },
  { file: '41-intake.css', starts: '/* ---- Intake:', holds: 'intake, triage, sources, stories, rotation' },
  { file: '50-today.css', starts: '/* ---- Today:', holds: 'Today and the pages that follow it' },
  { file: '60-rail-and-pages.css', starts: '/* ---- Context rail (ContextRail', holds: 'the context rail, search, brief chip' },
  { file: '90-option-e.css', starts: '/* ---- Triage 2.4:', holds: 'the Option E layers, appended in order' },
];

const args = process.argv.slice(2);
const write = args.includes('--write');
const vAt = args.indexOf('--version');
const version = vAt >= 0 ? args[vAt + 1] : `${new Date().toISOString().slice(0, 10).replaceAll('-', '')}-split`;
if (!/^[A-Za-z0-9._-]+$/.test(version ?? '')) fail(`--version needs a value of letters, digits, dots, hyphens: "${version}"`);
const unknown = args.filter((a, i) => !['--write', '--version'].includes(a) && i !== vAt + 1);
if (unknown.length) fail(`Unknown argument(s): ${unknown.join(' ')}`);

function fail(message) { console.error(`split-app-css: ${message}`); process.exit(1); }

if (!existsSync(source)) {
  fail(`${rel(source)} isn't there: the split has run already. To run it again on a newer main, restore app.css from ` +
       'that main first (tools/css/README.md).');
}
const bytes = readFileSync(source);
const text = bytes.toString('latin1'); // one char per byte, so offsets in text are offsets in bytes

// The offset of each line's start that is at the top level: outside every block and every comment. A heading only
// counts there, so a cut can't split a rule or an @media block.
function topLevelLineStarts(css) {
  const starts = new Set([0]);
  let depth = 0, inComment = false, quote = null;
  for (let i = 0; i < css.length; i++) {
    const c = css[i];
    if (inComment) { if (c === '*' && css[i + 1] === '/') { inComment = false; i++; } continue; }
    if (quote) { if (c === '\\') i++; else if (c === quote) quote = null; continue; }
    if (c === '/' && css[i + 1] === '*') { if (i === 0 || css[i - 1] === '\n') { if (depth === 0) starts.add(i); } inComment = true; i++; continue; }
    if (c === '"' || c === "'") { quote = c; continue; }
    if (c === '{') depth++;
    else if (c === '}') { depth--; if (depth < 0) fail(`an unmatched } at byte ${i}`); }
  }
  if (depth !== 0 || inComment || quote) fail('app.css ends inside a block, a comment or a string');
  return starts;
}

const topLevel = topLevelLineStarts(text);
const lineAt = offset => text.slice(0, offset).split('\n').length;
const cuts = [0];
for (const part of parts.slice(1)) {
  const found = [];
  for (let at = text.indexOf(part.starts); at >= 0; at = text.indexOf(part.starts, at + 1)) {
    if (at === 0 || text[at - 1] === '\n') found.push(at);
  }
  if (found.length === 0) fail(`${part.file}: its heading "${part.starts}" isn't at the start of any line of app.css`);
  if (found.length > 1) fail(`${part.file}: its heading "${part.starts}" starts ${found.length} lines (${found.map(lineAt).join(', ')}); make it unique`);
  if (!topLevel.has(found[0])) fail(`${part.file}: its heading at line ${lineAt(found[0])} is inside a block or a comment`);
  if (found[0] <= cuts.at(-1)) fail(`${part.file}: its heading at line ${lineAt(found[0])} comes before the part above it`);
  cuts.push(found[0]);
}

const pieces = parts.map((p, i) => ({ ...p, buf: bytes.subarray(cuts[i], i + 1 < cuts.length ? cuts[i + 1] : bytes.length) }));
if (!Buffer.concat(pieces.map(p => p.buf)).equals(bytes)) fail('the parts joined are not app.css (a bug in this script)');

const sha = buf => createHash('sha256').update(buf.toString('utf8').replaceAll('\r\n', '\n'), 'utf8').digest('hex');
for (const [i, p] of pieces.entries()) {
  const first = lineAt(cuts[i]), last = i + 1 < cuts.length ? lineAt(cuts[i + 1]) - 1 : text.split('\n').length - (text.endsWith('\n') ? 1 : 0);
  console.log(`${p.file.padEnd(24)} lines ${String(first).padStart(4)}-${String(last).padEnd(4)} ${String(p.buf.length).padStart(7)} bytes  ${p.holds}`);
}
console.log(`app.css: ${bytes.length} bytes, sha256 (LF) ${sha(bytes)}; the ${pieces.length} parts join into it byte for byte.`);

if (!write) { console.log('Nothing written (add --write to split).'); process.exit(0); }

// ---- Write ----

// App.razor: the one <link> to app.css, or the block an earlier run wrote, becomes a <link> per part, in order.
const razor = readFileSync(appRazor, 'utf8');
const eol = razor.includes('\r\n') ? '\r\n' : '\n';
const lines = razor.split(eol);
const isOld = l => /^\s*<link rel="stylesheet" href="app\.css\?v=[^"]*" \/>\s*$/.test(l);
const isOurs = l => /^\s*<link rel="stylesheet" href="css\/[^"]+\?v=[^"]*" \/>\s*$/.test(l) || /^\s*@\* The app's stylesheet, in parts/.test(l);
let from = lines.findIndex(isOld), to = from;
if (from < 0) {
  from = lines.findIndex(isOurs);
  if (from < 0) fail(`${rel(appRazor)} links neither app.css nor css/*.css`);
  for (to = from; to + 1 < lines.length && isOurs(lines[to + 1]); to++);
}
const indent = lines[from].match(/^\s*/)[0];
const block = [
  `${indent}@* The app's stylesheet, in parts by area (wwwroot/css). They load in file-name order, which is the cascade's: never reorder them. *@`,
  ...pieces.map(p => `${indent}<link rel="stylesheet" href="css/${p.file}?v=${version}" />`),
];
lines.splice(from, to - from + 1, ...block);

mkdirSync(outDir, { recursive: true });
for (const f of readdirSync(outDir)) if (f.endsWith('.css')) rmSync(join(outDir, f)); // an earlier run's parts
for (const p of pieces) writeFileSync(join(outDir, p.file), p.buf);
writeFileSync(appRazor, lines.join(eol), 'utf8');
writeFileSync(manifestPath, JSON.stringify({
  note: 'Written by split-app-css.mjs. StylesheetSplitTests checks that wwwroot/css/*.css, joined in this order, is the app.css this hash is of (line endings as LF).',
  source: 'src/Tide.Web/wwwroot/app.css',
  sourceBytesLf: Buffer.byteLength(bytes.toString('utf8').replaceAll('\r\n', '\n'), 'utf8'),
  sourceSha256Lf: sha(bytes),
  parts: pieces.map(p => p.file),
}, null, 2) + '\n');

// Read back what was written: the parts in file-name order must be app.css, byte for byte.
const written = readdirSync(outDir).filter(f => f.endsWith('.css')).sort();
if (written.join() !== pieces.map(p => p.file).join()) fail(`css/ holds ${written.join(', ')}: not the parts, in order`);
if (!Buffer.concat(written.map(f => readFileSync(join(outDir, f)))).equals(bytes)) fail('the written parts joined are not app.css');
rmSync(source);
console.log(`Wrote ${written.length} parts to ${rel(outDir)}, their <link>s (?v=${version}) to ${rel(appRazor)} and ` +
            `${rel(manifestPath)}; deleted ${rel(source)}.`);

function rel(p) { return relative(repo, p).replaceAll('\\', '/'); }
