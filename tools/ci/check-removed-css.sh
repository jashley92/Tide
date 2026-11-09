#!/usr/bin/env bash
# No class the markup uses loses its style: a change that drops the last rule (in app.css or palette.css) for a tide-*
# class that a Razor component, a C# file or the browser JS still uses fails, naming the class and where it's used.
# Its element would fall back to the browser's defaults, which no test reading the rendered HTML sees: v1.10.0's meeting
# quick add stacked its five controls one per line after a stylesheet edit dropped .tide-quick-add. A class built at
# run time from a prefix ("tide-badge-" + tone) counts as used when the markup holds that prefix.
#
# Usage: tools/ci/check-removed-css.sh [base]   base: the ref to compare with (default origin/main)
set -euo pipefail
export LC_ALL=C.UTF-8   # grep -P needs a UTF-8 locale (Git Bash's default isn't one)

base="${1:-origin/main}"
sheets=(src/Tide.Web/wwwroot/app.css src/Tide.Web/wwwroot/palette.css)
code=(src/Tide.Web/Components src/Tide.Web/wwwroot/js)

# A push that starts a branch has no "before" (all zeros): nothing to compare with.
if ! git rev-parse --verify -q "$base^{commit}" >/dev/null; then echo "No base to compare the stylesheets with ($base); skipped."; exit 0; fi

classes() { grep -oE '\.tide-[A-Za-z0-9_-]+' | sed 's/^\.//' | sort -u; }
before="$(for s in "${sheets[@]}"; do git show "$base:$s" 2>/dev/null || true; done | classes)"
after="$(cat "${sheets[@]}" | classes)"
dropped="$(comm -23 <(echo "$before") <(echo "$after"))"

# grep's 1 is "no match"; anything else (2: it couldn't search) fails the check rather than passing it quietly.
search() {
  local found status
  set +e
  found="$(grep -rlP --include='*.razor' --include='*.cs' --include='*.js' "$1" "${code[@]}")"
  status=$?
  set -e
  if [[ $status -gt 1 ]]; then echo "grep couldn't search ${code[*]} (exit $status)" >&2; exit 2; fi
  echo "$found"
}

# The prefixes class names are built from at run time: "tide-badge-" + tone, $"tide-level-{level}".
prefixes="$(grep -rhoP --include='*.razor' --include='*.cs' --include='*.js' '["'"'"'`]tide-[a-z0-9-]+-(?=["'"'"'`{$])' "${code[@]}" \
            | sed 's/^.//' | sort -u || true)"

bad=0
for c in $dropped; do
  uses="$(search "(?<![\\w-])${c}(?![\\w-])")"   # the whole class, not a prefix of a longer one
  if [[ -z "$uses" ]]; then
    for p in $prefixes; do [[ "$c" == "$p"* ]] && uses="$(search "$p")" && break; done
  fi
  if [[ -n "$uses" ]]; then
    echo "::error::.$c lost its last rule in ${sheets[*]}, but it's still used: $(paste -sd, - <<<"$uses")"
    bad=1
  fi
done

[[ $bad -eq 0 ]] || exit 1
echo "OK: no class the code uses lost its style ($(wc -w <<<"$dropped") dropped, none still used)."
