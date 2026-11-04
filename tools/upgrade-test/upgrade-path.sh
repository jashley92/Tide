#!/usr/bin/env bash
# Upgrade-path test: prove a database created by an OLDER TIDE release upgrades in place to a newer build on real
# SQL Server, with no wipe.
#
#   1. Check out <from-ref> into a temporary worktree, build it, and start it against a fresh SQL Server database
#      (Development, so it migrates AND seeds the sample articles, briefs and actions, writing audit rows as it goes).
#   2. Stop it, then start the <to> build against the SAME database and the same data root (files and key ring). Its
#      startup applies the pending migrations and runs every startup pass against existing data, as an IIS upgrade does.
#   3. Assert: the new build came up and reports Healthy, every one of its migrations is recorded, and the articles,
#      briefs, actions and audit rows the old release wrote are still there.
#
# Usage:  tools/upgrade-test/upgrade-path.sh <from-ref> [<to-ref>]
#   <from-ref>  a release tag (e.g. v0.1.0-alpha.1) or any commit
#   <to-ref>    optional; defaults to the current working tree (including uncommitted changes)
#
# Environment:
#   TIDE_TEST_SQL         ADO.NET connection string to the SQL Server, WITHOUT Database=, e.g.
#                         "Server=localhost,21433;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False"
#   SQLCMD                command that runs sqlcmd against that server (default: "sqlcmd"), e.g.
#                         "docker exec tide-test-sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P ..."
#   UPGRADE_TEST_PORT     local port for the app under test (default 5198)
#   UPGRADE_TEST_KEEP_DB  set to 1 to keep the upgraded database afterwards (its name is printed), for inspection
set -euo pipefail

from_ref="${1:?usage: upgrade-path.sh <from-ref> [<to-ref>]}"
to_ref="${2:-}"
: "${TIDE_TEST_SQL:?set TIDE_TEST_SQL (SQL Server connection string without Database=)}"
SQLCMD="${SQLCMD:-sqlcmd}"
port="${UPGRADE_TEST_PORT:-5198}"

repo="$(git rev-parse --show-toplevel)"
work="$(mktemp -d)"
db="TideUpg_$(printf %s "$from_ref" | tr -c 'A-Za-z0-9' '_')_$$"
conn="${TIDE_TEST_SQL%;};Database=$db"
# A data root shared by both builds, as production's files and key ring persist across upgrades (webhook secrets are
# encrypted with that key ring, so the new build must read what the old one wrote).
data="$work/data"
mkdir -p "$data/files" "$data/keys" "$data/mail"
app_pid=""

log()  { printf '==> %s\n' "$*"; }
fail() { printf '::error::%s\n' "$*" >&2; exit 1; }

# MSYS_NO_PATHCONV stops Git Bash on Windows rewriting a "docker exec ... /opt/..." path; a no-op elsewhere.
sql() { MSYS_NO_PATHCONV=1 $SQLCMD -b -h -1 -W -Q "SET NOCOUNT ON; $1" | tr -d '\r' | sed '/^$/d'; }

cleanup() {
  [[ -n "$app_pid" ]] && kill "$app_pid" 2>/dev/null || true
  if [[ "${UPGRADE_TEST_KEEP_DB:-}" = "1" ]]; then log "Kept database [$db]"; else
    sql "IF DB_ID('$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END" >/dev/null 2>&1 || true
  fi
  for wt in "$work"/*/; do [[ -d "$wt" ]] && git -C "$repo" worktree remove --force "$wt" >/dev/null 2>&1 || true; done
  rm -rf "$work"
}
trap cleanup EXIT

# Build a source tree; the app is then run from its build output.
build() {
  log "Building $2"
  dotnet build "$1/src/Tide.Web" -c Release --nologo -v q >"$work/build-$2.log" 2>&1 \
    || { tail -30 "$work/build-$2.log"; fail "build of $2 failed"; }
}

# Start the app from a built source tree and wait until it answers HTTP (startup migrates and seeds BEFORE Kestrel
# listens, so answering means the schema work finished). Fails fast if the process dies.
start_app() {
  local dir="$1" label="$2" applog="$work/app-$2.log"
  local dll; dll="$(ls "$dir"/src/Tide.Web/bin/Release/net*/Tide.Web.dll | head -1)"
  log "Starting $label against [$db]"
  ( cd "$dir/src/Tide.Web" && \
    ASPNETCORE_ENVIRONMENT=Development Database__Provider=SqlServer ConnectionStrings__Default="$conn" \
    Files__RootPath="$data/files" DataProtection__KeyPath="$data/keys" Email__PickupDirectory="$data/mail" \
    exec dotnet "$dll" --urls "http://127.0.0.1:$port" ) >"$applog" 2>&1 &
  app_pid=$!
  for _ in $(seq 1 120); do
    [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/health/live" || true)" != "000" ]] && return 0
    if ! kill -0 "$app_pid" 2>/dev/null; then
      tail -40 "$applog"; app_pid=""; fail "$label exited during startup (see log above)"
    fi
    sleep 2
  done
  tail -40 "$applog"; fail "$label did not become live within 240s"
}

stop_app() {
  [[ -n "$app_pid" ]] || return 0
  kill "$app_pid" 2>/dev/null || true
  for _ in $(seq 1 20); do kill -0 "$app_pid" 2>/dev/null || break; sleep 1; done
  kill -9 "$app_pid" 2>/dev/null || true
  app_pid=""
}

# Migration IDs a source tree ships for SQL Server (the same derivation as the release manifest).
migrations_in() {
  ls "$1/src/Tide.Migrations.SqlServer/Migrations" \
    | grep -E '^[0-9]+_.*\.cs$' | grep -v '\.Designer\.cs$' | sed 's/\.cs$//' | sort
}

count() { sql "SELECT COUNT(*) FROM [$db].dbo.[$1]"; }

# --- 1. Old release creates and seeds the database -------------------------------------------------
sql "CREATE DATABASE [$db]" >/dev/null || fail "cannot create a database via SQLCMD ($SQLCMD)"
git -C "$repo" worktree add --detach "$work/from" "$from_ref" >/dev/null 2>&1 \
  || fail "cannot check out '$from_ref'"
build "$work/from" "from-$from_ref"
start_app "$work/from" "from-$from_ref"
stop_app

articles_before="$(count Articles)"; briefs_before="$(count Briefs)"; actions_before="$(count Actions)"
audit_before="$(count AuditLog)"; mig_before="$(count __EFMigrationsHistory)"
log "$from_ref installed: $mig_before migrations, $articles_before articles, $briefs_before briefs, $actions_before actions, $audit_before audit rows"
[[ "$articles_before" -gt 0 ]] || fail "$from_ref seeded no articles; the upgrade would not be exercised against data"

# --- 2. Newer build upgrades the same database in place --------------------------------------------
if [[ -n "$to_ref" ]]; then
  git -C "$repo" worktree add --detach "$work/to" "$to_ref" >/dev/null 2>&1 || fail "cannot check out '$to_ref'"
  to_dir="$work/to"; to_label="to-$to_ref"
else
  to_dir="$repo"; to_label="to-worktree"
fi
build "$to_dir" "$to_label"
start_app "$to_dir" "$to_label"
ready="$(curl -sS "http://127.0.0.1:$port/health" || true)"
stop_app
[[ "$ready" = "Healthy" ]] || { grep -iE "health|unhealthy|fail|exception" "$work/app-$to_label.log" | tail -20; \
  fail "$to_label started but /health reported '$ready'"; }

# --- 3. Assertions ---------------------------------------------------------------------------------
expected="$(migrations_in "$to_dir")"
applied="$(sql "SELECT MigrationId FROM [$db].dbo.__EFMigrationsHistory ORDER BY MigrationId")"
missing="$(comm -23 <(echo "$expected") <(echo "$applied"))"
[[ -z "$missing" ]] || fail "migrations not applied after upgrade: $(echo $missing)"

articles_after="$(count Articles)"; briefs_after="$(count Briefs)"; actions_after="$(count Actions)"; audit_after="$(count AuditLog)"
[[ "$articles_after" -eq "$articles_before" ]] || fail "article count changed across the upgrade ($articles_before -> $articles_after)"
[[ "$briefs_after" -eq "$briefs_before" ]] || fail "brief count changed across the upgrade ($briefs_before -> $briefs_after)"
[[ "$actions_after" -eq "$actions_before" ]] || fail "action count changed across the upgrade ($actions_before -> $actions_after)"
[[ "$audit_after" -ge "$audit_before" ]] || fail "audit rows lost across the upgrade ($audit_before -> $audit_after)"

log "PASS $from_ref -> ${to_ref:-working tree}: $(echo "$expected" | wc -l | tr -d ' ') migrations applied; $articles_after articles, $briefs_after briefs, $actions_after actions and $audit_after audit rows kept"
