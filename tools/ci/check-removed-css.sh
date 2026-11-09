#!/usr/bin/env bash
# Fails when a change to src/Tide.Web/wwwroot/app.css drops the last rule for a tide-* class the markup still uses.
#
#   tools/ci/check-removed-css.sh [base]     base: the ref to compare with (default origin/main)
#
# A class used in a Razor component or C# file (class="... tide-foo ...") that the base stylesheet styled and this one no
# longer mentions at all has lost its look: its element falls back to the browser's defaults, which a test reading the
# rendered HTML can't see (v1.10.0's meeting quick add stacked its five controls one per line). Rename or remove the class
# in the markup too, or keep a rule for it.
set -euo pipefail
export LC_ALL=C.UTF-8   # grep -P needs a UTF-8 locale (Git Bash's default isn't one)

base="${1:-origin/main}"
# A push that starts a branch has no "before" (all zeros): nothing to compare with.
if ! git rev-parse --verify -q "$base^{commit}" >/dev/null; then
  echo "No base to compare app.css with ($base); skipped."
  exit 0
fi
css="src/Tide.Web/wwwroot/app.css"
markup="src/Tide.Web/Components"

classes() { grep -oE '\.tide-[A-Za-z0-9_-]+' | sed 's/^\.//' | sort -u; }

before=$(git show "$base:$css" | classes)
after=$(classes < "$css")
dropped=$(comm -23 <(echo "$before") <(echo "$after"))

failed=0
for c in $dropped; do
  # Used as a whole class name: not a prefix of a longer one (tide-foo in tide-foo-bar) or the end of one.
  uses=$(grep -rlP --include='*.razor' --include='*.cs' "(?<![\\w-])${c}(?![\\w-])" "$markup" || true)
  if [ -n "$uses" ]; then
    echo "::error file=$css::.$c lost its last rule, but the markup still uses it: $(echo $uses | tr ' ' ',')"
    failed=1
  fi
done

if [ "$failed" = 1 ]; then
  echo "A class above no longer has any style. Restore its rule, or change the markup that uses it." >&2
  exit 1
fi
echo "No class the markup uses lost its style ($(echo "$dropped" | grep -c . || true) dropped, none still used)."
