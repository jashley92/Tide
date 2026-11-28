using System.Text;
using AwesomeAssertions;
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

    // Query templates lets an empty template turn a kind off; the bundle exported it as "" and the import refused it as
    // "not a dialect, a kind of value and a template", so the target kept building queries for that kind.
    [Fact]
    public async Task ATurnedOffQueryTemplate_ImportsAsTurnedOff()
    {
        await _source.Queries().SaveTemplateAsync("kql", HuntValueKind.Url, "");
        var bundle = await new ConfigBundleService(_source, _source.User, new CountingRoleDirectory(), TimeProvider.System).ExportAsync();
        bundle.QueryTemplates.Should().Contain(q => q.Dialect == "kql" && q.Kind == nameof(HuntValueKind.Url) && q.Template == "");

        var parsed = ConfigBundle.Parse(Encoding.UTF8.GetBytes(bundle.ToJson()));
        var preview = await new ConfigBundleService(_target, _target.User, new CountingRoleDirectory(), TimeProvider.System).PreviewAsync(parsed);

        preview.Changes.Should().ContainSingle(c => c.Area == "Query templates")
            .Which.Kind.Should().BeOneOf(BundleChangeKind.Add, BundleChangeKind.Update);
    }
}
