#!/usr/bin/env bash
# Writes the SQL Server schema script a DBA applies on a DbaApplies install (deploy/Install-Database.ps1 -ApplySchema,
# or by hand with sqlcmd): every migration in src/Tide.Migrations.SqlServer, idempotent, so it brings an empty database
# or one at any earlier release up to this build and is a no-op on one already there. The release bundle carries it at
# deploy/sql/tide-schema-sqlserver.sql; the sql-server CI job generates it the same way and applies it to a real server.
#
# The script has GO batch separators, so it is run with sqlcmd or Invoke-Sqlcmd, not as one batch.
#
# Usage:  tools/ci/sqlserver-schema-script.sh <output.sql> [extra dotnet-ef options, e.g. --no-build]
#   Needs the local tools restored (dotnet tool restore: dotnet-ef from .config/dotnet-tools.json).
set -euo pipefail

out="${1:?usage: sqlserver-schema-script.sh <output.sql> [dotnet-ef options]}"
shift
mkdir -p "$(dirname "$out")"

# The design-time factory (AppDbContextFactory) builds the SQL Server model only when told to; unset, it is SQLite's.
TIDE_MIGRATIONS_PROVIDER=SqlServer dotnet ef migrations script --idempotent --configuration Release \
  --project src/Tide.Migrations.SqlServer --startup-project src/Tide.Web --context AppDbContext \
  --output "$out" "$@"

# Every migration must be in it: a script that silently lost some would leave a DBA's database behind the app.
ids="$(ls src/Tide.Migrations.SqlServer/Migrations | grep -E '^[0-9]+_.*\.cs$' | grep -v '\.Designer\.cs$' | sed 's/\.cs$//')"
missing=0
for id in $ids; do
  grep -q "N'$id'" "$out" || { echo "::error::$out doesn't apply migration $id."; missing=1; }
done
[[ $missing -eq 0 ]] || exit 1
echo "Wrote $out: $(echo "$ids" | wc -l | tr -d ' ') migrations, $(wc -c < "$out" | tr -d ' ') bytes."
