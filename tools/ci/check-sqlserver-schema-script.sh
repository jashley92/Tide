#!/usr/bin/env bash
# Proves the DBA schema script the release bundle carries (tools/ci/sqlserver-schema-script.sh) does what the app's own
# migrations do, on a real SQL Server:
#   1. Generate it, as the release does, and apply it to a new, empty database with sqlcmd, as a DBA (or
#      Install-Database.ps1 -ApplySchema) would: from the file (-i), read as UTF-8 (-f i:65001; it has non-ASCII text),
#      -b stops on the first error, -I sets QUOTED_IDENTIFIER (filtered indexes), -x leaves $(...) in the script alone,
#      -X1 refuses sqlcmd's shell (!!) and editor (ED) commands and stops if one appears (the script never has one).
#   2. Apply it again: it is idempotent, so the second run must change nothing and not fail.
#   3. EF itself must see no pending migration on that database, and its __EFMigrationsHistory must hold exactly the
#      migrations in src/Tide.Migrations.SqlServer.
#   4. With a reference database (one `dotnet ef database update` migrated), its columns, indexes (key columns in order,
#      uniqueness, filter), foreign keys (columns, referenced table and columns, delete action) and defaults must match.
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
  MSYS_NO_PATHCONV=1 $SQLCMD -b -I -x -X1 -f i:65001 -d "$db" -i "$path" > "$work/apply-$1.log" 2>&1 \
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
  log "Comparing its columns, indexes, foreign keys and defaults with [$ref_db]"
  # One line per column (type, size, nullability), index (kind, uniqueness, key columns in order with direction,
  # included columns, filter), foreign key (columns, referenced table and columns, delete action) and default
  # (definition, per column: their names are system-made, so they differ between databases). -y 8000: lines aren't cut
  # at 256 characters (-W, which would trim the padding, can't go with -y).
  shape() {
    local d="[$1]"
    MSYS_NO_PATHCONV=1 $SQLCMD -b -h -1 -y 8000 -Q "SET NOCOUNT ON;
      SELECT 'column ' + t.name + '.' + c.name + ' ' + ty.name + '(' + CAST(c.max_length AS varchar(10)) + ','
             + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
             + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END
             + CASE WHEN c.is_identity = 1 THEN ' IDENTITY' ELSE '' END
      FROM $d.sys.columns c JOIN $d.sys.tables t ON t.object_id = c.object_id
      JOIN $d.sys.types ty ON ty.user_type_id = c.user_type_id
      UNION ALL
      SELECT 'index ' + t.name + '.' + i.name + ' ' + i.type_desc COLLATE DATABASE_DEFAULT
             + CASE WHEN i.is_primary_key = 1 THEN ' PK' WHEN i.is_unique_constraint = 1 THEN ' UQ' WHEN i.is_unique = 1 THEN ' UNIQUE' ELSE '' END
             + ' (' + ISNULL((SELECT STRING_AGG(CAST(kc.name AS nvarchar(max)) + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END, ', ')
                                     WITHIN GROUP (ORDER BY ic.key_ordinal)
                              FROM $d.sys.index_columns ic JOIN $d.sys.columns kc ON kc.object_id = ic.object_id AND kc.column_id = ic.column_id
                              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0), '') + ')'
             + ISNULL(' INCLUDE (' + (SELECT STRING_AGG(CAST(kc.name AS nvarchar(max)), ', ') WITHIN GROUP (ORDER BY kc.name)
                              FROM $d.sys.index_columns ic JOIN $d.sys.columns kc ON kc.object_id = ic.object_id AND kc.column_id = ic.column_id
                              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1) + ')', '')
             + ISNULL(' WHERE ' + i.filter_definition, '')
      FROM $d.sys.indexes i JOIN $d.sys.tables t ON t.object_id = i.object_id WHERE i.type > 0
      UNION ALL
      SELECT 'fk ' + t.name + '.' + f.name
             + ' (' + (SELECT STRING_AGG(CAST(pc.name AS nvarchar(max)), ', ') WITHIN GROUP (ORDER BY fc.constraint_column_id)
                       FROM $d.sys.foreign_key_columns fc JOIN $d.sys.columns pc ON pc.object_id = fc.parent_object_id AND pc.column_id = fc.parent_column_id
                       WHERE fc.constraint_object_id = f.object_id) + ')'
             + ' -> ' + rt.name
             + ' (' + (SELECT STRING_AGG(CAST(rc.name AS nvarchar(max)), ', ') WITHIN GROUP (ORDER BY fc.constraint_column_id)
                       FROM $d.sys.foreign_key_columns fc JOIN $d.sys.columns rc ON rc.object_id = fc.referenced_object_id AND rc.column_id = fc.referenced_column_id
                       WHERE fc.constraint_object_id = f.object_id) + ')'
             + ' ON DELETE ' + f.delete_referential_action_desc COLLATE DATABASE_DEFAULT
      FROM $d.sys.foreign_keys f JOIN $d.sys.tables t ON t.object_id = f.parent_object_id
      JOIN $d.sys.tables rt ON rt.object_id = f.referenced_object_id
      UNION ALL
      SELECT 'default ' + t.name + '.' + c.name + ' ' + dc.definition
      FROM $d.sys.default_constraints dc JOIN $d.sys.tables t ON t.object_id = dc.parent_object_id
      JOIN $d.sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id;" \
      | tr -d '\r' | sed 's/[[:space:]]*$//; /^$/d' | LC_ALL=C sort
  }
  # Each side to a file, its exit status checked and its contents required: sqlcmd failing the same way for both would
  # otherwise compare nothing with nothing and pass.
  for side in ref:"$ref_db" script:"$db"; do
    name="${side#*:}"; out="$work/shape-${side%%:*}.txt"
    shape "$name" > "$out" || { head -20 "$out"; fail "Couldn't read the schema of [$name]."; }
    for kind in column index fk default; do
      grep -q "^$kind " "$out" || { head -20 "$out"; fail "Read no ${kind}s from [$name]: the comparison would prove nothing."; }
    done
    log "  [$name]: $(wc -l < "$out" | tr -d ' ') columns, indexes, foreign keys and defaults"
  done
  if ! diff "$work/shape-ref.txt" "$work/shape-script.txt" > "$work/shape.diff"; then
    sed 's/^/    /' "$work/shape.diff" | head -40
    fail "The script's schema differs from the migrated one (< migrated, > script)."
  fi
fi

log "The schema script applies cleanly, twice, and leaves $(echo "$expected" | wc -l | tr -d ' ') migrations applied and none pending."
