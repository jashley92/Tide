using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;

namespace Tide.IntegrationTests;

/// <summary>
/// The starter saved queries, outreach templates and playbooks: added at startup to a library with nothing in it, once,
/// by TIDE; never again once the team has emptied it; and not at all to one the team had already filled, which is marked
/// done all the same. Each test starts an app of its own, as each changes what startup finds.
/// </summary>
public class StarterLibrariesSeedTests
{
    private static readonly string[] Markers =
        [DatabaseInitializer.StarterSavedQueriesKey, DatabaseInitializer.StarterOutreachTemplatesKey, DatabaseInitializer.StarterPlaybooksKey];

    private static async Task Initialize(TideApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    }

    private static async Task<T> Read<T>(TideApp app, Func<AppDbContext, Task<T>> read)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static Task<(int Queries, int Templates, int Playbooks, int Markers)> Counts(TideApp app) => Read(app, async db =>
        (await db.SavedQueries.CountAsync(), await db.OutreachTemplates.CountAsync(), await db.Playbooks.CountAsync(),
            await db.Settings.CountAsync(s => Markers.Contains(s.Key))));

    private static Task Change(TideApp app, Func<AppDbContext, Task> change) => Read(app, async db =>
    {
        await change(db);
        await db.SaveChangesAsync();
        return 0;
    });

    private static readonly (int, int, int, int) Starters =
        (StarterSavedQueries.All.Count, StarterOutreachTemplates.All.Count, StarterPlaybooks.All.Count, 3);

    [Fact]
    public async Task ANewInstall_GetsEachStarterSet_SavedByTide_AndAnotherStartAddsNothing()
    {
        using var app = new TideApp();
        (await Counts(app)).Should().Be(Starters);

        var queries = await Read(app, db => db.SavedQueries.AsNoTracking().ToListAsync());
        queries.Select(q => (q.Name, q.Dialect, q.Text)).Should().BeEquivalentTo(StarterSavedQueries.All.Select(s => (s.Name, s.Dialect, s.Text)));
        queries.Should().OnlyContain(q => q.CreatedByName == "TIDE" && q.CreatedBy == "system" && !q.IsRetired);
        var templates = await Read(app, db => db.OutreachTemplates.AsNoTracking().ToListAsync());
        templates.Select(t => (t.Name, t.Subject, t.Ceiling)).Should().BeEquivalentTo(StarterOutreachTemplates.All.Select(s => (s.Name, s.Subject, s.Ceiling)));
        templates.Should().OnlyContain(t => t.CreatedByName == "TIDE" && t.CreatedBy == "system" && t.To == null);
        var playbooks = await Read(app, db => db.Playbooks.AsNoTracking().Include(p => p.Steps).ToListAsync());
        playbooks.Should().OnlyContain(p => !p.IsRetired && p.CreatedBy == "system" && p.CreatedByName == "TIDE");
        playbooks.Single(p => p.Kind == ActionKind.Outreach).Steps.OrderBy(s => s.Order).Select(s => s.Text)
            .Should().Equal(StarterPlaybooks.All.Single(s => s.Kind == ActionKind.Outreach).Steps);

        await Initialize(app);
        (await Counts(app)).Should().Be(Starters, "each set is offered once");
    }

    [Fact]
    public async Task ALibraryTheTeamEmptied_StaysEmpty()
    {
        using var app = new TideApp();
        await Change(app, async db =>
        {
            db.SavedQueries.RemoveRange(await db.SavedQueries.ToListAsync());
            db.OutreachTemplates.RemoveRange(await db.OutreachTemplates.ToListAsync());
            db.Playbooks.RemoveRange(await db.Playbooks.Include(p => p.Steps).ToListAsync());
        });

        await Initialize(app);
        (await Counts(app)).Should().Be((0, 0, 0, 3), "deleted starters don't come back");
    }

    [Fact]
    public async Task ALibraryTheTeamAlreadyKeeps_GetsNoStarters_ButIsMarkedDone()
    {
        using var app = new TideApp();
        // As an install from before the starters: the team's own in each library, and no markers.
        await Change(app, async db =>
        {
            db.SavedQueries.RemoveRange(await db.SavedQueries.ToListAsync());
            db.OutreachTemplates.RemoveRange(await db.OutreachTemplates.ToListAsync());
            db.Playbooks.RemoveRange(await db.Playbooks.Include(p => p.Steps).ToListAsync());
            db.Settings.RemoveRange(await db.Settings.Where(s => Markers.Contains(s.Key)).ToListAsync());
            db.SavedQueries.Add(new SavedQuery { Name = "Ours", Dialect = "kql", Text = "SigninLogs", CreatedByName = "Robin", IsRetired = true });
            db.OutreachTemplates.Add(new OutreachTemplate { Name = "Ours", Subject = "Hello", Body = "Hello", CreatedByName = "Robin" });
            db.Playbooks.Add(new Playbook { Name = "Ours", IsRetired = true, Steps = [new PlaybookStep { Text = "Do it" }] });
        });
        (await Counts(app)).Should().Be((1, 1, 1, 0));

        await Initialize(app);
        (await Counts(app)).Should().Be((1, 1, 1, 3), "a library with anything in it, retired or not, is the team's own");
        (await Read(app, db => db.SavedQueries.Select(q => q.Name).SingleAsync())).Should().Be("Ours");
    }
}
