// TIDE on a freshly seeded demo database, for the scripts in this folder (capture.mjs, layout.mjs, baseline.mjs): build
// it, seed it (Demo:Seed, Development only), start it on its own port and wait until it answers. Your own dev database
// isn't touched.

import { spawn, execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, createWriteStream } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const repo = path.resolve(here, '..', '..');
const web = path.join(repo, 'src', 'Tide.Web');
const demoDir = path.join(here, '.demo');

/** The address TIDE answers on: BASE_URL (one already running on the demo database), else localhost:PORT (5112). */
export function baseUrl() {
  return process.env.BASE_URL?.replace(/\/$/, '') ?? `http://localhost:${Number(process.env.PORT ?? 5112)}`;
}

/** The seed's names for the days and records routes refer to (DemoSeeder.Seeded), read from .demo/demo.json. */
export function readDemo() {
  return JSON.parse(readFileSync(path.join(demoDir, 'demo.json'), 'utf8'));
}

/** A route with {today}, {vpnArticle} and the like filled from the seed. */
export function fill(route, demo) {
  return route.replace(/\{(\w+)\}/g, (_, key) => {
    const value = demo[key[0].toUpperCase() + key.slice(1)];
    if (value === undefined) throw new Error(`The route ${route} names {${key}}, which the seed doesn't provide.`);
    return String(value);
  });
}

/** The entries `ONLY=name1,name2` names, or all of them; an unknown name ends the run, listing the names there are. */
export function pick(list, noun) {
  const only = (process.env.ONLY ?? '').split(',').map(s => s.trim()).filter(Boolean);
  const unknown = only.filter(n => !list.some(e => e.name === n));
  if (unknown.length) {
    console.error(`No ${noun} named ${unknown.join(', ')}. There are: ${list.map(e => e.name).join(', ')}`);
    process.exit(1);
  }
  return only.length ? list.filter(e => only.includes(e.name)) : list;
}

/**
 * Opens `route` in a fresh browser context, settled and set up: the same window settings for every script (reduced
 * motion, US English, New York time), the `theme` ('light' unless 'dark' is asked for), an expanded sidebar and the context
 * rail as it starts whatever a previous visit chose, and any `extra` context options. `fixedTime` stops the browser's clock
 * there. Returns { context, page }: close the context.
 */
export async function openPage(browser, { route, viewport, setup }, demo, { fixedTime, theme = 'light', ...extra } = {}) {
  const context = await browser.newContext({
    viewport, reducedMotion: 'reduce', colorScheme: theme, locale: 'en-US', timezoneId: 'America/New_York', ...extra,
  });
  try {
    await context.addInitScript(theme => {
      try { localStorage.setItem('tide-theme', theme); localStorage.removeItem('tide-nav'); localStorage.removeItem('tide-rail'); } catch { /* none */ }
    }, theme);
    const page = await context.newPage();
    if (fixedTime) await page.clock.setFixedTime(fixedTime);
    await page.goto(baseUrl() + fill(route, demo), { waitUntil: 'networkidle' });
    await settle(page);
    if (setup) { await setup(page, demo); await settle(page); }
    return { context, page };
  } catch (e) {
    await context.close();
    throw e;
  }
}

/** In present mode: steps through the slides to the first article's (the one with its recommended lines and actions). */
export async function toArticleSlide(page) {
  for (let i = 0; i < 15 && !(await page.locator('.tide-quick-add').count()); i++) {
    await page.keyboard.press('ArrowRight');
    await page.waitForTimeout(250);
  }
}

/** The page is interactive (Blazor's circuit is up) and fonts are in. */
export async function settle(page) {
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(500);
}

/**
 * Builds TIDE, seeds the demo database (REUSE=1 keeps the last one) and starts it, with any `settings` added to its
 * command line (`--Demo:TriageFiller=55`). Returns { stop }, or null when BASE_URL names one already running.
 */
export async function startTide(settings = []) {
  if (process.env.BASE_URL) return null;
  const base = baseUrl();
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
    '--Logging:LogLevel:Default=Warning', '--Logging:LogLevel:Tide.Demo=Information', ...settings,
  ], { cwd: web, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe'] });
  child.stdout.pipe(log);
  child.stderr.pipe(log);
  let exited = null;
  child.on('exit', code => { exited = code; });

  const deadline = Date.now() + 180_000;
  while (Date.now() < deadline) {
    if (exited !== null) throw new Error(`TIDE stopped (exit ${exited}) before it answered; see ${path.join(demoDir, 'tide.log')}.`);
    try {
      if ((await fetch(`${base}/health/live`)).ok) return { stop: () => child.kill() };
    } catch { /* not up yet */ }
    await new Promise(r => setTimeout(r, 500));
  }
  child.kill();
  throw new Error('TIDE did not answer within three minutes.');
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
