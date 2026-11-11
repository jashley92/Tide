#!/usr/bin/env bash
# Every script and stylesheet that changed ships: App.razor loads each self-hosted file with ?v=, and browsers cache it
# by that address, so a file changed without a new ?v= is served from the old copy. A page that then calls a function
# only the new copy has throws (a stale brief-order.js lacked tideOrder.focusTitle, and Today was replaced by its
# error). A change to a file under wwwroot/js or a wwwroot stylesheet that App.razor loads fails here unless the ?v=
# App.razor loads it with changed too. A file new since the base has no old copy to be served, so it needs nothing.
#
# Usage: tools/ci/check-asset-versions.sh [base]   base: the ref to compare with (default origin/main)
set -euo pipefail

base="${1:-origin/main}"
root=src/Tide.Web/wwwroot
app=src/Tide.Web/Components/App.razor

# A push that starts a branch has no "before" (all zeros): nothing to compare with.
if ! git rev-parse --verify -q "$base^{commit}" >/dev/null; then echo "No base to compare the assets with ($base); skipped."; exit 0; fi

# The address App.razor loads a file with, ?v= and all, from its text on stdin ("" when it doesn't load it).
loaded() { grep -oE "\"${1//./[.]}[?]v=[^\"]*\"" | head -1 || true; }

before_app="$(git show "$base:$app" 2>/dev/null || true)"
bad=0
# Changed since the base, committed or not (a local run checks work in progress too).
while IFS= read -r file; do
  [[ -z "$file" ]] && continue
  rel="${file#"$root"/}"
  now="$(loaded "$rel" < "$app")"
  [[ -z "$now" ]] && continue                      # not loaded by App.razor (a page loads it itself, or nothing does)
  was="$(loaded "$rel" <<<"$before_app")"
  if [[ -n "$was" && "$was" == "$now" ]]; then
    echo "::error file=$app::$rel changed since $base, but App.razor still loads it as ${now//\"/}: bump its ?v="
    bad=1
  fi
done < <(git diff --name-only "$base" -- "$root/js/*.js" "$root/*.css")

if [[ $bad -eq 0 ]]; then echo "Every changed script and stylesheet has a new ?v= in App.razor."; fi
exit $bad
