using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Files;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// The removed-file byte purge (<see cref="RemovedFilePurge"/>): off unless set, at least 30 days, and only a file every
/// use of which has been gone that long; the row and hash stay, the action's removed files say so, and it can't be restored.
/// </summary>
public class RemovedFilePurgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void It_is_off_unless_set_and_never_under_a_month() =>
        (RemovedFilePurge.Days(null), RemovedFilePurge.Days(0), RemovedFilePurge.Days(7), RemovedFilePurge.Days(90))
            .Should().Be(((int?)null, (int?)null, (int?)30, (int?)90));

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Only_files_gone_everywhere_for_long_enough_lose_their_bytes(string provider)
    {
        using var db = new TestDb(provider: provider);
        var team = new Team { Key = "cd", Name = "Cyber Defense" };
        var live = new ActionItem { Number = 1, Title = "Live", Kind = ActionKind.IocSweep, Team = team };
        var gone = new ActionItem { Number = 2, Title = "Deleted", Kind = ActionKind.IocSweep, Team = team, DeletedAtUtc = Now.AddDays(-40) };
        var article = new Article { Title = "Has a screenshot", AuthorId = "x", AuthorName = "X" };
        var comment = new ActionComment { ActionId = live.Id, Text = "See attached", AuthorId = "x", AuthorName = "X", AtUtc = Now.AddDays(-50) };
        StoredFile Stored() => new() { FileName = "f.png", ContentType = "image/png", SizeBytes = 1, Sha256 = new string('a', 64) };
        var (oldRemoved, newRemoved, alsoScreenshot, onDeleted, commentFile, kept) = (Stored(), Stored(), Stored(), Stored(), Stored(), Stored());
        ActionFile File(ActionItem a, StoredFile f, DateTimeOffset? removed) => new()
        {
            ActionId = a.Id, FileId = f.Id, FileName = "f.png", SizeBytes = 1, Sha256 = f.Sha256, AddedById = "x", AddedByName = "X",
            AddedAtUtc = Now.AddDays(-60), RemovedAtUtc = removed, RemovedById = removed is null ? null : "x", RemovedByName = removed is null ? null : "X",
            RemovedReason = removed is null ? null : "Wrong file",
        };
        await using (var ctx = db.Context())
        {
            ctx.AddRange(team, live, gone, article, comment, oldRemoved, newRemoved, alsoScreenshot, onDeleted, commentFile, kept);
            ctx.ActionFiles.AddRange(File(live, oldRemoved, Now.AddDays(-40)), File(live, newRemoved, Now.AddDays(-10)),
                File(live, alsoScreenshot, Now.AddDays(-40)), File(gone, onDeleted, null), File(live, kept, null));
            ctx.ArticleScreenshots.Add(new ArticleScreenshot { ArticleId = article.Id, FileId = alsoScreenshot.Id });
            ctx.ActionCommentFiles.Add(new ActionCommentFile { CommentId = comment.Id, FileId = commentFile.Id, FileName = "c.png", SizeBytes = 1, RemovedAtUtc = Now.AddDays(-35) });
            await ctx.SaveChangesAsync();
        }
        foreach (var f in new[] { oldRemoved, newRemoved, alsoScreenshot, onDeleted, commentFile, kept }) db.Store.Files[f.Id] = [1];

        await using (var ctx = db.Context()) (await RemovedFilePurge.RunAsync(ctx, db.Store, null, Now)).Should().Be(0, "off by default");
        await using (var ctx = db.Context()) (await RemovedFilePurge.RunAsync(ctx, db.Store, 30, Now)).Should().Be(3);

        db.Store.Files.Keys.Should().BeEquivalentTo([newRemoved.Id, alsoScreenshot.Id, kept.Id], "removed too lately, still a screenshot, still live");
        await using (var ctx = db.Context())
        {
            var purged = await ctx.Files.AsNoTracking().Where(f => f.BytesPurgedAtUtc != null).Select(f => f.Id).ToListAsync();
            purged.Should().BeEquivalentTo([oldRemoved.Id, onDeleted.Id, commentFile.Id]);
            (await ctx.Files.CountAsync()).Should().Be(6, "every row stays");
        }
        await using (var ctx = db.Context()) (await RemovedFilePurge.RunAsync(ctx, db.Store, 30, Now)).Should().Be(0, "nothing twice");

        db.User.Roles = new HashSet<AppRole> { AppRole.Admin, AppRole.Analyst };
        var files = await db.ActionFiles().ListAsync(live.Id);
        files.Removed.Single(f => f.FileId == oldRemoved.Id).BytesPurgedAtUtc.Should().Be(Now);
        await FluentActions.Invoking(() => db.ActionFiles().RestoreAsync(oldRemoved.Id)).Should().ThrowAsync<ArgumentException>().WithMessage("*bytes were purged*");
    }

    [Fact]
    public async Task The_job_says_what_it_purged()
    {
        using var db = new TestDb();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Tide.Application.Abstractions.IAppDbContextFactory>(services, db);
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var job = new Tide.Web.Services.FilePurgeHostedService(
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(provider),
            db.Store, new Tide.TestSupport.StaticOptionsMonitor<FileStoreOptions>(new FileStoreOptions { PurgeRemovedAfterDays = 30 }),
            new Tide.Application.Operations.JobMonitor(TimeProvider.System), new HeldLease(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Tide.Web.Services.FilePurgeHostedService>.Instance);
        (await job.RunAsync(CancellationToken.None)).Should().Be("nothing removed long enough");
    }
}
