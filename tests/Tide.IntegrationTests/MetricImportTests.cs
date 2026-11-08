using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tide.Application.Files;
using Tide.Application.Metrics;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Storage;

namespace Tide.IntegrationTests;

public class MetricImportTests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly DateOnly Thu = new(2026, 10, 22), Fri = new(2026, 10, 23), Sat = new(2026, 10, 24), Mon = new(2026, 10, 26);

    public MetricImportTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<(Guid Open, Guid Old)> Metrics()
    {
        var svc = _db.Metrics();
        var group = await svc.AddGroupAsync("Alert queue");
        var open = await svc.SaveMetricAsync(new MetricForm { GroupId = group, Name = "Open" });
        var old = await svc.SaveMetricAsync(new MetricForm { GroupId = group, Name = "Older than 24 hours" });
        return (open, old);
    }

    private static byte[] Csv(params string[] rows) => Encoding.UTF8.GetBytes("date,metric_key,value,note\n" + string.Join('\n', rows));

    private async Task<MetricValue?> Value(Guid metric, DateOnly day)
    {
        await using var ctx = _db.Context();
        return await ctx.MetricValues.AsNoTracking().SingleOrDefaultAsync(v => v.DefinitionId == metric && v.BriefDate == day);
    }

    [Fact]
    public async Task AnImport_SetsValuesWithProvenance_AndReadingItAgainChangesNothing()
    {
        var (open, old) = await Metrics();
        var file = Csv("2026-10-22,open,42,", "2026-10-22,OLDER-THAN-24-HOURS,\"1,204\",relay outage");

        var first = await _db.MetricImports().UploadAsync("queue.csv", file);

        first.Should().BeEquivalentTo(new { Rows = 2, Applied = 2, Unchanged = 0, Skipped = 0, Rejected = false, Origin = MetricImportOrigin.Upload,
            ImportedByName = "Test Analyst" });
        var v = (await Value(old, Thu))!;
        v.Value.Should().Be(1204);
        v.Note.Should().Be("relay outage");
        v.Source.Should().Be(MetricSource.Imported);
        v.ImportId.Should().Be(first.Id);
        v.EnteredByName.Should().Be("Import: queue.csv");
        (await _db.Metrics().GetDayAsync(Thu)).Groups.Single().Tiles.First(t => t.Metric.Id == open).ImportedFrom.Should().Be("Import: queue.csv");

        var again = await _db.MetricImports().UploadAsync("queue.csv", file);
        again.Should().BeEquivalentTo(new { Applied = 0, Unchanged = 2, Skipped = 0 });
        (await Value(old, Thu))!.ImportId.Should().Be(first.Id, "an unchanged row keeps the import that set it");

        var corrected = await _db.MetricImports().UploadAsync("queue-2.csv", Csv("2026-10-22,open,40,"));
        corrected.Applied.Should().Be(1, "a later file replaces an imported value");
        (await Value(open, Thu))!.Value.Should().Be(40);
    }

    [Fact]
    public async Task AnImport_NeverOverwritesWhatAPersonDecided()
    {
        var (open, old) = await Metrics();
        await _db.Metrics().SaveValueAsync(open, Thu, 7, null);   // typed
        await _db.MetricImports().UploadAsync("a.csv", Csv("2026-10-22,older-than-24-hours,3,"));
        await _db.Metrics().SaveValueAsync(old, Thu, 2, "recounted");   // typed over the import
        await using (var ctx = _db.Context())
        {
            ctx.Briefs.Add(new Brief { BriefDate = Fri, AnalystId = "x", AnalystName = "x", Status = BriefStatus.Sent });
            await ctx.SaveChangesAsync();
        }

        var result = await _db.MetricImports().UploadAsync("b.csv",
            Csv("2026-10-22,open,9,", "2026-10-22,older-than-24-hours,3,", "2026-10-23,open,1,"));

        result.Should().BeEquivalentTo(new { Applied = 0, Skipped = 3 });
        result.Problems.Should().Contain("Row 2: Open on 2026-10-22 was entered by Test Analyst (7), so it was kept.")
            .And.Contain("Row 3: Older than 24 hours on 2026-10-22 was entered by")
            .And.Contain("Row 4: the brief for 2026-10-23 has been sent");
        (await Value(open, Thu))!.Value.Should().Be(7);
        var overridden = (await Value(old, Thu))!;
        overridden.Value.Should().Be(2);
        overridden.IsOverride.Should().BeTrue();
        overridden.Source.Should().Be(MetricSource.Manual);
        (await Value(open, Fri)).Should().BeNull();
    }

    [Fact]
    public async Task BadRowsAreSkippedAndExplained_TheRestApply()
    {
        var (open, _) = await Metrics();
        var retired = await _db.Metrics().SaveMetricAsync(new MetricForm { GroupId = (await _db.Metrics().ListAsync()).Single().Id, Name = "Gone" });
        await _db.Metrics().SaveValueAsync(retired, Thu, 1, null);
        await _db.Metrics().SetMetricRetiredAsync(retired, true);

        var result = await _db.MetricImports().UploadAsync("mixed.csv", Csv(
            "22/10/2026,open,1,",
            "2026-10-22,nope,1,",
            "2026-10-22,gone,1,",
            "2026-10-22,open,,",
            "2026-10-22,open,n/a,",
            "2026-10-24,open,1,",
            "2099-01-05,open,1,",
            "2026-10-26,open,5,",
            "2026-10-26,open,6,"));

        result.Should().BeEquivalentTo(new { Rows = 9, Applied = 1, Skipped = 8 });
        result.Problems!.Split('\n').Should().Equal(
            "Row 2: '22/10/2026' isn't a date in the form 2026-10-28.",
            "Row 3: no metric has the key 'nope'.",
            "Row 4: Gone is retired.",
            "Row 5: Open on 2026-10-22 has no value.",
            "Row 6: 'n/a' isn't a number TIDE can keep.",
            "Row 7: 2026-10-24 is a weekend; metrics are kept for business days.",
            "Row 8: 2099-01-05 hasn't happened yet.",
            "Row 10: Open on 2026-10-26 is also in row 9, which was used.");
        (await Value(open, Mon))!.Value.Should().Be(5);
        _ = Sat;
    }

    [Fact]
    public async Task AFileWithoutTheColumns_IsRecordedAsRefused_AndChangesNothing()
    {
        await Metrics();
        var result = await _db.MetricImports().UploadAsync("../../etc/report.csv", Encoding.UTF8.GetBytes("when,what,value\n2026-10-22,x,1\n"));

        result.Rejected.Should().BeTrue();
        result.FileName.Should().Be("report.csv", "only the file's own name is kept");
        result.Problems.Should().Contain("no date or metric_key column");
        (await _db.MetricImports().RecentAsync()).Should().ContainSingle(i => i.Id == result.Id);
        await using var ctx = _db.Context();
        (await ctx.MetricValues.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OnlyAnAdministratorUploads_AndOnlyTIDEItselfReadsTheDropFolder()
    {
        await Metrics();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await _db.Invoking(d => d.MetricImports().UploadAsync("a.csv", Csv("2026-10-22,open,1,"))).Should().ThrowAsync<ForbiddenException>();
        await _db.Invoking(d => d.MetricImports().RecentAsync()).Should().ThrowAsync<ForbiddenException>();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await _db.Invoking(d => d.MetricImports().ImportDroppedAsync("a.csv", Csv("2026-10-22,open,1,")))
            .Should().ThrowAsync<ForbiddenException>("a signed-in person can't pass a file off as the drop folder's");
    }

    [Fact]
    public async Task TheDropFolder_ImportsSettledCsvFilesOnce_MovesThem_AndLeavesTheRest()
    {
        await Metrics();
        var root = Path.Combine(Path.GetTempPath(), "tide-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = new MetricDropFolder(Options.Create(new FileStoreOptions { RootPath = root }), new TestHostEnvironment(),
                new StaticOptionsMonitor<MetricImportOptions>(new MetricImportOptions { DropFolderEnabled = true }), TimeProvider.System,
                NullLogger<MetricDropFolder>.Instance);
            Directory.CreateDirectory(folder.Inbox);
            void Drop(string name, byte[] bytes, bool settled = true)
            {
                var path = Path.Combine(folder.Inbox, name);
                File.WriteAllBytes(path, bytes);
                if (settled) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
            }
            Drop("queue.csv", Csv("2026-10-22,open,42,"));
            Drop("broken.csv", Encoding.UTF8.GetBytes("nothing useful"));
            Drop("arriving.csv", Csv("2026-10-22,open,1,"), settled: false);
            Drop("~$queue.csv", Csv("2026-10-22,open,1,"));
            Drop("notes.txt", Csv("2026-10-22,open,1,"));

            _db.User.IsAuthenticated = false;   // the background service: no one signed in
            (await folder.ProcessAsync(_db.MetricImports())).Should().Be(2);
            (await folder.ProcessAsync(_db.MetricImports())).Should().Be(0, "each file is read once");

            Directory.GetFiles(folder.Inbox).Select(Path.GetFileName).Should().BeEquivalentTo("arriving.csv", "~$queue.csv", "notes.txt");
            Directory.GetFiles(Path.Combine(folder.Inbox, "processed")).Should().ContainSingle(f => f.EndsWith("-queue.csv"));
            Directory.GetFiles(Path.Combine(folder.Inbox, "rejected")).Should().ContainSingle(f => f.EndsWith("-broken.csv"));
            await using var ctx = _db.Context();
            var imports = await ctx.MetricImports.AsNoTracking().ToListAsync();
            imports.Should().HaveCount(2).And.OnlyContain(i => i.Origin == MetricImportOrigin.DropFolder && i.ImportedByName == "Drop folder");
            (await ctx.MetricValues.SingleAsync()).Value.Should().Be(42);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheDropFolder_DoesNothingUnlessEnabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "tide-drop-" + Guid.NewGuid().ToString("N"));
        var folder = new MetricDropFolder(Options.Create(new FileStoreOptions { RootPath = root }), new TestHostEnvironment(),
            new StaticOptionsMonitor<MetricImportOptions>(new MetricImportOptions()), TimeProvider.System, NullLogger<MetricDropFolder>.Instance);

        (await folder.ProcessAsync(_db.MetricImports())).Should().Be(0);
        Directory.Exists(root).Should().BeFalse("nothing is created on disk while it's off");
    }
}
