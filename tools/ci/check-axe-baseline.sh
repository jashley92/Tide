#!/usr/bin/env bash
# The accessibility scan's baseline (tools/screenshots/axe-baseline.json) only shrinks without a word: each entry lets
# one rule stay broken on one page, so the gate in layout.mjs doesn't fail on it. An entry may be removed freely (the
# finding was fixed), but one added since the base must say why in its "reason", which the reviewer reads. Every entry
# must name its rule and page.
#
# Usage: tools/ci/check-axe-baseline.sh [base]   base: the ref to compare with (default origin/main)
set -euo pipefail

base="${1:-origin/main}"
file=tools/screenshots/axe-baseline.json

if ! git rev-parse --verify -q "$base^{commit}" >/dev/null; then echo "No base to compare the baseline with ($base); skipped."; exit 0; fi

before="$(git show "$base:$file" 2>/dev/null || echo '[]')"

BEFORE="$before" node - "$file" <<'JS'
const { readFileSync } = require('node:fs');
const file = process.argv[2];
const read = (text, where) => {
  let list;
  try { list = JSON.parse(text); } catch (e) { console.log(`::error file=${file}::${where} isn't JSON: ${e.message}`); process.exit(1); }
  if (!Array.isArray(list)) { console.log(`::error file=${file}::${where} must be a list of entries`); process.exit(1); }
  return list;
};
const now = read(readFileSync(file, 'utf8'), 'the baseline');
const was = read(process.env.BEFORE, 'the base\'s baseline');
const key = e => JSON.stringify([e.rule, e.page, e.target ?? '']);
const known = new Set(was.map(key));
let bad = 0;
for (const e of now) {
  const name = `${e.rule ?? '?'} on ${e.page ?? '?'}${e.target ? ` (${e.target})` : ''}`;
  if (!e.rule || !e.page) { console.log(`::error file=${file}::${name}: every entry names its rule and page`); bad = 1; continue; }
  if (!known.has(key(e)) && !(typeof e.reason === 'string' && e.reason.trim())) {
    console.log(`::error file=${file}::${name} is new since the base, with no reason: say why it can't be fixed now`);
    bad = 1;
  }
}
const added = now.filter(e => !known.has(key(e))).length, removed = was.filter(e => !new Set(now.map(key)).has(key(e))).length;
if (!bad) console.log(`The axe baseline has ${now.length} entr${now.length === 1 ? 'y' : 'ies'} (${added} added with a reason, ${removed} removed).`);
process.exit(bad);
JS
