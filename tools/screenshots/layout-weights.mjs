// Refreshes layout-weights.json, what each layout page took in CI, from the logs of green CI runs' layout shards, so
// shards.mjs can cut the pages into slices that take about as long as each other.
//
//   node tools/screenshots/layout-weights.mjs <run id> [<run id> ...]      (needs gh, signed in; two or more runs)
//
// Each page's line in a shard's log has a timestamp, so the time since the line before is that width's. A page's
// weight is its mean over every log it's in (each run, each letter spacing). The first width in a log also carries
// TIDE's start, so it's counted as that page's other widths' mean, and a page whose only width is first isn't timed by
// that log. A page these runs didn't time keeps its weight, and one no run has timed is counted by its widths
// (perWidth, perPresentWidth: the median seconds per width).

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';

const runs = process.argv.slice(2);
if (!runs.length || runs.some(r => !/^\d+$/.test(r))) {
  console.error('Usage: node tools/screenshots/layout-weights.mjs <run id> [<run id> ...]');
  process.exit(1);
}

const file = new URL('./layout-weights.json', import.meta.url);
const gh = args => execFileSync('gh', args, { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 });
const repo = gh(['repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner']).trim();

const samples = new Map();      // page -> seconds, one per log
const perWidth = { present: [], other: [] };
// "  <page> at <width>x<height>: …" for each width, or "  <page>: not checked today …" for a page skipped on the day.
const line = /^(\S+Z)\s+(\S.*?)(?: at \d+x\d+: |: not checked today)/;

for (const run of runs) {
  const jobs = JSON.parse(gh(['api', `repos/${repo}/actions/runs/${run}/jobs?per_page=100`, '--jq', '[.jobs[] | select(.name | startswith("layout (")) | {id, name, conclusion}]']));
  if (!jobs.length) throw new Error(`Run ${run} has no layout shards.`);
  for (const job of jobs) {
    if (job.conclusion !== 'success') throw new Error(`Run ${run}'s ${job.name} didn't pass: time a green run.`);
    const lines = gh(['api', `repos/${repo}/actions/jobs/${job.id}/logs`]).split('\n');
    const start = lines.findIndex(l => l.includes('Starting TIDE on'));
    const took = new Map(), widths = new Map();
    let prev, first;
    for (const l of lines.slice(start + 1)) {
      const m = l.match(line);
      if (!m) continue;
      const t = Date.parse(m[1]), name = m[2];
      widths.set(name, (widths.get(name) ?? 0) + 1);
      if (prev === undefined) first = name;
      else took.set(name, (took.get(name) ?? 0) + (t - prev) / 1000);
      prev = t;
    }
    for (const [name, n] of widths) {
      const timed = name === first ? n - 1 : n;
      if (!timed) continue;
      const seconds = (took.get(name) ?? 0) * n / timed;
      samples.set(name, [...(samples.get(name) ?? []), seconds]);
      // Which pages are Present's isn't in a log, so it's read from their names, as close as a fallback needs.
      (/^(present|slide|rehearsal)-/.test(name) ? perWidth.present : perWidth.other).push(seconds / n);
    }
    console.log(`${run} ${job.name}: ${widths.size} pages`);
  }
}

const median = xs => [...xs].sort((a, b) => a - b)[Math.floor(xs.length / 2)];
const round = x => Math.round(x * 10) / 10;
const old = JSON.parse(readFileSync(file, 'utf8'));
const pages = { ...old.pages };
for (const [name, xs] of samples) pages[name] = round(xs.reduce((a, b) => a + b, 0) / xs.length);

const fallback = {
  perWidth: perWidth.other.length ? round(median(perWidth.other)) : old.perWidth,
  perPresentWidth: perWidth.present.length ? round(median(perWidth.present)) : old.perPresentWidth,
};

const sorted = Object.fromEntries(Object.entries(pages).sort(([a], [b]) => a.localeCompare(b)));
writeFileSync(file, JSON.stringify({ ...old, ...fallback, runs, pages: sorted }, null, 2) + '\n');
console.log(`Wrote ${samples.size} pages' weights from ${runs.length} run${runs.length === 1 ? '' : 's'} to layout-weights.json.`);
