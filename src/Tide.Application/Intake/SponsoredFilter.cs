using System.Globalization;
using System.Text;

namespace Tide.Application.Intake;

/// <summary>Why an item reads as sponsored: the marker found, and where (Author, Source, Category, Title or Summary).</summary>
public sealed record SponsoredMatch(string Marker, string Field);

/// <summary>What <see cref="SponsoredFilter.Check"/> found in a piece of text: the marker that matched, or the exception
/// that stopped one (both null when neither appears).</summary>
public sealed record SponsoredCheck(string? Marker, string? StoppedBy)
{
    public bool Sponsored => Marker is not null;
}

/// <summary>
/// The sponsored-item check every feed shares (<c>Intake:SkipSponsored</c>). Which words mark an item as sponsored is
/// data, not code: <c>Intake:SponsoredMarkers</c> lists the phrases ("Sponsored by", "Partner content", "[Partner]") and
/// <c>Intake:SponsoredExceptions</c> the phrases that contain one but aren't sponsorship ("state-sponsored"), both set on
/// Sources. A phrase matches as whole words, ignoring case, anywhere in the item's author, its source, any of its tags,
/// its title or the start of its summary; a hyphen, a dash and a space are alike, so "nation-state sponsored" also
/// covers "nation-state-sponsored". A marker inside an exception doesn't count, and nor does one that starts with
/// "sponsored" where the news names a state behind an attack ("Iran-sponsored", "Chinese sponsored", "sponsored by the
/// North Korean regime"), whatever the exceptions say; any other sponsor ("Vendor-sponsored", "Sponsored by American
/// Express") is still an advert. A new feed's convention needs only a new phrase.
/// </summary>
public sealed class SponsoredFilter
{
    /// <summary>How much of the summary is looked at: a feed that marks sponsorship there says so first ("Sponsored: …").</summary>
    public const int SummaryStart = 120;

    public const int MaxPhrases = 50;
    public const int MaxPhraseLength = 60;

    // Only phrases a real threat headline doesn't use: "advertisement", "promoted" and "webinar" appear in genuine news
    // ("Malicious advertisement campaign …"), so an administrator adds them knowingly, for a feed that needs them.
    public const string DefaultMarkers = "Sponsored; Sponsored by; Sponsored content; Sponsored post; Partner content; Paid post; Paid content; Advertorial";

    public const string DefaultExceptions = "state-sponsored; nation-state sponsored; government-sponsored; regime-sponsored";

    /// <summary>What separates phrases as typed and stored ("Sponsored; Paid post", or one per line).</summary>
    public static readonly char[] Separators = [';', '\n', '\r'];

    private readonly (string Phrase, string Folded)[] _markers;
    private readonly (string Phrase, string Folded)[] _exceptions;

    public SponsoredFilter(IEnumerable<string> markers, IEnumerable<string> exceptions)
    {
        // The longest marker first, so "Sponsored by TryHackMe" is put down to "Sponsored by", not "Sponsored".
        _markers = [.. Usable(markers).Select(m => (m, Fold(m))).OrderByDescending(m => m.Item2.Length)];
        _exceptions = [.. Usable(exceptions).Select(e => (e, Fold(e)))];
    }

    /// <summary>The filter as the intake settings have it, or null when <c>Intake:SkipSponsored</c> is off.</summary>
    public static SponsoredFilter? For(IntakeOptions o) =>
        o.SkipSponsored ? new SponsoredFilter(Parse(o.SponsoredMarkers ?? DefaultMarkers), Parse(o.SponsoredExceptions ?? DefaultExceptions)) : null;

    /// <summary>The phrases in a list as typed or stored, trimmed, each once (ignoring case), in order.</summary>
    public static IReadOnlyList<string> Parse(string? text) =>
        [.. (text ?? "").Split(Separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The list as it's stored ("Sponsored; Paid post"). Throws <see cref="ArgumentException"/> saying what's wrong:
    /// a phrase longer than <see cref="MaxPhraseLength"/> or with no letter or digit (it would match almost anything),
    /// more than <see cref="MaxPhrases"/>, or (when <paramref name="required"/>) none.</summary>
    public static string Normalize(string what, string? text, bool required)
    {
        var phrases = Parse(text);
        if (required && phrases.Count == 0) throw new ArgumentException($"{what} needs at least one phrase; to stop skipping sponsored items, turn the switch off.");
        if (phrases.Count > MaxPhrases) throw new ArgumentException($"{what} has at most {MaxPhrases} phrases.");
        if (phrases.FirstOrDefault(p => p.Length > MaxPhraseLength) is { } longer)
            throw new ArgumentException($"{what}: '{longer[..20]}…' is longer than {MaxPhraseLength} characters.");
        if (phrases.FirstOrDefault(p => !p.Any(char.IsLetterOrDigit)) is { } bare)
            throw new ArgumentException($"{what}: '{bare}' needs a letter or digit.");
        return string.Join("; ", phrases);
    }

    /// <summary>The first place <paramref name="item"/> reads as sponsored, or null when it doesn't.</summary>
    public SponsoredMatch? Match(ParsedItem item)
    {
        foreach (var (field, text) in Fields(item))
            if (Check(text).Marker is { } marker) return new SponsoredMatch(marker, field);
        return null;
    }

    /// <summary>The marker <paramref name="text"/> carries, or the exception that stopped one: what the Test box on Sources shows.</summary>
    public SponsoredCheck Check(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new SponsoredCheck(null, null);
        var at = new List<int>();
        var t = Fold(text, at);
        var shields = _exceptions.SelectMany(e => Spans(t, e.Folded).Select(s => (e.Phrase, s.Start, s.End))).ToList();
        string? stoppedBy = null;
        foreach (var (phrase, folded) in _markers)
            foreach (var (start, end) in Spans(t, folded))
            {
                var shield = shields.FirstOrDefault(s => s.Start <= start && end <= s.End).Phrase ?? StateSponsor(text, t, at, start);
                if (shield is null) return new SponsoredCheck(phrase, null);
                stoppedBy ??= shield;
            }
        return new SponsoredCheck(null, stoppedBy);
    }

    // Each text the parser reads for an item, by where it came from.
    private static IEnumerable<(string Field, string? Text)> Fields(ParsedItem item)
    {
        yield return ("Author", item.Author);
        yield return ("Source", item.Origin);
        foreach (var c in item.Categories) yield return ("Category", c);
        yield return ("Title", item.Title);
        yield return ("Summary", item.Summary is { Length: > SummaryStart } s ? s[..SummaryStart] : item.Summary);
    }

    private static IEnumerable<string> Usable(IEnumerable<string> phrases) =>
        phrases.Select(p => p.Trim()).Where(p => p.Length is > 0 and <= MaxPhraseLength && p.Any(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPhrases);

    // Hyphens, dashes and runs of space as one space, so "state-sponsored", "state – sponsored" and "state  sponsored" agree.
    // With at, also where in the text each folded character came from.
    private static string Fold(string text, List<int>? at = null)
    {
        var b = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var gap = char.IsWhiteSpace(ch) || IsDash(ch);
            if (gap && (b.Length == 0 || b[^1] == ' ')) continue;
            b.Append(gap ? ' ' : ch);
            at?.Add(i);
        }
        if (b.Length > 0 && b[^1] == ' ')
        {
            b.Length--;
            at?.RemoveAt(at.Count - 1);
        }
        return b.ToString();
    }

    private static bool IsDash(char ch) => CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.DashPunctuation;

    // News names a state behind an attack in a few set ways, which an advertorial doesn't use: a state word joined on with
    // a hyphen or dash ("Iran-sponsored", "DPRK–sponsored"; "Vendor-sponsored" is an advert), a state word just before
    // ("Iranian state sponsored", "Chinese sponsored"), or a state named among the next few words, up to a comma or colon
    // ("sponsored by the North Korean regime", "sponsored by Beijing's MSS", "sponsored by a foreign government"). After
    // "by", where a sponsor's own name goes, a capitalised generic word ("Intelligence", "State") or a people's name
    // ("American Express") isn't enough on its own: see NamesAState. The phrase when the marker at start (in the folded
    // text) begins one, else null.
    private static string? StateSponsor(string text, string folded, List<int> at, int start)
    {
        const string Word = "sponsored";
        if (string.Compare(folded, start, Word, 0, Word.Length, StringComparison.OrdinalIgnoreCase) != 0) return null;
        var o = at[start];
        var before = folded[..start].TrimEnd().Split(' ');
        var prior = before.Length > 1 ? before[^2] : null;
        if (o >= 2 && IsDash(text[o - 1]) && char.IsLetterOrDigit(text[o - 2]))
        {
            var from = o - 2;
            while (from > 0 && char.IsLetterOrDigit(text[from - 1])) from--;
            if (JoinedState(text[from..(o - 1)], prior)) return text[from..(o + Word.Length)];
        }
        var sponsored = folded.Substring(start, Word.Length);
        if (JoinedState(before[^1], prior)) return before[^1] + " " + sponsored;
        var after = folded[(start + Word.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (after.Length < 2 || !after[0].Equals("by", StringComparison.OrdinalIgnoreCase)) return null;
        for (var k = 1; k < Math.Min(after.Length, 6); k++)
        {
            if (NamesAState(after, k) is var end and >= 0) return string.Join(' ', [sponsored, .. after[..(end + 1)]]);
            if (EndsClause(after[k])) return null;
        }
        return null;
    }

    // A word that names a state: a country, its people, its capital or an agency, or "government", "regime" and the like.
    private static bool IsState(string word) =>
        Bare(word) is var w && (Named.Contains(w) || Countries.Contains(w) || Initials.Contains(w) || Peoples.Contains(w) || StateNouns.Contains(w));

    // A state word joined on or just before "sponsored", given the word before it (null when there's none): a
    // capitalised state noun after a capitalised word that isn't a state's is the end of a name ("Threat
    // Intelligence-sponsored"), unlike "Nation-State-sponsored" or "Government-sponsored" at the start.
    private static bool JoinedState(string word, string? prior)
    {
        if (!IsState(word)) return false;
        var w = Bare(word);
        if (!StateNouns.Contains(w) || !char.IsUpper(w[0]) || prior is null || EndsClause(prior)) return true;
        var p = Bare(prior);
        return p.Length == 0 || !char.IsUpper(p[0]) || IsState(p);
    }

    // After "sponsored by", whether after[k] names a state: the index of its last word when it does (k, or k + 1 for
    // "United States"), else -1. A capital, agency or group always does; a country does unless a capitalised word other
    // than a state noun or agency follows it ("China Mobile", "US Bank"; not "Iran's Ministry"), though a possessive
    // always does: "North Korea's Lazarus Group" is the news this is for, so "India's Infosys" is let through. A
    // people's name does only before a state noun ("the North Korean regime", "the Chinese government", not "American
    // Express"). A state noun on its own does in lowercase, after nothing but an article or "foreign" ("a foreign
    // government", "the state", "a nation-state"); capitalised it's a name ("Google Threat Intelligence", "the State of
    // Cloud Security report"), unless "foreign" comes before it ("a Foreign Government").
    private static int NamesAState(string[] after, int k)
    {
        var word = after[k];
        var w = Bare(word);
        if (Named.Contains(w)) return k;
        var end = k;
        if (w.Equals("united", StringComparison.OrdinalIgnoreCase) && !EndsClause(word) && k + 1 < after.Length && Bare(after[k + 1]).Equals("states", StringComparison.OrdinalIgnoreCase))
            word = after[++end];
        var following = end + 1 < after.Length && !EndsClause(word) ? Bare(after[end + 1]) : "";
        var beforeNoun = StateNouns.Contains(following);
        if (end > k || Countries.Contains(w) || Initials.Contains(w))
            return beforeNoun || Named.Contains(following) || following.Length == 0 || !char.IsUpper(following[0]) || Possessive(word) != word ? end : -1;
        if (Peoples.Contains(w)) return beforeNoun ? k : -1;
        if (!StateNouns.Contains(w) || !after[1..k].All(a => !EndsClause(a) && Leads.Contains(Bare(a)))) return -1;
        return char.IsLower(w[0]) || (k > 1 && Bare(after[k - 1]).Equals("foreign", StringComparison.OrdinalIgnoreCase)) ? k : -1;
    }

    // A word that ends with a comma, colon or the like ("Acme:"), past which the sponsor isn't named.
    private static bool EndsClause(string word) => Possessive(word) is { Length: > 0 } bare && !char.IsLetterOrDigit(bare[^1]);

    // A word without the punctuation round it or a possessive "'s": "(Beijing's" as "Beijing".
    private static string Bare(string word)
    {
        var w = word.AsSpan();
        while (w.Length > 0 && !char.IsLetterOrDigit(w[0])) w = w[1..];
        while (w.Length > 0 && !char.IsLetterOrDigit(w[^1])) w = w[..^1];
        return Possessive(w.ToString());
    }

    // "Beijing's" as "Beijing".
    private static string Possessive(string word) => word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || word.EndsWith("’s", StringComparison.OrdinalIgnoreCase) ? word[..^2] : word;

    // Capitals, agencies, groups and states' own initials: no sponsor of an advert goes by these.
    private static readonly HashSet<string> Named = new(
    [
        "prc", "ccp", "beijing", "kremlin", "moscow", "tehran", "irgc", "dprk", "pyongyang", "minsk", "damascus", "hamas", "hezbollah", "houthi", "houthis",
        "mss", "gru", "fsb", "svr", "kgb", "pla", "mois", "rgb",
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Countries = new(
    [
        "china", "russia", "iran", "korea", "belarus", "pakistan", "india", "vietnam", "israel", "syria", "turkey", "arabia", "uae", "qatar",
        "lebanon", "cuba", "venezuela",
    ], StringComparer.OrdinalIgnoreCase);

    // A state's initials, in capitals only ("sponsored by us" is a pronoun): "U.S." as Bare leaves it.
    private static readonly HashSet<string> Initials = new(["US", "U.S", "USA", "UK", "U.K", "EU"], StringComparer.Ordinal);

    // What may come before a state noun after "sponsored by" ("a foreign government", "the state", "its military").
    private static readonly HashSet<string> Leads = new(["a", "an", "the", "its", "their", "foreign", "hostile"], StringComparer.OrdinalIgnoreCase);

    // A people's name, which also starts many a company's ("American Express", "British Airways", "Korean Air").
    private static readonly HashSet<string> Peoples = new(
    [
        "chinese", "russian", "iranian", "korean", "belarusian", "pakistani", "indian", "vietnamese", "israeli", "syrian", "turkish", "saudi",
        "emirati", "qatari", "lebanese", "cuban", "venezuelan", "american", "british",
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> StateNouns = new(
        ["state", "states", "nation", "nations", "government", "governments", "regime", "regimes", "military", "army", "intelligence", "ministry", "junta"],
        StringComparer.OrdinalIgnoreCase);

    // Where the folded phrase appears in the folded text as whole words: a word ends at anything not a letter or digit,
    // and a phrase that starts or ends with punctuation ("[Partner]") needs no boundary on that side.
    private static IEnumerable<(int Start, int End)> Spans(string text, string phrase)
    {
        if (phrase.Length == 0) yield break;
        for (var at = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(phrase, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + phrase.Length;
            var startsWord = at == 0 || !char.IsLetterOrDigit(text[at - 1]) || !char.IsLetterOrDigit(phrase[0]);
            var endsWord = end == text.Length || !char.IsLetterOrDigit(text[end]) || !char.IsLetterOrDigit(phrase[^1]);
            if (startsWord && endsWord) yield return (at, end);
        }
    }
}

/// <summary>A sponsored item a poll left out: its title, the marker and where it was found.</summary>
public sealed record SponsoredSkip(string Title, string Marker, string Field);

/// <summary>What each source's last poll left out as sponsored, kept in memory for Sources (a restart clears it): how many,
/// and the first few, so the filtered-out count says which marker matched. Singleton.</summary>
public sealed class SponsoredLog
{
    public const int Sample = 5;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (int Count, IReadOnlyList<SponsoredSkip> Sample)> _last = new();

    public void Record(Guid sourceId, IReadOnlyList<SponsoredSkip> skipped) => _last[sourceId] = (skipped.Count, [.. skipped.Take(Sample)]);

    /// <summary>How many the source's last poll left out as sponsored, and the first few; (0, []) when none or not known.</summary>
    public (int Count, IReadOnlyList<SponsoredSkip> Sample) For(Guid sourceId) => _last.TryGetValue(sourceId, out var last) ? last : (0, []);
}
