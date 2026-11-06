using System.Text.RegularExpressions;

namespace Tide.Application.Intake;

/// <summary>An inbox item as <see cref="TriageGroups"/> reads it: its headline, feed, date and CVEs.</summary>
public sealed record GroupCandidate(Guid Id, string Title, Guid SourceId, string SourceName, DateTimeOffset At, IReadOnlyList<string> Cves);

/// <summary>
/// Inbox items that look like one event told by several feeds: their ids, newest first; how many feeds told it; the
/// names and amounts the headlines share ("Binance", "$6.3 billion"); and every CVE any of them names.
/// </summary>
public sealed record TriageGroup(IReadOnlyList<Guid> Ids, int Sources, IReadOnlyList<string> Shared, IReadOnlyList<string> Cves)
{
    /// <summary>The newest report's id, the one an article made from the group starts from.</summary>
    public Guid Newest => Ids[0];
}

/// <summary>
/// What <see cref="TriageGroups.Find"/> takes besides the items (EP-84): how far apart two reports of one event can be
/// published, the words an administrator added to the built-in common ones (as <see cref="TriageGroups.Words"/> reads
/// them), and the splits someone made ("Not the same"), whose reports never group with each other again.
/// </summary>
public sealed record TriageGroupOptions
{
    /// <summary>The defaults: <see cref="TriageGroups.DefaultWindow"/>, no extra words, nothing split.</summary>
    public static readonly TriageGroupOptions Default = new();

    public TimeSpan Window { get; init; } = TriageGroups.DefaultWindow;

    /// <summary>Extra common words, in lower case: matched as the built-in ones are, endings and all.</summary>
    public IReadOnlySet<string> Ignored { get; init; } = TriageGroups.NoWords;

    /// <summary>The splits, each as its reports' ids: no two reports of one split pair.</summary>
    public IReadOnlyCollection<IReadOnlyCollection<Guid>> Split { get; init; } = [];
}

/// <summary>
/// "Looks like one event": the inbox's suggested groups, worked out each time the inbox loads and never stored (only a
/// split is: two items someone said aren't the same never pair). Two items published within the window
/// (<see cref="TriageGroupOptions.Window"/>, <see cref="DefaultWindow"/> until an administrator changes it) of each other
/// join when they name a CVE in common, or when their headlines share at least two distinctive tokens, one of them a name
/// both write as one: a capitalised or product-like word ("Binance", "DevOps", "iOS"). The other can be an amount
/// ("$6.3 billion", "16 billion") or any other uncommon word, whatever its case: one outlet writes "business" where
/// another, capitalising every word, writes "Business", and both mean the same thing. Common words, the words every
/// security headline uses (hack, breach, flaw, critical, patch, add...), any an administrator added, and the publisher's
/// own name don't count. A group grows from its tightest pair, held to <see cref="MaxSize"/> items, and every report in
/// it pairs with every other: a roundup that names two events can't chain them into one. It's offered only when it has
/// two or more items from two or more feeds. A reworded headline groups where <see cref="TitleMatch"/>'s one-off intake
/// match didn't, because a few shared names count, not the overlap of every word.
/// </summary>
public static partial class TriageGroups
{
    /// <summary>How far apart two reports of one event can be published, until an administrator changes it
    /// (<see cref="MinWindowHours"/> to <see cref="MaxWindowHours"/> hours).</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(48);

    public const int MinWindowHours = 12, MaxWindowHours = 96;

    /// <summary>The most words an administrator adds, and the longest one.</summary>
    public const int MaxWords = 200, MaxWordLength = 40;

    /// <summary>The most items one group holds; a bigger run keeps its tightest pairs.</summary>
    public const int MaxSize = 8;

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "nor", "of", "to", "in", "on", "at", "for", "from", "by", "with", "as", "is", "are",
        "was", "were", "be", "been", "being", "it", "its", "this", "that", "these", "those", "new", "now", "after", "before",
        "over", "under", "into", "onto", "out", "up", "down", "off", "about", "against", "amid", "among", "via", "per", "than",
        "more", "most", "less", "how", "why", "what", "who", "whom", "when", "where", "which", "while", "not", "no", "all",
        "any", "some", "has", "have", "had", "do", "does", "did", "can", "could", "may", "might", "will", "would", "should",
        "must", "just", "still", "also", "yet", "again", "first", "last", "top", "big", "major", "massive", "huge", "record",
        "says", "said", "say", "tells", "told", "get", "gets", "got", "make", "makes", "made", "take", "takes", "took", "goes",
        "here", "there", "our", "your", "their", "his", "her", "we", "you", "they", "he", "she", "us", "them", "one", "two",
        "three", "week", "weekly", "today", "day", "days", "year", "years", "month", "months", "hour", "hours", "inside",
        "behind", "beyond", "through", "across", "within", "without", "since", "until", "following", "ahead",
        "max", "maximum", "severity", "high", "low", "medium", "potential", "possible", "likely", "alleged", "suspected",
        "latest", "ongoing", "active", "actively", "in-the-wild", "wild", "cve", "cves", "kev", "cvss", "poc",
        "rce", "ai", "llm", "faq", "q&a", "us", "uk", "eu", "un", "north", "south", "east", "west", "korea", "korean", "china",
        "chinese", "russia", "russian", "iran", "iranian", "israel", "israeli", "ukraine", "ukrainian", "american", "europe",
        "european", "global", "worldwide", "government", "federal", "state", "states", "nation", "national", "cisa", "fbi",
        "nsa", "ncsc", "cert", "doj", "police", "authorities", "agency", "agencies", "company", "companies", "firm", "firms",
    };

    // Words every security headline uses, in their plain form; "hacked", "hackers" and "patches" are found by
    // trimming an ending (IsGeneric), so a name that only starts the same way ("Hitachi", "Datadog") still counts.
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "hack", "hacker", "breach", "attack", "attacker", "cyberattack", "flaw", "vulnerability", "vulnerabilities", "vuln",
        "exploit", "security", "cybersecurity", "secure", "critical", "update", "patch", "zero", "bug", "threat", "malware",
        "ransomware", "phish", "phishing", "warn", "warning", "fix", "issue", "risk", "cyber", "alert", "actor", "crypto",
        "cryptocurrency", "theft", "thief", "thieves", "stole", "stolen", "steal", "heist", "drain", "leak", "compromise",
        "affect", "impact", "user", "customer", "account", "data", "password", "credential", "report", "research",
        "researcher", "campaign", "target", "abuse", "fake", "backdoor", "spyware", "botnet", "ddos", "scam", "fraud", "gang",
        "group", "link", "suffer", "probe", "investigate", "investigation", "confirm", "disclose", "reveal", "release", "urge",
        "rush", "scramble", "hit", "expose", "fund", "asset", "money", "victim", "incident", "outage", "disrupt", "infect",
        "deploy", "spread", "unauthenticated", "remote", "code", "execution", "privilege", "escalation", "bypass", "inject",
        "injection", "access", "control", "server", "device", "system", "network", "software", "app", "tool", "platform",
        "service", "vendor", "expert", "analyst", "hallmark", "bear", "sign", "trace", "tie", "blame", "attribute", "arrest",
        "charge", "sanction", "seize", "takedown", "dismantle", "operation", "roundup", "recap", "digest", "million",
        "billion", "thousand", "trillion", "freeze", "lose", "loss", "claim", "admit", "add", "list", "catalog", "catalogue",
        "ransom", "extort", "extortion",
    };

    private static readonly string[] Endings = ["s", "es", "ed", "d", "ing", "er", "ers", "ies"];

    internal static readonly IReadOnlySet<string> NoWords = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The built-in words that never count, as Administration shows them: the plain ones (a, the, says...) and
    /// the ones every security headline uses (hack, breach...), each in order.</summary>
    public static IReadOnlyList<string> StopWords { get; } = [.. Stop.Order(StringComparer.Ordinal)];

    /// <inheritdoc cref="StopWords"/>
    public static IReadOnlyList<string> GenericWords { get; } = [.. Generic.Order(StringComparer.Ordinal)];

    /// <summary>Whether a word in lower case is one the built-in sets ignore already, with an ending trimmed or not.</summary>
    public static bool IsBuiltIn(string word) => Stop.Contains(word) || IsGeneric(word, NoWords);

    private static bool IsGeneric(string key, IReadOnlySet<string> extra)
    {
        bool Common(string w) => Generic.Contains(w) || extra.Contains(w);
        return Common(key) || Endings.Any(e => key.Length > e.Length + 2 && key.EndsWith(e, StringComparison.Ordinal)
            && (Common(key[..^e.Length]) || (e == "ies" && Common(key[..^3] + "y"))));
    }

    // One word an administrator adds: letters, digits and hyphens, starting with a letter or digit.
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N}-]*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ExtraWord();

    private static bool IsValidWord(string word) => word.Length <= MaxWordLength && ExtraWord().IsMatch(word);

    /// <summary>The words in a list as typed or stored (one per line, or separated by semicolons), in lower case, each
    /// once, in order.</summary>
    public static IReadOnlyList<string> ParseWords(string? text) =>
        [.. SponsoredFilter.Parse(text).Select(w => w.ToLowerInvariant()).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The extra words as they're stored ("outage; webinar"), with the built-in ones left out: they're ignored anyway, and
    /// a later release that builds one in mustn't make a saved list fail its check. Throws <see cref="ArgumentException"/>
    /// saying what's wrong: more than one word to an entry, a character other than a letter, digit or hyphen, a word
    /// longer than <see cref="MaxWordLength"/>, or more than <see cref="MaxWords"/>.
    /// </summary>
    public static string NormalizeWords(string? text)
    {
        var words = ParseWords(text);
        if (words.FirstOrDefault(w => !IsValidWord(w)) is { } bad)
            throw new ArgumentException(bad.Any(char.IsWhiteSpace) ? $"One word to a line: '{bad}' is more than one."
                : bad.Length > MaxWordLength ? $"'{bad[..20]}…' is longer than {MaxWordLength} characters."
                : $"'{bad}' isn't a word: use letters, digits and hyphens only.");
        var kept = words.Where(w => !IsBuiltIn(w)).ToList();
        if (kept.Count > MaxWords) throw new ArgumentException($"At most {MaxWords} extra words.");
        return string.Join("; ", kept);
    }

    /// <summary>
    /// The stored extra words as <see cref="TriageGroupOptions.Ignored"/> takes them. A headline's words break at a
    /// hyphen, so a hyphenated one ("zero-click") is ignored as each of its parts. A word that wouldn't pass
    /// <see cref="NormalizeWords"/> (the server's configuration isn't checked) is left out.
    /// </summary>
    public static IReadOnlySet<string> Words(string? stored) =>
        ParseWords(stored).Where(IsValidWord)
            .SelectMany(w => w.Split('-', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);

    // An amount of money: "$6.3 billion", "$6B", "€20m", "$500,000".
    [GeneratedRegex(@"[$€£]\s?\d+(?:[.,]\d+)*(?:\s?(?<scale>thousand|million|billion|trillion|bn|k|m|b)\b)?", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Money();

    // A count with a scale: "16 billion passwords", "2.7 million records".
    [GeneratedRegex(@"\b\d+(?:[.,]\d+)?\s?(?<scale>thousand|million|billion|trillion)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Quantity();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’]s\b)?", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Word();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Space();

    /// <summary>A token a headline offers: its key, how it reads, and whether it's written as a name (not an amount, or a
    /// word in lower case).</summary>
    internal readonly record struct Token(string Key, string Shown, bool Name);

    /// <summary>The distinctive tokens of a headline from <paramref name="publisher"/>, by key, first spelling kept. The
    /// <paramref name="ignored"/> words don't count, as the built-in common ones don't.</summary>
    internal static IReadOnlyDictionary<string, Token> Tokens(string title, string publisher, IReadOnlySet<string>? ignored = null)
    {
        ignored ??= NoWords;
        var found = new Dictionary<string, Token>(StringComparer.Ordinal);
        var own = PublisherWords(publisher);
        var rest = title;
        // Outlets round an amount differently ($6B, $6.3B, $5.8B), so money counts by its scale; a count keeps its number.
        foreach (Match m in Money().Matches(rest))
        {
            var scale = Scale(m.Groups["scale"].Value);
            var key = "$" + (scale.Length > 0 ? scale : new string(m.Value.Where(char.IsDigit).ToArray()));
            found.TryAdd(key, new Token(key, Space().Replace(m.Value.Trim(), " "), Name: false));
        }
        rest = Money().Replace(rest, " ");
        foreach (Match m in Quantity().Matches(rest))
        {
            var shown = Space().Replace(m.Value.Trim(), " ");
            var key = "#" + shown.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
            found.TryAdd(key, new Token(key, shown, Name: false));
        }
        rest = Quantity().Replace(rest, " ");
        foreach (Match m in Word().Matches(rest))
        {
            var word = m.Value.EndsWith("'s", StringComparison.Ordinal) || m.Value.EndsWith("’s", StringComparison.Ordinal) ? m.Value[..^2] : m.Value;
            if (word.Length < 2 || word.All(char.IsDigit)) continue;
            var key = word.ToLowerInvariant();
            if (Stop.Contains(key) || own.Contains(key) || IsGeneric(key, ignored)) continue;
            // Kept as written when the word comes again: a later capitalised spelling makes it a name.
            var token = new Token(key, word, Name: NameLike(word));
            if (!found.TryAdd(key, token) && token.Name && !found[key].Name) found[key] = token;
        }
        return found;
    }

    // Capitalised ("Binance"), capitals inside ("iOS", "DevOps"), or letters with digits ("Log4j", "S3").
    private static bool NameLike(string word) =>
        char.IsUpper(word[0]) || word.Skip(1).Any(char.IsUpper) || (word.Any(char.IsDigit) && word.Any(char.IsLetter));

    private static string Scale(string s) => s.ToLowerInvariant() switch
    {
        "k" or "thousand" => "thousand",
        "m" or "million" => "million",
        "b" or "bn" or "billion" => "billion",
        "trillion" => "trillion",
        _ => "",
    };

    // The publisher's own name, so "The Record" doesn't tie its stories together by "Record".
    private static HashSet<string> PublisherWords(string publisher)
    {
        var words = Word().Matches(publisher).Select(m => m.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        words.Add(new string(publisher.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant());
        return words;
    }

    /// <summary>The groups among <paramref name="items"/>, the newest group first; items in no group aren't listed. With
    /// no <paramref name="options"/>, <see cref="TriageGroupOptions.Default"/>.</summary>
    public static IReadOnlyList<TriageGroup> Find(IReadOnlyList<GroupCandidate> items, TriageGroupOptions? options = null)
    {
        options ??= TriageGroupOptions.Default;
        var window = options.Window;
        var tokens = items.Select(i => Tokens(i.Title, i.SourceName, options.Ignored)).ToArray();
        var cves = items.Select(i => i.Cves.ToHashSet(StringComparer.OrdinalIgnoreCase)).ToArray();
        // Each item's splits, by number. Two items in one split don't pair, so they never share a group: Join asks every
        // member of one run to pair with every member of the other.
        var splitsOf = new Dictionary<Guid, HashSet<int>>();
        foreach (var (split, n) in options.Split.Select((s, n) => (s, n)))
            foreach (var id in split)
                (splitsOf.TryGetValue(id, out var of) ? of : (splitsOf[id] = [])).Add(n);
        bool Apart(int a, int b) => splitsOf.TryGetValue(items[a].Id, out var x) && splitsOf.TryGetValue(items[b].Id, out var y) && x.Overlaps(y);
        var pairs = Pairs(items, tokens, cves, window, Apart);
        var linked = pairs.Select(p => (p.A, p.B)).ToHashSet();
        var root = Join(items.Count, pairs, (a, b) => linked.Contains(a < b ? (a, b) : (b, a)));
        return Enumerable.Range(0, items.Count).GroupBy(i => root[i])
            .Select(g => g.OrderByDescending(i => items[i].At).ThenBy(i => i).ToList())
            .Where(g => g.Count >= 2 && g.Select(i => items[i].SourceId).Distinct().Count() >= 2)
            .Select(g => new TriageGroup(
                g.Select(i => items[i].Id).ToList(),
                g.Select(i => items[i].SourceId).Distinct().Count(),
                Shared(g.Select(i => tokens[i]).ToList()),
                g.SelectMany(i => items[i].Cves).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(g => items.First(i => i.Id == g.Newest).At)
            .ToList();
    }

    // Every two items that pair (published within the window, not split, enough in common), and how much: the cheap
    // checks first.
    private static List<(int A, int B, int Weight, TimeSpan Gap)> Pairs(IReadOnlyList<GroupCandidate> items,
        IReadOnlyDictionary<string, Token>[] tokens, HashSet<string>[] cves, TimeSpan window, Func<int, int, bool> apart)
    {
        var pairs = new List<(int A, int B, int Weight, TimeSpan Gap)>();
        for (var a = 0; a < items.Count; a++)
            for (var b = a + 1; b < items.Count; b++)
            {
                var gap = (items[a].At - items[b].At).Duration();
                if (gap <= window && !apart(a, b) && Weight(tokens[a], tokens[b], cves[a], cves[b]) is var weight and > 0) pairs.Add((a, b, weight, gap));
            }
        return pairs;
    }

    // Runs of pairs, tightest pair first (most in common, then closest in time), so a run held to the cap keeps the
    // closest reports. Two runs join only when every item of one pairs with every item of the other, so membership
    // doesn't chain from item to item through a roundup that names two events. Each item's run, by the index of the
    // item at its root.
    private static int[] Join(int count, List<(int A, int B, int Weight, TimeSpan Gap)> pairs, Func<int, int, bool> paired)
    {
        var parent = Enumerable.Range(0, count).ToArray();
        var members = Enumerable.Range(0, count).Select(i => new List<int> { i }).ToArray();
        int Root(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        foreach (var (a, b, _, _) in pairs.OrderByDescending(p => p.Weight).ThenBy(p => p.Gap))
        {
            var (ra, rb) = (Root(a), Root(b));
            if (ra == rb || members[ra].Count + members[rb].Count > MaxSize) continue;
            if (members[ra].Count < members[rb].Count) (ra, rb) = (rb, ra);
            if (!members[rb].All(m => members[ra].All(n => paired(n, m)))) continue;
            parent[rb] = ra;
            members[ra].AddRange(members[rb]);
        }
        return Enumerable.Range(0, count).Select(Root).ToArray();
    }

    // A shared CVE joins two items outright, and counts above any number of shared words; otherwise they need two
    // shared tokens, one of them written as a name in both (two amounts, or two plain words, don't make one event; nor
    // does a word only a headline in title case capitalises).
    private static int Weight(IReadOnlyDictionary<string, Token> a, IReadOnlyDictionary<string, Token> b, HashSet<string> cvesA, HashSet<string> cvesB)
    {
        var shared = a.Values.Where(t => b.ContainsKey(t.Key)).ToList();
        if (cvesA.Overlaps(cvesB)) return 100 + shared.Count;
        return shared.Count >= 2 && shared.Any(t => t.Name && b[t.Key].Name) ? shared.Count : 0;
    }

    // The tokens two or more of the group's headlines share, the most shared first (names before other words and
    // amounts), as the newest headline spells them.
    private static List<string> Shared(IReadOnlyList<IReadOnlyDictionary<string, Token>> members) =>
        members.SelectMany((m, order) => m.Values.Select(t => (t, order)))
            .GroupBy(x => x.t.Key)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.First().t.Name).ThenBy(g => g.Min(x => x.order))
            .Take(6)
            .Select(g => g.OrderBy(x => x.order).First().t.Shown)
            .ToList();
}
