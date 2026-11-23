using System.Security.Cryptography;
using AwesomeAssertions;
using Tide.Application.Files;
using Tide.Domain.Entities;

namespace Tide.IntegrationTests;

/// <summary>
/// The weekly check of stored files (<see cref="FileSweep"/>) reports rows without bytes, bytes without a row (not a
/// fresh upload) and bytes that no longer match their hash, and changes nothing.
/// </summary>
public class FileSweepTests
{
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task It_reports_what_is_missing_unowned_or_changed_and_deletes_nothing(string provider)
    {
        using var db = new TestDb(provider: provider);
        var now = DateTimeOffset.UtcNow;
        StoredFile Row(byte[] bytes) => new() { FileName = "a.png", ContentType = "image/png", SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        var (good, changed, gone) = (Row([1, 2, 3]), Row([4, 5, 6]), Row([7, 8, 9]));
        await using (var ctx = db.Context())
        {
            ctx.Files.AddRange(good, changed, gone);
            await ctx.SaveChangesAsync();
        }
        db.Store.Files[good.Id] = [1, 2, 3];
        db.Store.Files[changed.Id] = [4, 5, 99];
        var (stray, fresh) = (Guid.NewGuid(), Guid.NewGuid());
        db.Store.Files[stray] = [0];
        db.Store.Files[fresh] = [0];
        db.Store.Written[fresh] = now.AddMinutes(-5);

        await using (var ctx = db.Context())
        {
            var r = await FileSweep.RunAsync(ctx, db.Store, now, new Random(1));
            (r.Rows, r.OnDisk, r.Missing, r.Orphans, r.Checked, r.Mismatched).Should().Be((3, 4, 1, 1, 2, 1), "a fresh upload's bytes may come before its row");
            r.Clean.Should().BeFalse();
            r.Examples.Should().BeEquivalentTo([$"missing {gone.Id}", $"no row {stray}", $"hash differs {changed.Id}"]);
            r.Summary().Should().StartWith("3 rows, 4 files: 1 missing, 1 without a row, 1 of 2 sampled don't match their hash");
        }
        db.Store.Files.Should().HaveCount(4, "it only reports");
    }

    [Fact]
    public async Task The_disk_store_lists_its_own_files_and_nothing_else_in_its_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "tide-sweep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Tide.Infrastructure.Storage.DiskFileStore(Microsoft.Extensions.Options.Options.Create(new FileStoreOptions { RootPath = root }), new Env());
            var id = Guid.NewGuid();
            await store.WriteAsync(id, new MemoryStream([1, 2]));
            await File.WriteAllTextAsync(Path.Combine(root, "activity.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "metrics-inbox"));
            await File.WriteAllTextAsync(Path.Combine(root, id.ToString("N")[..2], "x.bin.partial"), "");
            await File.WriteAllTextAsync(Path.Combine(root, id.ToString("N")[..2], "not-a-guid.bin"), "");
            store.List().Select(f => f.Id).Should().Equal(id);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Env : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tide";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
