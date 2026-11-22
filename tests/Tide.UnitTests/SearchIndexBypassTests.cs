using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>
/// An article's search text is marked out of date by an interceptor on every save, which a bulk <c>ExecuteUpdate</c> or
/// <c>ExecuteDelete</c> doesn't pass through. So no code may change an article's searched parts that way: one that did
/// would leave a search matching the article by its old words. Each bulk statement on an article or one of its searched
/// parts is listed here with why it's safe; a new one fails until it's looked at.
/// </summary>
public partial class SearchIndexBypassTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Tide.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repo root (Tide.sln).");
    }

    // File (from src) and the table, and why the statement leaves every searched part as it was.
    private static readonly Dictionary<(string File, string Table), string> Allowed = new()
    {
        [("Tide.Application/Files/ScreenshotService.cs", "Articles")] = "sets ScreenshotsChangedAtUtc, a time no search reads",
    };

    [Fact]
    public void No_bulk_statement_changes_an_articles_searched_parts_unseen()
    {
        var src = Path.Combine(Root(), "src");
        var (found, seen) = (new List<string>(), new HashSet<(string, string)>());
        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(path);
            var file = Path.GetRelativePath(src, path).Replace('\\', '/');
            foreach (Match m in BulkRx().Matches(text))
            {
                // The statement the call ends: back to the last statement or block boundary.
                var start = text.LastIndexOfAny([';', '{', '}'], m.Index) + 1;
                foreach (Match t in TableRx().Matches(text[start..m.Index]))
                {
                    var table = t.Groups[1].Success ? t.Groups[1].Value : t.Groups[2].Value + "s";
                    if (Allowed.ContainsKey((file, table))) seen.Add((file, table));
                    else found.Add($"{file}: {m.Value} on {table}");
                }
            }
        }
        found.Should().BeEmpty("a bulk statement bypasses SearchDirtyInterceptor: save through the context instead, or list it in Allowed with why it's safe");
        Allowed.Keys.Should().BeSubsetOf(seen, "an entry whose statement has gone is taken out");
    }

    [Fact]
    public void The_check_sees_a_bulk_statement_on_a_searched_part()
    {
        const string code = """
            await db.ArticleBullets
                .Where(b => b.ArticleId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Text, "x"), ct);
            """;
        var m = BulkRx().Match(code);
        m.Success.Should().BeTrue();
        TableRx().Match(code[..m.Index]).Groups[1].Value.Should().Be("ArticleBullets");
    }

    [GeneratedRegex(@"\bExecute(?:Update|Delete)(?:Async)?\b")] private static partial Regex BulkRx();
    [GeneratedRegex(@"\.(Articles|ArticleBullets|ArticleIndicators|ArticleCves|ArticleTechniques|ArticleScreenshots)\b|Set<(Article(?:Bullet|Indicator|Cve|Technique|Screenshot)?)>")]
    private static partial Regex TableRx();
}
