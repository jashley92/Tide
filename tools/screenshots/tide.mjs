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
// DEMO_DIR keeps runs side by side apart (each its own database and log); .demo here otherwise.
const demoDir = process.env.DEMO_DIR ? path.resolve(process.env.DEMO_DIR) : path.join(here, '.demo');

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
    await watch(context);
    const page = await context.newPage();
    if (fixedTime) await page.clock.setFixedTime(fixedTime);
    await page.goto(baseUrl() + fill(route, demo), { waitUntil: 'load' });
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
  for (let i = 0; i < 15 && !(await page.locator('[data-slide-key^="article:"]').count()); i++) {
    await page.keyboard.press('ArrowRight');
    await idle(page);
  }
}

/** The page is interactive (Blazor's circuit is up), fonts are in, and it's still: idle, under the name the scripts use
 * after a load. */
export const settle = page => idle(page);

/** Watches each page of `context` from its start, for idle: openPage does it; a context made by hand needs it. */
export const watch = context => context.addInitScript(tracker, false);

/**
 * In the page: notes the time of anything that changes it (a change to the DOM, a message to or from the server, focus
 * moving, a scroll, a transition or animation starting) and whether Blazor's circuit has drawn its first render. Run
 * `late` (by idle, on a page watch didn't start on), the page's socket is already open, and only the rest is seen.
 */
function tracker(late) {
  if (window.__tideStill) return;
  const s = window.__tideStill = { n: 0, at: performance.now(), late, open: 0, rendered: false };
  const touch = () => { s.n++; s.at = performance.now(); };
  const Socket = window.WebSocket, send = Socket.prototype.send;
  Socket.prototype.send = function (data) { touch(); return send.call(this, data); };
  if (!late) {
    // Blazor's messages are binary (MessagePack): a render batch names its target, JS.RenderBatch, in plain ASCII near
    // the start.
    const text = new TextDecoder('latin1');
    const head = data => text.decode(new Uint8Array(data, 0, Math.min(64, data.byteLength)));
    s.Socket = window.WebSocket = class extends Socket {
      constructor(...args) {
        super(...args);
        this.addEventListener('open', () => { s.open++; touch(); });
        this.addEventListener('close', () => { s.open--; touch(); });
        this.addEventListener('message', e => {
          touch();
          if (!s.rendered && e.data instanceof ArrayBuffer && head(e.data).includes('JS.RenderBatch')) s.rendered = true;
        });
      }
    };
  }
  new MutationObserver(touch).observe(document, { subtree: true, childList: true, attributes: true, characterData: true });
  for (const type of ['focusin', 'scroll', 'transitionrun', 'animationstart', 'load']) addEventListener(type, touch, true);
}

// How long nothing may change, with the server still, before the page counts as still. It covers what the server does
// outside an inbound activity: a message on its way, a render's after-work, a page's data read after a navigation, and
// a list's rail, which rests 150ms on a selection before it reads the row (ActionRowRail.Rest and the like). A longer
// wait of the app's own (a search's or the preview's pause after typing) is waited out where it's met.
const QUIET_MS = 200;
const noActivityAt = new Set();

/**
 * Waits until the page is still: Blazor's circuit is up and has drawn, the server has no event or callback in hand (the
 * Development-only /demo/activity), and for QUIET_MS nothing has changed in the page, with no finite transition or
 * animation running, no image or font loading and nothing shown loading (a placeholder, an aria-busy region, the app's
 * "Loading…"). Checked twice in a row with nothing changed between, so a render on its way when the server went still is
 * seen. Gives up after `timeout` (a page that never settles is then checked as it is, as a fixed pause would leave it),
 * saying what it was waiting for.
 */
export async function idle(page, timeout = 10_000) {
  const origin = new URL(page.url()).origin;
  const end = Date.now() + timeout;
  let was = null, here, server;
  while (Date.now() < end) {
    // A page loading anew (a link that leaves the app's own navigation) has no context to ask for a moment.
    here = await page.evaluate(stillness).catch(() => ({ busy: 'a page loading' }));
    if (here === null) { await page.evaluate(tracker, true); continue; }
    // The server is asked only once the page itself is still.
    server = !here.busy && here.quiet >= QUIET_MS ? await serverActivity(origin) : null;
    const still = !!server?.idle;
    if (still && was && was.n === here.n && was.started === server.started) return;
    was = still ? { n: here.n, started: server.started } : null;
    await page.waitForTimeout(still || here.busy ? 20 : Math.max(20, QUIET_MS - here.quiet));
  }
  console.log(`    (not still after ${timeout / 1000}s, ${here?.busy || (server && !server.idle ? 'the server busy' : 'changing')}: ${page.url()})`);
}

/** In the page: how long since anything changed, and what's still under way ('' for nothing). */
function stillness() {
  const s = window.__tideStill;
  if (!s) return null;
  const shown = el => el.getClientRects().length > 0;
  const busy = !s.late && window.WebSocket === s.Socket && !(s.open > 0 && s.rendered) ? 'the circuit starting'
    : document.fonts.status === 'loading' ? 'fonts loading'
    : Array.prototype.some.call(document.images,i => !i.complete && i.loading !== 'lazy') ? 'an image loading'
    : document.getAnimations().some(a => a.playState === 'running' && a.effect?.getComputedTiming().endTime !== Infinity) ? 'an animation running'
    : [...document.querySelectorAll('.tide-loading, [aria-busy="true"]')].some(shown) || document.body.innerText.includes('Loading…') ? 'something loading'
    : '';
  return { n: s.n, quiet: performance.now() - s.at, busy };
}

/** The server's circuit work: { idle, started } from /demo/activity, or idle always where it doesn't answer. */
async function serverActivity(origin) {
  if (noActivityAt.has(origin)) return { idle: true, started: 0 };
  try {
    const res = await fetch(`${origin}/demo/activity`);
    if (res.ok) {
      const { started, inHand } = await res.json();
      return { idle: inHand === 0, started };
    }
  } catch { /* not answering */ }
  noActivityAt.add(origin);
  return { idle: true, started: 0 };
}

/**
 * Builds TIDE, seeds the demo database (REUSE=1 keeps the last one) and starts it, with any `settings` added to its
 * command line (`--Demo:TriageFiller=55`). Returns { stop }, or null when BASE_URL names one already running.
 */
export async function startTide(settings = []) {
  if (process.env.BASE_URL) return null;
  const base = baseUrl();
  // A TIDE left running on the port from an earlier run would answer in place of this one, with its own day's demo.
  if (await fetch(`${base}/health/live`, { signal: AbortSignal.timeout(2000) }).then(r => r.ok, () => false)) {
    throw new Error(`Something already answers on ${base}. Stop it (a TIDE left from an earlier run?), or name it with BASE_URL.`);
  }
  if (!process.env.REUSE) rmSync(demoDir, { recursive: true, force: true });
  mkdirSync(demoDir, { recursive: true });
  // NO_BUILD=1 starts the last build, for runs side by side that one build served.
  if (!process.env.NO_BUILD) console.log('Building TIDE...');
  if (!process.env.NO_BUILD) execFileSync(dotnet(), ['build', path.join(web, 'Tide.Web.csproj'), '-c', 'Debug', '-v', 'q', '-nologo'], { stdio: 'inherit' });
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
