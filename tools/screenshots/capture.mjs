// Captures the documentation screenshots (docs/screenshots/<name>.png) from a freshly seeded TIDE.
//
//   node tools/screenshots/capture.mjs                    build, seed a demo database, start TIDE, capture every shot
//   ONLY=triage-inbox,doc-editions node tools/screenshots/capture.mjs     capture just these
//   REUSE=1 node tools/screenshots/capture.mjs            keep the demo database from the last run (faster)
//   BASE_URL=http://localhost:5112 node ...               use a TIDE already running on the demo database
//
// The manifest below is the list of shots. See README.md in this folder for how to add one and how callouts work.

import { spawn, execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, createWriteStream } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
const web = path.join(repo, 'src', 'Tide.Web');
const outDir = path.join(repo, 'docs', 'screenshots');
const demoDir = path.join(here, '.demo');
const port = Number(process.env.PORT ?? 5112);
const external = process.env.BASE_URL?.replace(/\/$/, '');
const base = external ?? `http://localhost:${port}`;
const startedAt = new Date();

// --- the manifest -------------------------------------------------------------------------------------------------
//
// name      the file: docs/screenshots/<name>.png
// route     the page, with {today}, {sent}, {huntAction}, {detailAction}, {vpnArticle} or {sentArticle} filled from
//           the seed (see DemoSeeder.Seeded)
// viewport  { width, height }; fullPage: true captures the whole page at that width
// setup     async (page, demo) => {...}: clicks, keys or scrolling before the capture
// clip      a CSS selector: capture just that element (with `pad` pixels around it, default 16)
// annotate  [[selector, n], ...] or [[selector, n, corner]]: a numbered circle on the element's corner
//           (corner: tl (default), tr, bl, br, l, r). A selector that matches nothing fails the run.

const desktop = { width: 1440, height: 1000 };

/** Scrolls the first match to `top` pixels below the top of the window. */
const scrollTo = (selector, top = 70) => async page => {
  await page.locator(selector).first().evaluate((el, top) => window.scrollTo(0, el.getBoundingClientRect().top + window.scrollY - top), top);
};

export const shots = [
  // ---- the README's shots (same names, so existing links keep working) ----
  { name: 'brief-todays-numbers', route: '/', viewport: desktop,
    setup: scrollTo('.tide-card:has(.tide-mentry-bar)', 70) },
  { name: 'triage-inbox', route: '/triage', viewport: desktop },
  { name: 'article-editor', route: '/articles/{vpnArticle}', viewport: { width: 1440, height: 1300 } },
  { name: 'present-metrics-slide', route: '/present/{today}', viewport: desktop,
    setup: async page => { await page.keyboard.press('ArrowRight'); await page.waitForTimeout(400); } },
  { name: 'email-team', route: '/briefs/{sent}/email/team', viewport: desktop, fullPage: true },
  { name: 'email-company-wide', route: '/briefs/{sent}/email/public', viewport: desktop, fullPage: true },
  { name: 'sharepoint-copy', route: '/briefs/{sent}/sharepoint', viewport: desktop },
  { name: 'published-archive', route: '/published/{sent}', viewport: desktop },
  { name: 'action-hunt', route: '/actions/{huntAction}', viewport: desktop },
  { name: 'workload', route: '/workload', viewport: desktop },
  { name: 'search-lookup', route: '/search?q=update-check.example.net', viewport: { width: 1400, height: 1200 } },
  { name: 'export-indicators', route: '/', viewport: { width: 1400, height: 900 },
    setup: async page => { await page.getByRole('button', { name: 'Export indicators' }).click(); await page.waitForTimeout(300); } },
  { name: 'trends', route: '/trends', viewport: { width: 1500, height: 1100 },
    setup: async page => {
      await page.locator('tr:has(> td:text-is("Ransomware")) td.tide-trend-now button').first().click();
      await page.waitForTimeout(500);
      await scrollTo('.tide-card:has(td:text-is("Ransomware"))', 70)(page);
    } },
  { name: 'metrics-history', route: '/metrics', viewport: desktop },
  { name: 'rotation-calendar', route: '/rotation', viewport: desktop },

  // ---- shots for the documentation, with numbered callouts ----
  { name: 'doc-today-steps', route: '/', viewport: desktop, clip: '.tide-page-head, .tide-steps', pad: 20,
    annotate: [
      ['.tide-steps > li:nth-child(1)', 1], ['.tide-steps > li:nth-child(2)', 2], ['.tide-steps > li:nth-child(3)', 3],
      ['.tide-steps > li:nth-child(4)', 4], ['.tide-steps > li:nth-child(5)', 5], ['.tide-page-head button:has-text("Mark ready")', 6, 'l'],
    ] },
  { name: 'doc-triage-reading', route: '/triage', viewport: desktop,
    setup: async page => {
      await page.locator('.tide-triage-row').first().click({ position: { x: 900, y: 12 } });
      await page.keyboard.press('v');
      await page.locator('.tide-reading').first().waitFor();
      await page.waitForTimeout(500);
    },
    annotate: [
      ['.tide-tabbar-page', 1, 'l'], ['.tide-filterbar', 2, 'l'], ['.tide-triage-row', 3, 'l'],
      ['.tide-triage-row .tide-cve', 4, 'tr'], ['.tide-triage-row button:has-text("Promote")', 5, 'l'], ['.tide-reading', 6, 'tr'],
    ] },
  { name: 'doc-article-editor', route: '/articles/{vpnArticle}', viewport: { width: 1440, height: 1300 },
    annotate: [
      ['.tide-section-pills', 1, 'l'], ['.tide-section:has(h2:has-text("Assessment"))', 2, 'l'],
      ['.tide-bullet-row:nth-of-type(3) select', 3, 'tr'], ['.tide-reader .tide-seg', 4, 'l'],
      ['.tide-reader-sum', 5, 'l'], ['.tide-sticky-save', 6, 'l'],
    ] },
  { name: 'doc-editions', route: '/briefs/{sent}?tab=preview', viewport: { width: 1440, height: 1200 },
    annotate: [
      ['.tide-both-ed:nth-of-type(1) .tide-both-head', 1, 'l'], ['.tide-both-ed:nth-of-type(2) .tide-both-head', 2, 'l'],
      ['.tide-both-ed:nth-of-type(1) .tide-ed-numbers-head', 3, 'l'], ['.tide-both-ed:nth-of-type(1) li .tide-tlp-amber', 4, 'r'],
      ['.tide-both-ed:nth-of-type(2) :text("restricted parts were left out")', 5, 'l'],
    ] },
  { name: 'doc-action-detail', route: '/actions/{detailAction}', viewport: desktop,
    annotate: [
      ['.tide-status-row', 1, 'l'], ['.tide-facts', 2, 'l'], ['.tide-checklist', 3, 'l'],
      ['.tide-comment', 4, 'l'], ['.tide-card:has(h2:text-is("About"))', 5, 'l'], ['.tide-files-side', 6, 'l'],
    ] },
  { name: 'doc-admin-settings', route: '/admin/organization', viewport: desktop,
    annotate: [
      ['.tide-admin-tabs', 1, 'l'], ['.tide-admin-subtabs', 2, 'l'], ['.tide-admin-pane .tide-card:nth-of-type(1)', 3, 'l'],
      ['.tide-admin-pane .tide-card:has(h2:has-text("Holidays"))', 4, 'l'],
    ] },
  { name: 'doc-api-tokens', route: '/admin/api', viewport: desktop,
    annotate: [
      ['.tide-card:has(h2:has-text("Callers")) table', 1, 'l'], ['.tide-card:has(h2:has-text("Callers")) td .tide-tlp', 2, 'l'],
      ['.tide-card:has(h2:has-text("Callers")) button:has-text("Revoke")', 3, 'l'], ['h3:has-text("Issue a token")', 4, 'l'],
      ['button:has-text("Issue the token")', 5, 'r'],
    ] },
];

// --- the run -----------------------------------------------------------------------------------------------------------

const only = (process.env.ONLY ?? '').split(',').map(s => s.trim()).filter(Boolean);
const unknown = only.filter(n => !shots.some(s => s.name === n));
if (unknown.length) fail(`No shot named ${unknown.join(', ')}. Shots: ${shots.map(s => s.name).join(', ')}`);
const wanted = only.length ? shots.filter(s => only.includes(s.name)) : shots;

let server;
try {
  if (!external) server = await startTide();
  const demo = JSON.parse(readFileSync(path.join(demoDir, 'demo.json'), 'utf8'));
  const problems = await capture(wanted, demo);
  if (problems.length) {
    for (const p of problems) console.error(p);
    process.exitCode = 1;
  }
} catch (e) {
  console.error(e.message ?? e);
  process.exitCode = 1;
} finally {
  server?.stop();
}

function fail(message) {
  console.error(message);
  process.exit(1);
}

async function capture(list, demo) {
  const fill = route => route.replace(/\{(\w+)\}/g, (_, key) => {
    const value = demo[key[0].toUpperCase() + key.slice(1)];
    if (value === undefined) throw new Error(`The route ${route} names {${key}}, which the seed doesn't provide.`);
    return String(value);
  });
  mkdirSync(outDir, { recursive: true });
  const browser = await chromium.launch();
  const problems = [];
  try {
    for (const shot of list) {
      const context = await browser.newContext({
        viewport: shot.viewport ?? desktop, deviceScaleFactor: 1, reducedMotion: 'reduce', colorScheme: 'light',
        locale: 'en-US', timezoneId: 'America/New_York',
      });
      // The light theme and an expanded sidebar, whatever a previous visit chose.
      await context.addInitScript(() => {
        try { localStorage.setItem('tide-theme', 'light'); localStorage.removeItem('tide-nav'); } catch { /* none */ }
      });
      const page = await context.newPage();
      // The browser's clock stands still at the moment the run started, so anything the page times reads the same.
      await page.clock.setFixedTime(startedAt);
      try {
        await page.goto(base + fill(shot.route), { waitUntil: 'networkidle' });
        await settle(page);
        if (shot.setup) { await shot.setup(page, demo); await settle(page); }
        const missing = await annotate(page, shot.annotate ?? []);
        if (missing.length) {
          problems.push(`${shot.name}: annotation target(s) not found: ${missing.join(' | ')}`);
          continue;
        }
        const file = path.join(outDir, `${shot.name}.png`);
        await page.screenshot({ path: file, fullPage: !!shot.fullPage, animations: 'disabled', caret: 'hide', clip: await clipFor(page, shot) });
        console.log(`  ${shot.name}.png`);
      } catch (e) {
        problems.push(`${shot.name}: ${e.message.split('\n')[0]}`);
      } finally {
        await context.close();
      }
    }
  } finally {
    await browser.close();
  }
  return problems;
}


// The page is interactive (Blazor's circuit is up) and fonts are in.
async function settle(page) {
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(500);
}

async function clipFor(page, shot) {
  if (!shot.clip) return undefined;
  const pad = shot.pad ?? 16;
  const boxes = (await Promise.all((await page.locator(shot.clip).all()).map(l => l.boundingBox()))).filter(Boolean);
  if (!boxes.length) throw new Error(`clip target not found: ${shot.clip}`);
  const vp = page.viewportSize();
  const x = Math.max(0, Math.min(...boxes.map(b => b.x)) - pad);
  const y = Math.max(0, Math.min(...boxes.map(b => b.y)) - pad);
  const right = Math.min(vp.width, Math.max(...boxes.map(b => b.x + b.width)) + pad);
  const bottom = Math.min(vp.height, Math.max(...boxes.map(b => b.y + b.height)) + pad);
  return { x, y, width: right - x, height: bottom - y };
}

/** Draws the numbered callouts on the page. Returns the selectors that matched nothing visible. */
async function annotate(page, callouts) {
  const targets = [];
  const missing = [];
  for (const [selector, n, corner = 'tl'] of callouts) {
    const box = await page.locator(selector).first().boundingBox({ timeout: 2000 }).catch(() => null);
    if (!box || box.width === 0 || box.height === 0) missing.push(selector);
    else targets.push({ n, corner, box });
  }
  if (missing.length || !targets.length) return missing;
  await page.evaluate(targets => {
    const r = 14;
    const layer = document.createElement('div');
    layer.id = 'tide-callouts';
    layer.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:2147483647';
    for (const { n, corner, box } of targets) {
      const x = { tl: box.x, bl: box.x, l: box.x - r - 4, tr: box.x + box.width, br: box.x + box.width, r: box.x + box.width + r + 4 }[corner];
      const y = { tl: box.y, tr: box.y, l: box.y + box.height / 2, r: box.y + box.height / 2, bl: box.y + box.height, br: box.y + box.height }[corner];
      const cx = Math.min(Math.max(x, r + 2), innerWidth - r - 2);
      const cy = Math.min(Math.max(y, r + 2), innerHeight - r - 2);
      const dot = document.createElement('div');
      dot.textContent = String(n);
      dot.style.cssText = `position:absolute;left:${cx - r}px;top:${cy - r}px;width:${2 * r}px;height:${2 * r}px;border-radius:50%;` +
        'background:#6d28d9;color:#fff;border:2px solid #fff;box-sizing:border-box;box-shadow:0 1px 4px rgba(0,0,0,.45);' +
        'font:700 14px/24px system-ui,Segoe UI,sans-serif;text-align:center';
      layer.appendChild(dot);
    }
    document.body.appendChild(layer);
  }, targets);
  return [];
}

/**
 * The dotnet executable, by absolute path rather than a search of PATH: DOTNET_HOST_PATH (set when run from dotnet),
 * DOTNET_ROOT, or the SDK's usual install folders.
 */
function dotnet() {
  const exe = process.platform === 'win32' ? 'dotnet.exe' : 'dotnet';
  const candidates = [
    process.env.DOTNET_HOST_PATH,
    process.env.DOTNET_ROOT && path.join(process.env.DOTNET_ROOT, exe),
    process.platform === 'win32' && path.join(process.env.ProgramFiles ?? 'C:\\Program Files', 'dotnet', exe),
    '/usr/local/share/dotnet/dotnet', '/usr/share/dotnet/dotnet', '/usr/lib/dotnet/dotnet',
    path.join(os.homedir(), '.dotnet', exe),
  ].filter(Boolean);
  const found = candidates.find(c => path.isAbsolute(c) && existsSync(c));
  if (!found) throw new Error(`dotnet wasn't found in ${candidates.join(', ')}. Set DOTNET_ROOT to the folder that holds ${exe}.`);
  return found;
}

// --- TIDE, seeded ------------------------------------------------------------------------------------------------------

async function startTide() {
  if (!process.env.REUSE) rmSync(demoDir, { recursive: true, force: true });
  mkdirSync(demoDir, { recursive: true });
  console.log('Building TIDE...');
  execFileSync(dotnet(), ['build', path.join(web, 'Tide.Web.csproj'), '-c', 'Debug', '-v', 'q', '-nologo'], { stdio: 'inherit' });
  const binDir = path.join(web, 'bin', 'Debug');
  const tfm = readdirSync(binDir).find(d => existsSync(path.join(binDir, d, 'Tide.Web.dll')));
  if (!tfm) throw new Error('The build left no Tide.Web.dll under src/Tide.Web/bin/Debug.');

  console.log(`Starting TIDE on ${base} with the demo database (${process.env.REUSE ? 'reused' : 'fresh'})...`);
  const log = createWriteStream(path.join(demoDir, 'tide.log'));
  const child = spawn(dotnet(), [path.join(binDir, tfm, 'Tide.Web.dll'),
    '--urls', base,
    '--Demo:Seed=true', `--Demo:InfoFile=${path.join(demoDir, 'demo.json')}`,
    `--ConnectionStrings:Default=Data Source=${path.join(demoDir, 'tide.db')}`,
    `--Files:RootPath=${path.join(demoDir, 'files')}`,
    `--Email:PickupDirectory=${path.join(demoDir, 'mail')}`,
    '--Logging:LogLevel:Default=Warning', '--Logging:LogLevel:Tide.Demo=Information',
  ], { cwd: web, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe'] });
  child.stdout.pipe(log);
  child.stderr.pipe(log);
  let exited = null;
  child.on('exit', code => { exited = code; });

  const deadline = Date.now() + 180_000;
  while (Date.now() < deadline) {
    if (exited !== null) throw new Error(`TIDE stopped (exit ${exited}) before it answered; see ${path.join(demoDir, 'tide.log')}.`);
    try {
      if ((await fetch(`${base}/health/live`)).ok) break;
    } catch { /* not up yet */ }
    await new Promise(r => setTimeout(r, 500));
  }
  if (Date.now() >= deadline) { child.kill(); throw new Error('TIDE did not answer within three minutes.'); }
  console.log('Capturing...');
  return { stop: () => child.kill() };
}
