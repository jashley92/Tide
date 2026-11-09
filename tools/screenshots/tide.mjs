// TIDE on a freshly seeded demo database, for the scripts in this folder (capture.mjs, layout.mjs): build it, seed it
// (Demo:Seed, Development only), start it on its own port and wait until it answers. Your own dev database isn't touched.

import { spawn, execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, createWriteStream } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const repo = path.resolve(here, '..', '..');
const web = path.join(repo, 'src', 'Tide.Web');
export const demoDir = path.join(here, '.demo');

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

/** The page is interactive (Blazor's circuit is up) and fonts are in. */
export async function settle(page) {
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(500);
}

/**
 * Builds TIDE, seeds the demo database (REUSE=1 keeps the last one) and starts it. Returns { stop }, or null when BASE_URL
 * names one already running.
 */
export async function startTide() {
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
