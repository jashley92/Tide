using System.Text;
using FluentAssertions;
using Tide.Application.Operations;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>Bug round 1, platform: a hand-edited configuration bundle with nulls in it.</summary>
public class BugRound1PlatformTests : IDisposable
{
    private readonly TestDb _db = new();

    public BugRound1PlatformTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // A null item in a list, or a null value, failed the whole preview with "Something went wrong" (a
    // NullReferenceException) instead of leaving the item out or skipping it with its reason.
    [Fact]
    public async Task ABundleWithNulls_PreviewsTheRest_InsteadOfFailing()
    {
        var json = $$"""
            {
              "format": "{{ConfigBundle.FormatName}}", "version": {{ConfigBundle.CurrentVersion}},
              "exportedAtUtc": "2026-11-01T00:00:00Z", "exportedBy": "x", "appVersion": "x",
              "settings": [ null, { "key": "Distribution:TeamRecipients", "value": null }, { "key": "Org:TeamName", "value": "Blue Team" } ],
              "roleMappings": [ null, { "adGroup": "CONTOSO\\CTI", "role": "Analyst" } ],
              "sources": [ null ],
              "scorecard": [ null, { "key": "c", "name": "Category", "sortOrder": 0, "isRetired": false, "tags": [ null, { "key": "t", "name": "Tag", "sortOrder": 0, "isRetired": false } ] } ],
              "queryTemplates": [ null ],
              "holidays": [ null ]
            }
            """;
        var bundle = ConfigBundle.Parse(Encoding.UTF8.GetBytes(json));

        var preview = await new ConfigBundleService(_db, _db.User, new CountingRoleDirectory(), TimeProvider.System).PreviewAsync(bundle);

        preview.Changes.Should().Contain(c => c.Area == "Settings" && c.Item == "Org:TeamName" && c.Kind == BundleChangeKind.Add);
        preview.Changes.Should().Contain(c => c.Area == "Settings" && c.Item == "Distribution:TeamRecipients" && c.Kind == BundleChangeKind.Skipped,
            "a null isn't an empty list, which would stop the edition being emailed");
        preview.Changes.Should().Contain(c => c.Area == "AD groups" && c.Kind == BundleChangeKind.Add);
        preview.Changes.Should().Contain(c => c.Area == "Scorecard" && c.Item == "Category › Tag" && c.Kind == BundleChangeKind.Add);
        preview.Changes.Where(c => c.Area is "Sources" or "Query templates" or "Holidays").Should().BeEmpty();
    }
}
