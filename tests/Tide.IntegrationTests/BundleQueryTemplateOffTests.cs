using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Operations;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>A query template an administrator turned off travels in the config bundle.</summary>
public class BundleQueryTemplateOffTests : IDisposable
{
    private readonly TestDb _source = new();
    private readonly TestDb _target = new();

    public BundleQueryTemplateOffTests()
    {
        _source.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        _target.User.Roles = new HashSet<AppRole> { AppRole.Admin };
    }

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
    }

    private static ConfigBundleService Bundles(TestDb db) => new(db, db.User, new CountingRoleDirectory(), TimeProvider.System);

    // Query templates lets an empty template turn a kind off; the bundle exported it as "" and the import refused it as
    // "not a dialect, a kind of value and a template", so the target kept building queries for that kind.
    [Fact]
    public async Task ATurnedOffQueryTemplate_ImportsAsTurnedOff()
    {
        await _source.Queries().SaveTemplateAsync("kql", HuntValueKind.Url, "");
        var bundle = await Bundles(_source).ExportAsync();
        bundle.QueryTemplates.Should().Contain(q => q.Dialect == "kql" && q.Kind == nameof(HuntValueKind.Url) && q.Template == "");

        var parsed = ConfigBundle.Parse(Encoding.UTF8.GetBytes(bundle.ToJson()));
        var done = await Bundles(_target).PreviewAndImportAsync(parsed);

        done.Changes.Should().ContainSingle(c => c.Area == "Query templates")
            .Which.Kind.Should().BeOneOf(BundleChangeKind.Add, BundleChangeKind.Update);
        (await _target.Queries().TemplatesAsync()).Single(r => r.Dialect.Key == "kql" && r.Kind == HuntValueKind.Url).IsOff.Should().BeTrue();
    }

    // The import holds a template to the editor's rules: one without {{values}} is refused with the editor's reason, and
    // one with Windows line endings and padding is stored as the editor would store it.
    [Fact]
    public async Task ABundleQueryTemplate_IsHeldToTheEditorsRules()
    {
        var bundle = (await Bundles(_source).ExportAsync()) with
        {
            QueryTemplates =
            [
                new("kql", nameof(HuntValueKind.Domain), "DeviceEvents | where x == 1"),
                new("kql", nameof(HuntValueKind.Url), "  DeviceEvents\r\n| where Url in ({{values}})\r\n "),
            ],
        };

        var done = await Bundles(_target).PreviewAndImportAsync(bundle);

        done.Changes.Should().ContainSingle(c => c.Item == "kql · Domain")
            .Which.Should().Match<BundleChange>(c => c.Kind == BundleChangeKind.Skipped && c.Detail!.Contains("{{values}}"));
        await using var check = _target.Context();
        (await check.QueryTemplates.AsNoTracking().ToListAsync()).Should().ContainSingle()
            .Which.Template.Should().Be("DeviceEvents\n| where Url in ({{values}})");
    }
}
