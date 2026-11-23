using System.IO.Compression;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.History;
using Tide.Domain.Entities;

namespace Tide.IntegrationTests;

/// <summary>
/// The audit log's archive-then-prune (<see cref="AuditArchive"/>): off unless set, never under a year, whole months only,
/// and a month's rows leave the database only once its archive reads back with the same rows and its hash is recorded.
/// </summary>
public class AuditArchiveTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tide-audit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void It_is_off_unless_set_and_never_keeps_less_than_a_year()
    {
        AuditArchive.Days(new AuditOptions()).Should().BeNull();
        AuditArchive.Days(new AuditOptions { RetentionDays = 0 }).Should().BeNull();
        AuditArchive.Days(new AuditOptions { RetentionDays = 30 }).Should().Be(AuditArchive.MinimumDays);
        AuditArchive.Days(new AuditOptions { RetentionDays = 730 }).Should().Be(730);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Whole_months_past_the_retention_are_archived_checked_and_only_then_removed(string provider)
    {
        using var db = new TestDb(provider: provider);
        AuditLogEntry At(DateTimeOffset at, string key) => new() { AtUtc = at, UserId = "x", UserName = "X", Action = "Modified", EntityType = "Article", EntityKey = key, ChangesJson = """{"Title":["a","b"]}""" };
        await using (var ctx = db.Context())
        {
            ctx.AuditLog.AddRange(
                At(new DateTimeOffset(2024, 3, 2, 9, 0, 0, TimeSpan.Zero), "march-1"), At(new DateTimeOffset(2024, 3, 30, 9, 0, 0, TimeSpan.Zero), "march-2"),
                At(new DateTimeOffset(2025, 1, 15, 9, 0, 0, TimeSpan.Zero), "january"),
                At(new DateTimeOffset(2025, 11, 30, 9, 0, 0, TimeSpan.Zero), "inside the year's month"),   // November 2025 isn't whole past the cutoff
                At(Now.AddDays(-1), "recent"));
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = db.Context())
            (await AuditArchive.RunAsync(ctx, new AuditOptions(), _folder, Now)).Should().BeEmpty("off by default");

        IReadOnlyList<ArchivedMonth> months;
        await using (var ctx = db.Context()) months = await AuditArchive.RunAsync(ctx, new AuditOptions { RetentionDays = 365 }, _folder, Now);

        months.Select(m => (m.FromUtc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), m.Rows)).Should().Equal(("2024-03", 2), ("2025-01", 1));
        await using (var ctx = db.Context())
            (await ctx.AuditLog.AsNoTracking().OrderBy(e => e.AtUtc).Select(e => e.EntityKey).ToListAsync()).Should().Equal("inside the year's month", "recent");

        var march = months[0];
        var (rows, sha) = await AuditArchive.CheckAsync(Path.Combine(_folder, march.File));
        (rows, sha).Should().Be((2, march.Sha256));
        await using (var gz = new GZipStream(File.OpenRead(Path.Combine(_folder, march.File)), CompressionMode.Decompress))
        using (var reader = new StreamReader(gz))
        {
            var first = JsonDocument.Parse((await reader.ReadLineAsync())!).RootElement;
            (first.GetProperty("entityKey").GetString(), first.GetProperty("changesJson").GetString()).Should().Be(("march-1", """{"Title":["a","b"]}"""));
        }
        (await File.ReadAllLinesAsync(Path.Combine(_folder, AuditArchive.ManifestName))).Should().HaveCount(2).And.AllSatisfy(l => l.Should().Contain("sha256"));
        Directory.EnumerateFiles(_folder, "*.partial").Should().BeEmpty();

        await using (var ctx = db.Context())
            (await AuditArchive.RunAsync(ctx, new AuditOptions { RetentionDays = 365 }, _folder, Now.AddHours(1))).Should().BeEmpty("nothing left to archive");
    }

    [Fact]
    public async Task The_job_archives_into_the_file_stores_folder_and_says_what_it_did()
    {
        using var db = new TestDb();
        await using (var ctx = db.Context())
        {
            ctx.AuditLog.Add(new AuditLogEntry { AtUtc = DateTimeOffset.UtcNow.AddYears(-3), UserId = "x", UserName = "X", Action = "Added", EntityType = "Article", EntityKey = "old" });
            await ctx.SaveChangesAsync();
        }
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Tide.Application.Abstractions.IAppDbContextFactory>(services, db);
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        Tide.Web.Services.AuditArchiveHostedService Job(int? days) => new(
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(provider),
            new Tide.TestSupport.StaticOptionsMonitor<AuditOptions>(new AuditOptions { RetentionDays = days }),
            new Tide.TestSupport.StaticOptionsMonitor<Tide.Application.Files.FileStoreOptions>(new Tide.Application.Files.FileStoreOptions { RootPath = _folder }),
            new Env(), new Tide.Application.Operations.JobMonitor(TimeProvider.System), new HeldLease(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Tide.Web.Services.AuditArchiveHostedService>.Instance);

        (await Job(null).RunAsync(CancellationToken.None)).Should().Be("nothing older than the retention", "off: nothing archived");
        (await Job(400).RunAsync(CancellationToken.None)).Should().StartWith("1 rows archived");
        Directory.EnumerateFiles(Path.Combine(_folder, "audit-archive"), "audit-*.jsonl.gz").Should().ContainSingle();
    }

    private sealed class Env : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tide";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
