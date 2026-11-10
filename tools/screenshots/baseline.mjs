// Captures a "before" (or "after") picture of the main screens from a freshly seeded TIDE, each at three window sizes
// in the light and the dark theme, into artifacts/ux-baseline/<label>/ (git-ignored; never docs/screenshots). Run it
// before a change to the UI and again after, under another label, and compare the two folders.
//
//   node tools/screenshots/baseline.mjs before       build, seed a demo database, start TIDE, capture every screen
//   ONLY=today,present-wrap-up node tools/screenshots/baseline.mjs after     just these screens
//   FULL=1 node tools/screenshots/baseline.mjs before                        the whole page, not just the window
//   REUSE=1 / BASE_URL=http://localhost:5112 node ...                        as capture.mjs
//
// The list below is the screens. See README.md in this folder.

import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { chromium } from 'playwright';
import { openPage, pick, readDemo, repo, startTide, toArticleSlide } from './tide.mjs';

const sizes = [{ width: 1920, height: 1080 }, { width: 1440, height: 900 }, { width: 780, height: 900 }];
const themes = ['light', 'dark'];

// name   the file: <name>-<width>x<height>-<theme>.png
// route  the page, filled from the seed as capture.mjs's routes are
// setup  async page => {...}: keys before the capture
const screens = [
  { name: 'today', route: '/' },
  { name: 'triage', route: '/triage' },
  { name: 'article-editor', route: '/articles/{vpnArticle}' },
  { name: 'search-indicator', route: '/search?q=update-check.example.net' },
  { name: 'actions-list', route: '/actions?view=open&layout=list' },
  { name: 'actions-board', route: '/actions?view=open&layout=board' },
  { name: 'action', route: '/actions/{detailAction}' },
  { name: 'stack', route: '/stack' },
  { name: 'stack-product', route: '/stack/{stackProduct}' },
  { name: 'saved-queries', route: '/hunts/saved' },
  { name: 'outreach-templates', route: '/outreach/templates' },
  { name: 'playbooks', route: '/playbooks' },
  { name: 'board-rules', route: '/board-rules' },
  { name: 'present-title', route: '/present/{today}', setup: page => page.keyboard.press('Home') },
  { name: 'present-article', route: '/present/{today}', setup: toArticleSlide },
  { name: 'present-wrap-up', route: '/present/{today}', setup: page => page.keyboard.press('End') },
];

// --- the run -----------------------------------------------------------------------------------------------------------

const label = process.argv[2] ?? 'before';
// A folder name, not a path: the images stay under artifacts/ux-baseline.
if (!/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(label)) {
  console.error(`The label "${label}" isn't a folder name: letters, digits, dots, hyphens and underscores only.`);
  process.exit(1);
}
const outDir = path.join(repo, 'artifacts', 'ux-baseline', label);
const wanted = pick(screens, 'screen');
const startedAt = new Date();

let server;
try {
  server = await startTide();
  const { written, problems } = await capture(wanted, readDemo());
  console.log(`\n${written} image${written === 1 ? '' : 's'} written to ${path.relative(repo, outDir)}`);
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

async function capture(list, demo) {
  mkdirSync(outDir, { recursive: true });
  const browser = await chromium.launch();
  const problems = [];
  let written = 0;
  try {
    const jobs = list.flatMap(screen => sizes.flatMap(viewport => themes.map(theme => ({ screen, viewport, theme }))));
    for (const { screen, viewport, theme } of jobs) {
      const file = `${screen.name}-${viewport.width}x${viewport.height}-${theme}.png`;
      let context;
      try {
        let page;
        ({ context, page } = await openPage(browser, { ...screen, viewport }, demo, { deviceScaleFactor: 1, fixedTime: startedAt, theme }));
        await page.screenshot({ path: path.join(outDir, file), fullPage: !!process.env.FULL, animations: 'disabled', caret: 'hide' });
        written++;
        console.log(`  ${file}`);
      } catch (e) {
        problems.push(`${file}: ${e.message.split('\n')[0]}`);
      } finally {
        await context?.close();
      }
    }
  } finally {
    await browser.close();
  }
  return { written, problems };
}
