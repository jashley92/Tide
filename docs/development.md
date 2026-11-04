# Developing TIDE

## Run and test

```bash
dotnet run --project src/Tide.Web --launch-profile http
```

Then open <http://localhost:5110>. Development uses SQLite (`src/Tide.Web/App_Data/tide.db`) and the dev sign-in.
Email is written as `.eml` files to `src/Tide.Web/App_Data/mail` (`Email:PickupDirectory`) instead of going to a relay;
open one in Outlook to check how a brief looks there.
Delete the database to start over: sample data is seeded only into an empty one.

```bash
dotnet test
```

- `tests/Tide.UnitTests` holds pure logic: edition cutting, the email and SharePoint renderings (the TLP leak tests),
  the metric rules, the SLA rules, the hunt query dialects (golden files in `Golden/`; run with `TIDE_UPDATE_GOLDEN=1`
  to accept a deliberate change, then review the diff), feed and CVE parsing (including a refused XXE), URL
  canonicalization and title matching, indicator extraction, the org clock, Markdown safety and permissions.
- `tests/Tide.IntegrationTests` runs the services against in-memory SQLite with the real migrations (`TestDb`), and
  the whole app over HTTP (`TideApp`, and `ReaderApp` signed in as a Reader). Sends go to `FakeEmailSender`, or
  through the real `EmailSender` to `FakeSmtpServer` on loopback; outbound GETs (feeds, CVE services) go to `FakeHttp`,
  which answers from canned responses. Test doubles are hand-written; there's no mocking
  library.
- A test of code whose queries SQL Server could run differently from SQLite (ordering and ties, `DateTimeOffset`
  comparisons, case, `ExecuteUpdate`, subqueries, `Contains`) runs on both. Make it a theory over the providers and
  take the provider's database first:

  ```csharp
  [Theory]
  [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
  public async Task Something_Holds(string provider)
  {
      _db = _db.On(provider);
      ...
  }
  ```

  On SQLite that's the class's own database; on SQL Server it's a throwaway `tide_test_<guid>` database with the schema
  made from the model (`EnsureCreated`: the migrations are proven by CI applying them), dropped afterwards. A test that
  only makes sense on SQL Server is a `[SqlServerFact]`; one that needs the whole app on SQL Server uses
  `SqlServerTideApp`, which migrates its own database at startup, as production does (`EveryPage_RendersOnSqlServer`
  reads every page that way). The SQL Server rows run only when `TIDE_TEST_SQL` holds a server's connection string with
  no database in it, so the default run needs nothing installed and never passes one vacuously; CI's `sql-server` job
  runs them on every change. To run them locally, start a throwaway container and run just those rows against it:

  ```bash
  docker run -d --name tide-test-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<a password>' -p 127.0.0.1:21433:1433 mcr.microsoft.com/mssql/server:2022-latest
  ```

  ```bash
  TIDE_TEST_SQL="Server=127.0.0.1,21433;User Id=sa;Password=<the password>;TrustServerCertificate=True;Encrypt=False" \
    dotnet test tests/Tide.IntegrationTests --filter "DisplayName~SqlServer"
  ```

  Order what you assert on (SQL Server returns rows in no particular order, and ties fall either way) and don't rely
  on case.
- Name tests `Method_State_Expectation`.

## Changing the schema

Add a migration to **both** sets, from the repository root:

```bash
dotnet ef migrations add <Name> --project src/Tide.Infrastructure --startup-project src/Tide.Web --context AppDbContext --output-dir Persistence/Migrations
```

```bash
TIDE_MIGRATIONS_PROVIDER=SqlServer dotnet ef migrations add <Name> --project src/Tide.Migrations.SqlServer --startup-project src/Tide.Web --context AppDbContext --output-dir Migrations
```

A migration that has shipped in a release tag is never edited, removed or regenerated: databases installed from that
release would stop upgrading. CI enforces this (`tools/ci/check-migrations-immutable.sh`) and fails when the model
has changes no migration covers.

CI also upgrades real databases. Its `sql-server` job installs each of the three newest releases, and the first, into a
fresh SQL Server database (migrated and seeded with the sample data). It then starts the build under test against the
same database and requires it to come up Healthy, with every migration recorded and the old release's articles, briefs,
actions and audit rows intact (`tools/upgrade-test/upgrade-path.sh`). To run it locally from a release to your working
tree, against the container above:

```bash
TIDE_TEST_SQL="Server=127.0.0.1,21433;User Id=sa;Password=<the password>;TrustServerCertificate=True;Encrypt=False"   SQLCMD="docker exec tide-test-sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P <the password>"   bash tools/upgrade-test/upgrade-path.sh v0.1.0-alpha.1
```

On Windows, if the app can't listen ("access permissions" on bind), its port is in a range Windows has reserved
(`netsh interface ipv4 show excludedportrange protocol=tcp`): set `UPGRADE_TEST_PORT` to one outside them.

## Conventions

- **Colours.** They come from the `--tide-*` tokens in `wwwroot/app.css`, which are defined for both themes. Never
  hard-code a hex colour in a page. Email is the exception (clients can't read CSS variables): its colours are in
  `EmailComposer.Palette`, mirroring the light tokens. Change both together.
- **Scripts.** The CSP allows only `script-src 'self'`, so scripts are external files under `wwwroot/js`, wired by
  delegated listeners. Bump the `?v=` on a script's tag in `App.razor` when you change it.
- **Permissions.** Services check them; pages also hide what a person can't do. A new capability gets a
  `Permission`, a policy (automatic from `Policies.Required`) and grants in `RoleDefinitions`.
- **Configuration.** Every key in `appsettings.json` has a `"//"` line saying what it does.
- **Docs.** They change in the same commit as the code they describe.
