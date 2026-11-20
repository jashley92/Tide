#!/usr/bin/env bash
# Proves the DBA schema script the release bundle carries (tools/ci/sqlserver-schema-script.sh) does what the app's own
# migrations do, on a real SQL Server:
#   1. Generate it, as the release does, and apply it to a new, empty database with sqlcmd, as a DBA (or
#      Install-Database.ps1 -ApplySchema) would: from the file (-i), read as UTF-8 (-f i:65001; it has non-ASCII text),
#      -b stops on the first error, -I sets QUOTED_IDENTIFIER (filtered indexes), -x leaves $(...) in the script alone.
#   2. Apply it again: it is idempotent, so the second run must change nothing and not fail.
#   3. EF itself must see no pending migration on that database, and its __EFMigrationsHistory must hold exactly the
#      migrations in src/Tide.Migrations.SqlServer.
#   4. With a reference database (one `dotnet ef database update` migrated), its columns and indexes must match.
#
# Usage:  tools/ci/check-sqlserver-schema-script.sh [<reference database>]
#   Run from the repository root, after `dotnet tool restore`.
# Environment:
#   TIDE_TEST_SQL  ADO.NET connection string to the SQL Server, WITHOUT Database=
#   SQLCMD         command that runs sqlcmd against that server (default: "sqlcmd"), e.g.
#                  "docker exec <container> /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P ..."
#   SQL_CONTAINER  when SQLCMD runs inside a container, its name or id: the script is copied in for sqlcmd to read
set -euo pipefail

ref_db="${1:-}"
: "${TIDE_TEST_SQL:?set TIDE_TEST_SQL (SQL Server connection string without Database=)}"
SQLCMD="${SQLCMD:-sqlcmd}"
db="TideSchemaScript_$$"
work="$(mktemp -d)"
script="$work/tide-schema-sqlserver.sql"

log()  { printf '==> %s\n' "$*"; }
fail() { printf '::error::%s\n' "$*" >&2; exit 1; }
# MSYS_NO_PATHCONV stops Git Bash on Windows rewriting a "docker exec ... /opt/..." path; a no-op elsewhere.
sql() { MSYS_NO_PATHCONV=1 $SQLCMD -b -h -1 -W -Q "SET NOCOUNT ON; $1" | tr -d '\r' | sed '/^$/d'; }
apply() {
  local path="$script"
  if [[ -n "${SQL_CONTAINER:-}" ]]; then
    path="/tmp/tide-schema-sqlserver.sql"
    docker cp "$script" "$SQL_CONTAINER:$path" >/dev/null
  fi
  MSYS_NO_PATHCONV=1 $SQLCMD -b -I -x -f i:65001 -d "$db" -i "$path" > "$work/apply-$1.log" 2>&1 \
    || { tail -30 "$work/apply-$1.log"; fail "Applying the schema script ($1 run) failed."; }
}

cleanup() {
  sql "IF DB_ID('$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

log "Generating the schema script"
bash tools/ci/sqlserver-schema-script.sh "$script"

log "Applying it to the empty database [$db], then again"
sql "CREATE DATABASE [$db];" >/dev/null
apply first
apply second

log "Checking the migration history"
expected="$(ls src/Tide.Migrations.SqlServer/Migrations | grep -E '^[0-9]+_.*\.cs$' | grep -v '\.Designer\.cs$' | sed 's/\.cs$//' | sort)"
applied="$(sql "SELECT MigrationId FROM [$db].dbo.__EFMigrationsHistory ORDER BY MigrationId;" | sort)"
if [[ "$expected" != "$applied" ]]; then
  diff <(echo "$expected") <(echo "$applied") | sed 's/^/    /' || true
  fail "__EFMigrationsHistory doesn't match the migrations (< only in the code, > only in the database)."
fi
# EF's own view: the history matching by name isn't the whole story if the model's migrations assembly differs.
pending="$(TIDE_MIGRATIONS_PROVIDER=SqlServer dotnet ef migrations list --no-build --configuration Release \
  --project src/Tide.Migrations.SqlServer --startup-project src/Tide.Web --context AppDbContext \
  --connection "${TIDE_TEST_SQL%;};Database=$db" | grep -F '(Pending)' || true)"
[[ -z "$pending" ]] || { echo "$pending" | sed 's/^/    /'; fail "EF sees pending migrations after the script ran."; }

if [[ -n "$ref_db" ]]; then
  log "Comparing its columns and indexes with [$ref_db]"
  shape() {
    sql "SELECT t.name + '.' + c.name + ' ' + ty.name + '(' + CAST(c.max_length AS varchar) + ',' + CAST(c.precision AS varchar)
                + ',' + CAST(c.scale AS varchar) + ')' + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END
         FROM [$1].sys.columns c JOIN [$1].sys.tables t ON t.object_id = c.object_id
         JOIN [$1].sys.types ty ON ty.user_type_id = c.user_type_id
         UNION ALL
         SELECT t.name + ' index ' + i.name + CASE WHEN i.is_unique = 1 THEN ' unique' ELSE '' END + ISNULL(' ' + i.filter_definition, '')
         FROM [$1].sys.indexes i JOIN [$1].sys.tables t ON t.object_id = i.object_id WHERE i.name IS NOT NULL
         UNION ALL
         SELECT t.name + ' fk ' + f.name + ' ' + f.delete_referential_action_desc
         FROM [$1].sys.foreign_keys f JOIN [$1].sys.tables t ON t.object_id = f.parent_object_id;" | sort
  }
  if ! diff <(shape "$ref_db") <(shape "$db") > "$work/shape.diff"; then
    sed 's/^/    /' "$work/shape.diff" | head -40
    fail "The script's schema differs from the migrated one (< migrated, > script)."
  fi
fi

log "The schema script applies cleanly, twice, and leaves $(echo "$expected" | wc -l | tr -d ' ') migrations applied and none pending."
