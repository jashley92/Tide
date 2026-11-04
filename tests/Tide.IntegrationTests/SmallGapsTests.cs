using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>Races closed: a story deleted as an article is linked to it, and a CVE refresh by hand during the scheduled one.</summary>
public class SmallGapsTests : IDisposable
{
    private readonly TestDb _db = new(connectionPerContext: true);   // each test runs two things at once

    public void Dispose() => _db.Dispose();

    // The delete found no articles, a colleague linked one, and the delete went on: the foreign key set the article's
    // story to null, so the link they'd just made was gone, unsaid. Now the link waits for the delete, and is refused.
    [Fact]
    public async Task AnArticleLinkedAsItsStoryIsDeleted_IsRefused_NotSilentlyUnlinked()
    {
        var stories = _db.Stories();
        var story = await stories.CreateAsync("Edge VPN campaign", null);
        var article = await _db.Articles().CreateAsync("Edge VPN exploited");
        Task? link = null;
        _db.BeforeSave = saving =>
        {
            if (!saving.ChangeTracker.Entries<Story>().Any(e => e.State == EntityState.Deleted)) return;
            _db.BeforeSave = null;
            link = Task.Run(() => _db.Stories().LinkAsync(article, story));
            // Unchecked, the link was saved in this moment; now it waits for the delete to finish.
            ((IAsyncResult)link).AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
        };

        await stories.DeleteAsync(story);

        await FluentActions.Awaiting(() => link!).Should().ThrowAsync<KeyNotFoundException>().WithMessage("That story no longer exists.");
        (await stories.GetAsync(story)).Should().BeNull();
        await using var check = _db.Context();
        (await check.Articles.SingleAsync(a => a.Id == article)).StoryId.Should().BeNull("the link never landed");
    }

    [Fact]
    public async Task AStoryWithAnArticle_IsStillRefused_AndOneWithout_IsDeleted()
    {
        var stories = _db.Stories();
        var used = await stories.CreateAsync("Used", null);
        await stories.LinkAsync(await _db.Articles().CreateAsync("On the story"), used);
        await FluentActions.Awaiting(() => stories.DeleteAsync(used)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*closed, not deleted*");

        var empty = await stories.CreateAsync("Empty", null);
        await stories.DeleteAsync(empty);
        (await stories.GetAsync(empty)).Should().BeNull();
        (await stories.GetAsync(used)).Should().NotBeNull();
    }

    // An administrator's refresh at the same moment as the scheduled one: both added the same new CVEs, and the second
    // save failed on their keys. Now a refresh asked for while one runs doesn't run.
    [Fact]
    public async Task ARefreshAskedForWhileOneRuns_DoesntRun_AndOneAfterwardsDoes()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        var o = new CveOptions { Enabled = true, NvdDelayMs = 0 };
        _db.Http.Set(o.KevUrl, """{"vulnerabilities":[{"cveID":"CVE-2026-0101","dateAdded":"2026-10-24"}]}""");
        (CveRun? ByHand, CveRun? Scheduled) meanwhile = default;
        _db.Http.During = async () => meanwhile = (await _db.Cves(o).EnrichNowAsync(), await _db.Cves(o).EnrichDueAsync());

        var run = await _db.Cves(o).EnrichNowAsync();

        meanwhile.Should().Be(((CveRun?)null, (CveRun?)null), "one was running");
        run!.Kev.Should().Be(1);
        run.Error.Should().BeNull();
        (await _db.Cves(o).EnrichNowAsync()).Should().NotBeNull("the first has finished");
    }
}
