using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Search;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;

namespace Tide.IntegrationTests;

/// <summary>
/// Search ignores case for accented letters as SQL Server's collation does, on SQLite too. SQLite's own LIKE folds A to Z
/// only, so "éclair" missed "Éclair". Its wildcards and escapes behave exactly as before.
/// </summary>
public class AccentedSearchTests : IDisposable
{
    private TestDb _db = new();

    public AccentedSearchTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<IReadOnlyList<string>> Found(string query) =>
        [.. (await new SearchService(_db, _db.User).SearchAsync(query)).Articles.Items.Select(a => a.Title).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task SearchingAnAccentedWord_FindsItInEitherCase(string provider)
    {
        _db = _db.On(provider);
        foreach (var title in new[] { "Éclair outage", "Ölfeld breach", "Eclair without its accent" })
            await _db.Articles().CreateAsync(title, "Vendor", null, TlpLevel.Clear);

        (await Found("éclair")).Should().Equal("Éclair outage");
        (await Found("ÉCLAIR")).Should().Equal("Éclair outage");
        (await Found("ölfeld")).Should().Equal("Ölfeld breach");
        (await Found("eclair")).Should().Equal(["Eclair without its accent"], "accents still count, as in SQL Server's default collation");
    }

    // Each row: value, pattern, escape (null for none). ASCII only where case matters, so SQLite's own LIKE is the oracle.
    public static TheoryData<string?, string?, string?> Cases => new()
    {
        { "abc", "abc", null }, { "ABC", "abc", null }, { "abc", "ab", null }, { "abc", "abcd", null }, { "", "", null },
        { "", "%", null }, { "a", "", null }, { "abc", "%", null }, { "abc", "a%", null }, { "abc", "%c", null },
        { "abc", "%b%", null }, { "abc", "%%b%%", null }, { "abc", "a_c", null }, { "abc", "___", null }, { "abc", "__", null },
        { "abc", "____", null }, { "abc", "%_", null }, { "abc", "_%_%_", null }, { "abc", "_%_%_%_", null },
        { "aXbXc", "%b%c", null }, { "mississippi", "%iss%ppi", null }, { "mississippi", "m%s_s%i", null },
        { "aaa", "%a%a%a%a", null }, { "a%c", "a\\%c", "\\" }, { "abc", "a\\%c", "\\" }, { "a_c", "a\\_c", "\\" },
        { "abc", "a\\_c", "\\" }, { "a\\c", "a\\\\c", "\\" }, { "abc", "abc\\", "\\" }, { "abc", "%\\", "\\" },
        { "abc", "a\\bc", "\\" }, { "abc", "a!bc", "!" }, { "a%c", "a!%c", "!" }, { "50% off", "%0!%%", "!" },
        { "a%c", "a%%c", "%" }, { "abc", "a%c", "%" }, { "abc", "a%%c", "%" }, { "a_c", "a__c", "_" }, { "abc", "a_c", "_" },
        { "abc", "a_bc", "_" }, { "a[b]c", "a[b]c", null }, { "a[b]c", "a\\[b]c", "\\" }, { "é", "_", null }, { "éa", "_A", null },
        { "😀", "_", null }, { "😀x", "__", null }, { "x😀y", "x_y", null }, { "😀", "😀", null }, { "a😀", "A%", "😀" },
        { "aé", "a😀é", "😀" }, { null, "a", null }, { "a", null, null }, { "a", "a", null }, { "12", "1_", null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Wildcards_AndEscapes_MatchAsSqliteMatchesThem(string? value, string? pattern, string? escape)
    {
        using var own = Open(register: false);
        using var ours = Open(register: true);
        var expected = Scalar(own, Plain(escape), value, pattern, escape);

        Scalar(ours, Translated(escape), value, pattern, escape).Should().Be(expected, "the LIKE a search runs gives what SQLite's did");
        if (value is not null && pattern is not null)
            Scalar(ours, $"SELECT {SqliteLike.Like}(@p, @v{(escape is null ? "" : ", @e")})", value, pattern, escape)
                .Should().Be(expected, "ulike matches as SQLite's LIKE does, wherever case doesn't come into it");
    }

    [Fact]
    public void AnEscapeOfOtherThanOneCharacter_IsStillAnError()
    {
        using var ours = Open(register: true);
        foreach (var escape in new[] { "", "ab" })
            ((Action)(() => Scalar(ours, $"SELECT {SqliteLike.Like}(@p, @v, @e)", "é", "é", escape))).Should().Throw<SqliteException>()
                .Which.Message.Should().Contain("ESCAPE expression must be a single character");
        Scalar(ours, Translated("\\"), "É", "é", "\\").Should().Be(1L);
        Scalar(ours, Translated("\\"), "a", "a", null).Should().BeNull("a NULL escape gives NULL, as in SQLite");
    }

    [Fact]
    public void SqlitesOwnLike_IsLeftAsItWas()
    {
        using var ours = Open(register: true);
        Scalar(ours, "SELECT 'Éclair' LIKE 'éclair'").Should().Be(0L, "only the translated search adds ulike");
        Scalar(ours, Translated(null), "Éclair", "éclair", null).Should().Be(1L);

        Scalar(ours, "PRAGMA case_sensitive_like = ON");

        Scalar(ours, "SELECT 'Abc' LIKE 'abc'").Should().Be(0L);
        Scalar(ours, "SELECT 'abc' LIKE 'a_c'").Should().Be(1L);
    }

    [Fact]
    public async Task AnAsciiSearch_NeverCallsUlike_AndTheCheckRunsOncePerQuery()
    {
        for (var i = 0; i < 30; i++) await _db.Articles().CreateAsync(i % 3 == 0 ? $"Éclair {i}" : $"Report {i}", "Vendor", null, TlpLevel.Clear);
        await using var ctx = _db.Context();
        var calls = SqliteLike.Counted.Value = new SqliteLike.Calls();

        var ascii = SearchText.Contains("report");
        (await ctx.Articles.CountAsync(a => EF.Functions.Like(a.Title, ascii, SearchText.Escape))).Should().Be(20);

        calls.Like.Should().Be(0, "SQLite's own LIKE handles a pattern with no letter beyond ASCII");
        calls.NeedsUnicode.Should().Be(1, "SQLite checks the pattern once, not once a row");

        var accented = SearchText.Contains("éclair");
        (await ctx.Articles.CountAsync(a => EF.Functions.Like(a.Title, accented, SearchText.Escape))).Should().Be(10);
        calls.NeedsUnicode.Should().Be(2);
        calls.Like.Should().Be(30, "ulike looks at every row SQLite's LIKE didn't match");
    }

    [Fact]
    public async Task AnAsciiSearch_ThroughTheSearchPage_NeverCallsUlike()
    {
        foreach (var title in new[] { "Éclair outage", "Ransomware report", "Phishing wave" })
            await _db.Articles().CreateAsync(title, "Vendor", null, TlpLevel.Clear);
        var calls = SqliteLike.Counted.Value = new SqliteLike.Calls();

        (await Found("ransomware report")).Should().Equal("Ransomware report");

        calls.Like.Should().Be(0);
        calls.NeedsUnicode.Should().BePositive("the search ran through the translated LIKE");
    }

    private static string Plain(string? escape) => escape is null ? "SELECT @v LIKE @p" : "SELECT @v LIKE @p ESCAPE @e";

    // What the translator writes for EF.Functions.Like.
    private static string Translated(string? escape) => escape is null
        ? $"SELECT @v LIKE @p OR ({SqliteLike.NeedsUnicode}(@p) AND {SqliteLike.Like}(@p, @v))"
        : $"SELECT @v LIKE @p ESCAPE @e OR ({SqliteLike.NeedsUnicode}(@p) AND {SqliteLike.Like}(@p, @v, @e))";

    private static SqliteConnection Open(bool register)
    {
        var c = new SqliteConnection("Data Source=:memory:");
        if (register) SqliteText.Register(c);
        c.Open();
        return c;
    }

    private static object? Scalar(SqliteConnection c, string sql, string? value = null, string? pattern = null, string? escape = null)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (sql.Contains("@v", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("@v", (object?)value ?? DBNull.Value);
        if (sql.Contains("@p", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("@p", (object?)pattern ?? DBNull.Value);
        if (sql.Contains("@e", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("@e", (object?)escape ?? DBNull.Value);
        return cmd.ExecuteScalar() is var r and not DBNull ? r : null;
    }
}
