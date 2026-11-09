using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Content;
using Tide.Application.Distribution;
using Tide.Application.Files;
using Tide.Application.Intake;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Application.Rotation;
using Tide.Application.Scorecard;
using Tide.Application.Settings;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;

namespace Tide.IntegrationTests;

/// <summary>A signed-in user with the given roles' permissions.</summary>
public sealed class FakeUser(params AppRole[] roles) : ICurrentUser
{
    public bool IsAuthenticated { get; set; } = true;
    public string UserId { get; set; } = "S-1-5-21-TEST-1";
    public string DisplayName { get; set; } = "Test Analyst";
    public string? UserPrincipalName => "test@example.com";
    public string? Email { get; set; } = "test@example.com";
    public IReadOnlySet<AppRole> Roles { get; set; } = roles.ToHashSet();
    public IReadOnlySet<Permission> Permissions => Application.Security.RoleDefinitions.PermissionsFor(Roles);
    public bool Has(Permission permission) => Permissions.Contains(permission);
}

/// <summary>Records what would have been emailed, and answers with <see cref="Result"/>.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public List<OutgoingEmail> Sent { get; } = [];
    public bool Enabled { get; set; } = true;
    public EmailSendResult Result { get; set; } = new(EmailSendStatus.Sent);

    /// <summary>A fault to throw instead of sending this email (null sends it).</summary>
    public Func<OutgoingEmail, Exception?>? Fault { get; set; }

    public Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        if (Fault?.Invoke(email) is { } fault) throw fault;
        lock (Sent) Sent.Add(email);   // sends can overlap in a test of concurrent decisions
        return Task.FromResult(Result);
    }
}

/// <summary>Holds each email at the relay until <see cref="Release"/> is set, for a test of what happens while a send is out.</summary>
public sealed class HeldEmailSender : IEmailSender
{
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;
    public int Calls => _calls;
    public bool Enabled => true;

    public async Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        Reached.TrySetResult();
        await Release.Task;
        return new EmailSendResult(EmailSendStatus.Sent);
    }
}

/// <summary>A relay that faults mid-send, so whether the email went is unknown.</summary>
public sealed class ThrowingEmailSender : IEmailSender
{
    public bool Enabled => true;
    public Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct = default) => throw new ObjectDisposedException("SmtpClient");
}

/// <summary>A host whose content root is the temp folder.</summary>
public sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "Tide";
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}

/// <summary>Answers GETs from a table of canned responses (by address prefix) and records what was asked.</summary>
public sealed class FakeHttp : IOutboundHttp
{
    public Dictionary<string, HttpFetch> Responses { get; } = [];
    public List<(string Url, OutboundRequest? Request)> Requests { get; } = [];

    /// <summary>Addresses whose fetch throws, as a fault the client didn't turn into a result would.</summary>
    public Dictionary<string, Exception> Throws { get; } = [];

    public void Set(string url, string body, string? etag = null) =>
        Responses[url] = new HttpFetch(FetchStatus.Ok, System.Text.Encoding.UTF8.GetBytes(body), etag);

    /// <summary>Run once, while the next fetch is under way, for a test of something that happens meanwhile.</summary>
    public Func<Task>? During { get; set; }

    public Task<HttpFetch> GetAsync(string url, OutboundRequest? request = null, CancellationToken ct = default)
    {
        if (During is { } during)
        {
            During = null;
            return AfterAsync(during, url, request, ct);
        }
        Requests.Add((url, request));
        if (Throws.TryGetValue(url, out var fault)) throw fault;
        var hit = Responses.Where(r => url.StartsWith(r.Key, StringComparison.Ordinal)).OrderByDescending(r => r.Key.Length).Select(r => r.Value).FirstOrDefault();
        return Task.FromResult(hit ?? new HttpFetch(FetchStatus.Failed, Error: "No canned response for " + url));
    }

    private async Task<HttpFetch> AfterAsync(Func<Task> during, string url, OutboundRequest? request, CancellationToken ct)
    {
        await during();
        return await GetAsync(url, request, ct);
    }
}

/// <summary>"Protects" a secret reversibly, so a test can sign as the receiver would.</summary>
public sealed class FakeSecrets : Tide.Application.Api.ISecretProtector
{
    public string Protect(string secret) => "p:" + secret;
    public string? Unprotect(string protectedSecret) => protectedSecret.StartsWith("p:", StringComparison.Ordinal) ? protectedSecret[2..] : null;
}

/// <summary>Records each webhook post and answers with <see cref="Status"/>.</summary>
public sealed class FakeWebhookPoster : Tide.Application.Api.IWebhookPoster
{
    public int Status { get; set; } = 200;
    public List<(string Url, string Body, IReadOnlyDictionary<string, string> Headers)> Posted { get; } = [];

    /// <summary>Run while a post is with the receiver, for a test of something that happens during a run.</summary>
    public Func<Task>? During { get; set; }

    public async Task<(int? Status, string? Error)> PostAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        Posted.Add((url, body, headers));
        if (During is { } during) await during();
        return (Status, Status is >= 200 and < 300 ? null : $"The address answered {Status}.");
    }
}

/// <summary>An AI provider that answers with what a test gives it, and records what it was asked.</summary>
public sealed class FakeAssistant : Tide.Application.Assist.IThreatIntelAssistant
{
    public bool Configured { get; set; } = true;
    public string Provider => Tide.Application.Assist.AiOptions.AzureOpenAI;
    public string Model => "fake-model";

    /// <summary>The next answer's JSON; null answers with <see cref="Error"/>.</summary>
    public string? Json { get; set; } = "{}";
    public string Error { get; set; } = "The provider failed.";

    /// <summary>What a web lookup finds; null finds nothing.</summary>
    public string? Found { get; set; }

    public List<Tide.Application.Assist.AssistRequest> Asked { get; } = [];
    public List<string> LookedUp { get; } = [];

    public Task<Tide.Application.Assist.AssistAnswer> AnswerAsync(Tide.Application.Assist.AssistRequest request, CancellationToken ct = default)
    {
        Asked.Add(request);
        return Task.FromResult(Json is null ? new Tide.Application.Assist.AssistAnswer(null, Error) : new Tide.Application.Assist.AssistAnswer(Json));
    }

    public Task<Tide.Application.Assist.FoundArticle> FindArticleAsync(string url, string? title, CancellationToken ct = default)
    {
        LookedUp.Add(url);
        return Task.FromResult(new Tide.Application.Assist.FoundArticle(Found, Found is null ? "not found" : null));
    }
}

/// <summary>Files kept in memory.</summary>
public sealed class MemoryFileStore : IFileStore
{
    public Dictionary<Guid, byte[]> Files { get; } = [];

    public async Task WriteAsync(Guid id, Stream content, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Files[id] = ms.ToArray();
    }

    public Stream? OpenRead(Guid id) => Files.TryGetValue(id, out var b) ? new MemoryStream(b) : null;

    public void Delete(Guid id) => Files.Remove(id);
}

/// <summary>
/// An in-memory SQLite database with the real model and migrations, and the application services wired to it as the
/// app wires them (a short-lived context per operation, the audit interceptor on each). A test of code whose queries
/// SQL Server could run differently (ordering, dates, case, bulk updates) takes its provider as a theory row from
/// <see cref="Providers"/> and passes it in: on <see cref="SqlServer"/> it gets a throwaway database of its own on the
/// server <c>TIDE_TEST_SQL</c> names, dropped afterwards. Without a server it runs once, on SQLite, and never passes
/// vacuously as "SQL Server". Run the SQL Server rows alone with <c>--filter "DisplayName~SqlServer"</c>.
/// </summary>
public sealed class TestDb : IAppDbContextFactory, IDisposable
{
    public const string Sqlite = "Sqlite";
    public const string SqlServer = "SqlServer";

    /// <summary>The providers to run on: SQLite always, SQL Server when a server is configured.</summary>
    public static TheoryData<string> Providers
    {
        get
        {
            var rows = new TheoryData<string> { Sqlite };
            if (SqlServerFactAttribute.Server is not null) rows.Add(SqlServer);
            return rows;
        }
    }

    // On SQLite the database lives as long as this connection is open. Shared, every context uses it; per context, it only
    // keeps a named in-memory database alive, and each context opens its own connection to it. Null on SQL Server.
    private readonly SqliteConnection? _connection;
    private readonly string? _perContext;
    private readonly DbContextOptions<AppDbContext> _options;

    // This test's own SQL Server database; null on SQLite.
    private readonly string? _sqlServer;

    // How it was made, for On to make another the same way.
    private readonly bool _retrying;
    private readonly bool _connectionPerContext;

    public FakeUser User { get; } = new(AppRole.Analyst);
    public MemoryFileStore Store { get; } = new();

    /// <summary>What saves tell open pages, as the app's change interceptor publishes it.</summary>
    public ChangeFeed Feed { get; } = new();
    public ChangeOrigin Origin { get; } = new();

    /// <param name="retrying">False for a test that opens transactions itself to exercise the interceptors.</param>
    /// <param name="connectionPerContext">
    /// True for a test that runs services at the same moment: a SQLite connection isn't safe to use from two threads, so
    /// shared it fails at random under load ("unable to delete/modify user-function due to active statements"), where
    /// production's contexts each have their own.
    /// </param>
    /// <param name="provider">
    /// <see cref="Sqlite"/>, migrated as the app's development database is; or <see cref="SqlServer"/>, its schema made from
    /// the model (EnsureCreated): the SQL Server migrations are proven by CI applying them, so a test needn't replay them.
    /// Each SQL Server context has its own connection, so <paramref name="connectionPerContext"/> doesn't apply there.
    /// </param>
    public TestDb(bool retrying = true, bool connectionPerContext = false, string provider = Sqlite)
    {
        (_retrying, _connectionPerContext) = (retrying, connectionPerContext);
        if (provider == SqlServer) _sqlServer = SqlServerFactAttribute.NewDatabase();
        else if (provider == Sqlite)
        {
            _perContext = connectionPerContext ? $"Data Source=tide-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared" : null;
            _connection = new SqliteConnection(_perContext ?? "Data Source=:memory:");
            _connection.Open();
        }
        else throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        // Retrying, as production's SQL Server is (EnableRetryOnFailure): a transaction the code opens itself must then run
        // inside the execution strategy, and one that doesn't fails here as it would there.
        _options = Configure(new DbContextOptionsBuilder<AppDbContext>(), retrying)
            .AddInterceptors(new AuditInterceptor(User, TimeProvider.System), new ChangeInterceptor(Feed, User, Origin), new HookInterceptor(this), new ColumnLengthCheck(), Commands)
            .Options;
        if (_connection is not null)
        {
            lock (MigratedSqlite) MigratedSqlite.Value.BackupDatabase(_connection);
            return;
        }
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }

    // The SQLite schema, migrated once per run and copied into each test's database (SQLite's backup API copies the
    // tables, the migrations history and any rows the migrations insert). Replaying every migration for each test was
    // most of what a service test cost. The copy is the migrated database itself, so a test still runs on exactly the
    // schema the real migrations make. Locked, because a SqliteConnection isn't safe to use from two threads.
    private static readonly Lazy<SqliteConnection> MigratedSqlite = new(() =>
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
        return connection;
    });

    /// <summary>
    /// The database for a provider theory's row, in place of a test class's default (SQLite) one: on <see cref="Sqlite"/>
    /// this one; otherwise a new one on that provider, made as this one was and signed in as the same user, and this one
    /// disposed. Call it first, before anything is written: <c>_db = _db.On(provider);</c>
    /// </summary>
    public TestDb On(string provider)
    {
        if (provider == Sqlite && _sqlServer is null) return this;
        var db = new TestDb(_retrying, _connectionPerContext, provider);
        (db.User.UserId, db.User.DisplayName, db.User.Roles, db.User.IsAuthenticated) = (User.UserId, User.DisplayName, User.Roles, User.IsAuthenticated);
        Dispose();
        return db;
    }

    /// <summary>Every SQL command the services run, for a test that counts queries or reads their text.</summary>
    public CommandLog Commands { get; } = new();

    /// <summary>A context with no audit or change interceptors, for seeding a lot of rows quickly.</summary>
    public AppDbContext PlainContext() => new(Configure(new DbContextOptionsBuilder<AppDbContext>(), retrying: false).Options);

    private DbContextOptionsBuilder<AppDbContext> Configure(DbContextOptionsBuilder<AppDbContext> b, bool retrying)
    {
        if (_sqlServer is { } sql)
            return b.UseSqlServer(sql, o => { if (retrying) o.ExecutionStrategy(d => new RetryingStrategy(d)); });
        return _perContext is { } cs
            ? b.UseSqlite(cs, o => { if (retrying) o.ExecutionStrategy(d => new RetryingStrategy(d)); })
            : b.UseSqlite(_connection!, o => { if (retrying) o.ExecutionStrategy(d => new RetryingStrategy(d)); });
    }

    public sealed class CommandLog : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private readonly List<string> _sql = [];

        public int Count { get; private set; }

        /// <summary>The commands since the last <see cref="Reset"/>, when <see cref="Record"/> is on.</summary>
        public IReadOnlyList<string> Sql { get { lock (_sql) return _sql.ToList(); } }

        public bool Record { get; set; }

        public void Reset()
        {
            Count = 0;
            lock (_sql) _sql.Clear();
        }

        private (Func<string, bool> Matches, Action Run)? _before;

        /// <summary>Runs <paramref name="hook"/> once, just before the next command whose text <paramref name="matches"/>,
        /// for a test that needs a colleague's change to land between a service's read and its write (a race).</summary>
        public void Before(Func<string, bool> matches, Action hook) => _before = (matches, hook);

        private void Note(System.Data.Common.DbCommand command)
        {
            Count++;
            if (Record) lock (_sql) _sql.Add(command.CommandText);
            Hook(command);
        }

        private void Hook(System.Data.Common.DbCommand command)
        {
            if (_before is { } before && before.Matches(command.CommandText))
            {
                _before = null;
                before.Run();
            }
        }

        // Not counted (the counts are of reads); only the race hook.
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Hook(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Note(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            Note(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object>> ScalarExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Note(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Runs before each save, for a test that needs something to happen at that exact moment (a race).</summary>
    public Action<DbContext>? BeforeSave { get; set; }

    private sealed class HookInterceptor(TestDb db) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context) db.BeforeSave?.Invoke(context);
            return ValueTask.FromResult(result);
        }
    }

    // SQLite keeps a string of any length, and SQL Server refuses one longer than its column ("String or binary data would
    // be truncated"), so a service that can write past a column passed here and failed in production. Every save is
    // checked against the model's lengths, after the audit and change interceptors have added their rows.
    private sealed class ColumnLengthCheck : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> SavingChanges(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
        {
            Check(eventData.Context);
            return result;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Check(eventData.Context);
            return ValueTask.FromResult(result);
        }

        private static void Check(DbContext? context)
        {
            if (context is null) return;
            foreach (var entry in context.ChangeTracker.Entries())
            {
                if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
                foreach (var p in entry.Properties)
                    if (p.CurrentValue is string s && p.Metadata.GetMaxLength() is { } max && s.Length > max && (entry.State == EntityState.Added || p.IsModified))
                        throw new InvalidOperationException(
                            $"{entry.Metadata.DisplayName()}.{p.Metadata.Name} is {s.Length} characters and its column holds {max}: SQL Server would refuse the save.");
            }
        }
    }

    public AppDbContext Context() => new(_options);

    /// <summary>
    /// Sets an article's earlier free-text "Threat actors" (Article.Actors), as an install from before the adversary
    /// picker replaced the box has it: the editor no longer writes it.
    /// </summary>
    public async Task SetEarlierActorsAsync(Guid articleId, string? actors)
    {
        await using var db = Context();
        var article = await db.Articles.FirstAsync(a => a.Id == articleId);
        article.Actors = actors;
        await db.SaveChangesAsync();
    }

    public Task<IAppDbContext> CreateAsync(CancellationToken ct = default) => Task.FromResult<IAppDbContext>(Context());

    public OrgClock Clock { get; } = new(Options.Create(new OrgOptions()));

    /// <summary>The latest day whose brief can be sent now: today on a business day, else the last business day. A brief
    /// isn't sent before its day, and on a weekend the current brief day is the coming Monday.</summary>
    public DateOnly SendDay => SendDayFor(Clock);

    public static DateOnly SendDayFor(OrgClock clock) =>
        clock.Days.IsWorkingDay(clock.Today) ? clock.Today : clock.Days.Previous(clock.Today);

    /// <summary>The meetings going on, as the services see them (a Visitor's reads go through only from a seat in one).</summary>
    public IMeetingsInProgress? Live { get; set; }

    public ScreenshotService Screenshots() => new(this, User, Store, Options.Create(new FileStoreOptions { MaxImageMB = 1 }), Live);

    public ArticleService Articles() => new(this, User, Screenshots(), Clock);

    public BriefService Briefs() => new(this, User, Clock, Rotation(), Live);

    public MeetingService Meetings(TimeProvider? time = null) => new(this, User, time ?? TimeProvider.System, Live);

    public RotationService Rotation(OrgClock? clock = null) =>
        new(this, User, clock ?? Clock, Email, Options.Create(new AppOptions { BaseUrl = "https://tide.example.org" }), EmailTemplates);

    /// <summary>The email templates saved in Administration, as the app keeps them (the reminders and rotation read it).</summary>
    public EmailTemplateStore EmailTemplates { get; } = new(Options.Create(new AppOptions { BaseUrl = "https://tide.example.org" }));

    /// <summary>The standing copies, read again after each save (the service's own).</summary>
    public EmailCopyStore EmailCopies { get; } = new();

    public EmailTemplateService EmailTemplateAdmin(string? from = "tide@example.org") =>
        new(this, User, EmailTemplates, Email, Clock, Options.Create(new AppOptions { BaseUrl = "https://tide.example.org" }), TimeProvider.System,
            EmailCopies, new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:From"] = from }).Build());

    public ScorecardService Scorecard() => new(this, User);

    public MetricsService Metrics() => new(this, User, meetings: Live);

    public Tide.Application.Stack.StackService Stack() => new(this, User, Clock, Settings(), Live);

    public MetricImportService MetricImports(OrgClock? clock = null) => new(this, User, clock ?? Clock, TimeProvider.System);

    public DistributionSettings Settings() => new(this, User, Options.Create(new DistributionOptions()), Live);

    public FakeEmailSender Email { get; } = new();

    public TeamService Teams(ReminderOptions? options = null) => new(this, User, new StaticOptionsMonitor<ReminderOptions>(options ?? ReminderOptions));

    public SlaSettings Sla() => new(this, User, Options.Create(new SlaOptions()));

    public HuntQueryBuilder Queries() => new(this, User);

    public ActionService Actions() => new(this, User, Sla(), Clock, Queries(), Screenshots(), Articles(), Live, Notices());

    /// <summary>Hand-offs and splits, telling teams by <see cref="Email"/> with links to tide.example.org.</summary>
    public HandoffService Handoffs() => new(this, User, Clock, Notices());

    public HandoffNotices Notices() => new(Clock, Email, Options.Create(new AppOptions { BaseUrl = "https://tide.example.org" }), EmailTemplates);

    public PlaybookService Playbooks() => new(this, User);
    public SavedQueryService SavedQueries() => new(this, User);
    public OutreachTemplateService OutreachTemplates() => new(this, User);

    /// <summary>Outreach emails on actions, with the reminder settings given (or <see cref="ReminderOptions"/>) and links to tide.example.org.</summary>
    public OutreachService Outreach(ReminderOptions? options = null, OrgClock? clock = null) =>
        new(this, User, clock ?? Clock, Actions(), Options.Create(new AppOptions { BaseUrl = "https://tide.example.org" }),
            new StaticOptionsMonitor<ReminderOptions>(options ?? ReminderOptions));

    public CardRuleService CardRules() => new(this, User);

    public FakeHttp Http { get; } = new();

    public FakeAssistant Ai { get; } = new();

    public Tide.Application.Assist.AssistService Assist(Tide.Application.Assist.AiOptions? options = null) =>
        new(this, User, Ai, Http, new StaticOptionsMonitor<Tide.Application.Assist.AiOptions>(options ?? new Tide.Application.Assist.AiOptions()), Stack(), Scorecard(),
            new StaticOptionsMonitor<IntakeOptions>(new IntakeOptions()));

    public IntakeService Intake(IntakeOptions? options = null, TimeProvider? time = null) =>
        new(this, User, Http, Articles(), new StaticOptionsMonitor<IntakeOptions>(options ?? new IntakeOptions { Enabled = true, MinPollMinutes = 5 }),
            time ?? TimeProvider.System, polls: Polls);

    /// <summary>The sources being polled, shared by every <see cref="Intake"/> as the app's singleton is.</summary>
    public SourcePollGuard Polls { get; } = new();

    public Tide.Application.Api.ApiTokenService ApiTokens(TimeProvider? time = null) => new(this, User, time ?? TimeProvider.System);

    public FakeWebhookPoster Posts { get; } = new();

    public Tide.Application.Api.WebhookService Webhooks(TimeProvider? time = null) =>
        new(this, User, new FakeSecrets(), Posts, Clock, Options.Create(new AppOptions { BaseUrl = "https://tide.example.org" }), time ?? TimeProvider.System,
            Settings());

    public Tide.Application.Adversaries.AdversaryService Adversaries() => new(this, User, Clock, Http);

    public Tide.Application.Api.ApiService Api() => new(this, User, Settings(), Actions(), Stack(), Metrics(), Intake(), ActionFiles());

    /// <summary>The files on actions, with the limits the comments use (1 MB for an image, 25 MB for any file) and the zip setting given.</summary>
    public ActionFileService ActionFiles(ZipDownloads zip = ZipDownloads.RiskyOnly) =>
        new(this, User, Store, new StaticOptionsMonitor<FileStoreOptions>(new FileStoreOptions { MaxImageMB = 1, MaxEvidenceMB = 2, ZipDownloads = zip }));

    public ReadingService Reading(IntakeOptions? options = null, TimeProvider? time = null) =>
        new(this, User, Http, new StaticOptionsMonitor<IntakeOptions>(options ?? new IntakeOptions()), time ?? TimeProvider.System);

    public CveService Cves(CveOptions? options = null, TimeProvider? time = null) =>
        new(this, User, Http, new StaticOptionsMonitor<CveOptions>(options ?? new CveOptions { Enabled = true, NvdDelayMs = 0 }),
            time ?? TimeProvider.System, CveLog, Live);

    /// <summary>The CVE runs, shared by every <see cref="Cves"/> as the app's singleton is.</summary>
    public CveRunLog CveLog { get; } = new();

    public StoryService Stories() => new(this, User);

    public Tide.Application.Search.SearchService Search() => new(this, User);

    public Tide.Application.Trends.TrendsService Trends() => new(this, User, Clock);
    public Tide.Application.Trends.TeamScorecardService TeamScorecard(OrgClock? clock = null) => new(this, User, clock ?? Clock);

    public Tide.Application.Exports.ExportService Exports() => new(this, User);

    public Tide.Application.Search.RelatedService Related(OrgClock? clock = null) => new(this, User, clock ?? Clock);

    /// <summary>The reminder settings an administrator saved, as the notification choices read them.</summary>
    public ReminderOptions ReminderOptions { get; } = new();

    public Tide.Application.Preferences.UserPreferenceService Preferences() => new(this, User, TimeProvider.System);

    public Tide.Application.Preferences.NotificationPreferenceService NotificationPrefs(ReminderOptions? options = null) =>
        new(this, User, Preferences(), new StaticOptionsMonitor<ReminderOptions>(options ?? ReminderOptions));

    public ReminderService Reminders(ReminderOptions? options = null, OrgClock? clock = null, ReminderLog? log = null) =>
        new(this, Email, clock ?? Clock, new StaticOptionsMonitor<ReminderOptions>(options ?? new ReminderOptions()),
            Options.Create(new AppOptions { BaseUrl = "https://tide.example.org" }), log ?? new ReminderLog(), User, Rotation(clock), EmailTemplates);

    /// <summary>The distribution options the service reads (RequireSharePoint), changed by a test as an administrator would.</summary>
    public DistributionOptions DistributionOptions { get; } = new();

    public DistributionService Distribution(IEmailSender? sender = null, ICurrentUser? user = null) => new(this, user ?? User, Store, sender ?? Email, Settings(), Metrics(),
        new BriefRenderer(new MarkdownService()), Clock, Options.Create(new AppOptions { BaseUrl = "https://tide.example.org" }), Published,
        new StaticOptionsMonitor<DistributionOptions>(DistributionOptions), Live);

    /// <summary>What the published list has parsed of each send, shared as the app's singleton is.</summary>
    public PublishedContentCache Published { get; } = new();

    public CorrectionService Corrections(IEmailSender? sender = null) => new(this, User, sender ?? Email, Settings(),
        new BriefRenderer(new MarkdownService()), Clock, Options.Create(new AppOptions { BaseUrl = "https://tide.example.org" }), Published);

    public void Dispose()
    {
        _connection?.Dispose();
        if (_sqlServer is null) return;
        using var db = PlainContext();
        db.Database.EnsureDeleted();
    }

    /// <summary>The smallest valid PNG (1x1).</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
}

/// <summary>
/// A strategy that retries, as SQL Server's does in production (EnableRetryOnFailure), so user-initiated transactions are
/// held to its rule, and an error production retries is retried here too: on SQL Server a serializable path's deadlock
/// victim (1205) is tried again, as there, rather than failing the test. Its delays are short, for a test's sake.
/// </summary>
internal sealed class RetryingStrategy(Microsoft.EntityFrameworkCore.Storage.ExecutionStrategyDependencies dependencies)
    : SqlServerRetryingExecutionStrategy(dependencies, 6, TimeSpan.FromMilliseconds(200), errorNumbersToAdd: null)
{
    // SQL Server's transient errors, as production's strategy has them; on SQLite, whose production strategy retries
    // nothing, none.
    protected override bool ShouldRetryOn(Exception exception) =>
        Dependencies.CurrentContext.Context.Database.IsSqlServer() && base.ShouldRetryOn(exception);
}
