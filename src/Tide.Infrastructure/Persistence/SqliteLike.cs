using System.Text;
using Microsoft.Data.Sqlite;
using Tide.Application.Content;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// The SQLite functions behind <c>EF.Functions.Like</c> for letters beyond A to Z, which SQLite's own LIKE matches only in
/// their exact case, so searching "éclair" missed "Éclair". <see cref="SqliteText"/> translates a LIKE to
/// <c>x LIKE p ESCAPE e OR (needs_unicode(p) AND ulike(p, x, e))</c>: SQLite's own LIKE does all the work for a pattern
/// with no letter beyond ASCII (most searches), and <c>ulike</c>, LIKE ignoring case for every letter as SQL Server's
/// collation does, runs only for a pattern that has one. SQLite factors <c>needs_unicode(p)</c> out of the row loop, as
/// it does any deterministic function of a query parameter, so it runs once a query.
/// </summary>
/// <remarks>
/// <c>ulike</c> keeps SQLite's rules: <c>%</c> matches any run of characters, <c>_</c> one character (a code point, not a
/// UTF-16 unit), the escape character makes the next one literal (and, if it is <c>%</c> or <c>_</c>, that wildcard is
/// off), an escape at the end matches nothing, and an escape that isn't one character is an error. Accents still count
/// (SQL Server's default is accent-sensitive): "eclair" doesn't find "éclair". A NULL gives false rather than NULL, so
/// the OR is NULL exactly when SQLite's LIKE is. SQLite's <c>like()</c> itself is left alone, so its index use and
/// <c>PRAGMA case_sensitive_like</c> are as they were; <c>ulike</c> ignores that pragma, which the app never sets.
/// </remarks>
internal static class SqliteLike
{
    public const string NeedsUnicode = "needs_unicode";
    public const string Like = "ulike";

    // SQLite's SQLITE_MAX_LIKE_PATTERN_LENGTH, in bytes of UTF-8, and its messages, so errors read as they did.
    private const int MaxPatternBytes = 50000;
    private const string TooComplex = "LIKE or GLOB pattern too complex";
    private const string BadEscape = "ESCAPE expression must be a single character";

    /// <summary>How many times each function ran, for a test that sets it (it flows with the test's async calls).</summary>
    internal static readonly AsyncLocal<Calls?> Counted = new();

    internal sealed class Calls
    {
        public int NeedsUnicode;
        public int Like;
    }

    public static void Register(SqliteConnection connection)
    {
        connection.CreateFunction<string?, bool>(NeedsUnicode, pattern =>
        {
            if (Counted.Value is { } c) Interlocked.Increment(ref c.NeedsUnicode);
            return pattern is not null && HasUnicodeLetter(pattern);
        }, isDeterministic: true);
        connection.CreateFunction<string?, string?, bool>(Like,
            (pattern, value) => Count() && pattern is not null && value is not null && Matches(Compile(pattern, null), value),
            isDeterministic: true);
        connection.CreateFunction<string?, string?, string?, bool>(Like,
            (pattern, value, escape) => Count() && pattern is not null && value is not null && escape is not null
                && Matches(Compile(pattern, EscapeOf(escape)), value),
            isDeterministic: true);
    }

    private static bool Count()
    {
        if (Counted.Value is { } c) Interlocked.Increment(ref c.Like);
        return true;
    }

    // A letter beyond ASCII is the only thing SQLite's LIKE can match differently from ulike: it compares every other
    // character as ulike does. Wildcards and escapes are ASCII or not letters.
    private static bool HasUnicodeLetter(string pattern)
    {
        foreach (var r in pattern.EnumerateRunes())
            if (!r.IsAscii && Rune.IsLetter(r)) return true;
        return false;
    }

    private static Rune EscapeOf(string escape) =>
        Rune.DecodeFromUtf16(escape, out var rune, out var length) == System.Buffers.OperationStatus.Done && length == escape.Length
            ? rune
            : throw new InvalidOperationException(BadEscape);

    private enum Kind : byte { Literal, One, Any }

    private readonly record struct Token(Kind Kind, Rune Rune);

    /// <summary>A compiled pattern; null tokens when it ends in the escape character, which SQLite never matches.</summary>
    private sealed record Pattern(string Text, Rune? Escape, Token[]? Tokens);

    // A query compares the same few patterns with every row (one per accented term or indicator form, taken in turn), so
    // the last few compiled are kept, per thread (SQLite calls back on the thread running the statement), and the oldest
    // gives way to a new one.
    private const int Cached = 4;
    [ThreadStatic] private static Pattern?[]? _recent;
    [ThreadStatic] private static int _next;

    private static Pattern Compile(string text, Rune? escape)
    {
        var recent = _recent ??= new Pattern?[Cached];
        foreach (var p in recent)
            if (p is not null && p.Escape == escape && string.Equals(p.Text, text, StringComparison.Ordinal)) return p;
        var compiled = Build(text, escape);
        recent[_next] = compiled;
        _next = (_next + 1) % Cached;
        return compiled;
    }

    private static Pattern Build(string text, Rune? escape)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxPatternBytes) throw new InvalidOperationException(TooComplex);

        // As SQLite does: an escape character that is itself a wildcard turns that wildcard off.
        var any = escape?.Value != '%';
        var one = escape?.Value != '_';
        var tokens = new List<Token>(text.Length);
        var runes = text.EnumerateRunes();
        Token[]? compiled = null;
        var ended = false;
        while (runes.MoveNext())
        {
            var c = runes.Current;
            if (any && c.Value == '%')
            {
                if (tokens.Count == 0 || tokens[^1].Kind != Kind.Any) tokens.Add(new Token(Kind.Any, default));
            }
            else if (escape == c)
            {
                if (!runes.MoveNext()) { ended = true; break; }
                tokens.Add(new Token(Kind.Literal, runes.Current));
            }
            else tokens.Add(new Token(one && c.Value == '_' ? Kind.One : Kind.Literal, c));
        }
        if (!ended) compiled = [.. tokens];
        return new Pattern(text, escape, compiled);
    }

    private static bool Matches(Pattern pattern, string value)
    {
        if (pattern.Tokens is not { } tokens) return false;
        var span = value.AsSpan();
        int p = 0, s = 0, anyP = -1, anyS = -1;
        while (s < span.Length)
        {
            if (p < tokens.Length)
            {
                var t = tokens[p];
                if (t.Kind == Kind.Any)
                {
                    // Try the rest of the pattern here; if it fails, come back and let % take one more character.
                    anyP = ++p;
                    anyS = s;
                    continue;
                }
                Rune.DecodeFromUtf16(span[s..], out var c, out var length);
                if (t.Kind == Kind.One || Same(t.Rune, c))
                {
                    p++;
                    s += length;
                    continue;
                }
            }
            if (anyP < 0) return false;
            Rune.DecodeFromUtf16(span[anyS..], out _, out var skipped);
            anyS += skipped;
            (p, s) = (anyP, anyS);
        }
        while (p < tokens.Length && tokens[p].Kind == Kind.Any) p++;
        return p == tokens.Length;
    }

    /// <summary>The same character ignoring case, as <see cref="NameOrder"/> compares, with A to Z (most text) done directly.</summary>
    private static bool Same(Rune a, Rune b)
    {
        if (a == b) return true;
        if (a.IsAscii && b.IsAscii) return (a.Value | 0x20) == (b.Value | 0x20) && char.IsAsciiLetter((char)a.Value);
        if (Rune.ToUpperInvariant(a) == Rune.ToUpperInvariant(b)) return true;
        // Only letters have case; anything else that differs differs (and the culture would equate some it overlooks).
        if (!Rune.IsLetter(a) || !Rune.IsLetter(b)) return false;
        Span<char> x = stackalloc char[2], y = stackalloc char[2];
        return NameOrder.SameIgnoringCase(x[..a.EncodeToUtf16(x)], y[..b.EncodeToUtf16(y)]);
    }
}
