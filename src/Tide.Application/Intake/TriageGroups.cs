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
/// "Looks like one event": the inbox's suggested groups, worked out each time the inbox loads and never stored. Two
/// items published within <see cref="Window"/> of each other join when they name a CVE in common, or when their
/// headlines share at least two distinctive tokens, one of them a name: a capitalised or product-like word ("Binance",
/// "DevOps", "iOS"), or an amount ("$6.3 billion", "16 billion"). Common words, the words every security headline uses
/// (hack, breach, flaw, critical, patch, add...) and the publisher's own name don't count. A group grows from its
/// tightest pair, held to <see cref="MaxSize"/> items, and every report in it pairs with every other: a roundup that
/// names two events can't chain them into one. It's offered only when it has two or more items from two
/// or more feeds. A reworded headline groups where <see cref="TitleMatch"/>'s one-off intake
/// match didn't, because a few shared names count, not the overlap of every word.
/// </summary>
public static partial class TriageGroups
{
    /// <summary>How far apart two reports of one event can be published.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(48);

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
    };

    private static readonly string[] Endings = ["s", "es", "ed", "d", "ing", "er", "ers", "ies"];

    private static bool IsGeneric(string key) =>
        Generic.Contains(key) || Endings.Any(e => key.Length > e.Length + 2 && key.EndsWith(e, StringComparison.Ordinal)
            && (Generic.Contains(key[..^e.Length]) || (e == "ies" && Generic.Contains(key[..^3] + "y"))));

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

    /// <summary>A token a headline offers: its key, how it reads, and whether it's a name (not an amount).</summary>
    internal readonly record struct Token(string Key, string Shown, bool Name);

    /// <summary>The distinctive tokens of a headline from <paramref name="publisher"/>, by key, first spelling kept.</summary>
    internal static IReadOnlyDictionary<string, Token> Tokens(string title, string publisher)
    {
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
            if (word.Length < 2 || word.All(char.IsDigit) || !NameLike(word)) continue;
            var key = word.ToLowerInvariant();
            if (Stop.Contains(key) || own.Contains(key) || IsGeneric(key)) continue;
            found.TryAdd(key, new Token(key, word, Name: true));
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

    /// <summary>The groups among <paramref name="items"/>, the newest group first; items in no group aren't listed.</summary>
    public static IReadOnlyList<TriageGroup> Find(IReadOnlyList<GroupCandidate> items)
    {
        var tokens = items.Select(i => Tokens(i.Title, i.SourceName)).ToArray();
        var cves = items.Select(i => i.Cves.ToHashSet(StringComparer.OrdinalIgnoreCase)).ToArray();
        var root = Join(items.Count, Pairs(items, tokens, cves), (a, b) => Weight(tokens[a], tokens[b], cves[a], cves[b]) > 0
            && (items[a].At - items[b].At).Duration() <= Window);
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

    // Every two items published within the window that have enough in common, and how much.
    private static List<(int A, int B, int Weight, TimeSpan Gap)> Pairs(IReadOnlyList<GroupCandidate> items,
        IReadOnlyDictionary<string, Token>[] tokens, HashSet<string>[] cves)
    {
        var pairs = new List<(int A, int B, int Weight, TimeSpan Gap)>();
        for (var a = 0; a < items.Count; a++)
            for (var b = a + 1; b < items.Count; b++)
            {
                var gap = (items[a].At - items[b].At).Duration();
                if (gap <= Window && Weight(tokens[a], tokens[b], cves[a], cves[b]) is var weight and > 0) pairs.Add((a, b, weight, gap));
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
    // shared tokens, one of them a name (two amounts alone don't make one event).
    private static int Weight(IReadOnlyDictionary<string, Token> a, IReadOnlyDictionary<string, Token> b, HashSet<string> cvesA, HashSet<string> cvesB)
    {
        var shared = a.Values.Where(t => b.ContainsKey(t.Key)).ToList();
        if (cvesA.Overlaps(cvesB)) return 100 + shared.Count;
        return shared.Count >= 2 && shared.Any(t => t.Name) ? shared.Count : 0;
    }

    // The tokens two or more of the group's headlines share, the most shared first (names before amounts), as the newest
    // headline spells them.
    private static List<string> Shared(IReadOnlyList<IReadOnlyDictionary<string, Token>> members) =>
        members.SelectMany((m, order) => m.Values.Select(t => (t, order)))
            .GroupBy(x => x.t.Key)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.First().t.Name).ThenBy(g => g.Min(x => x.order))
            .Take(6)
            .Select(g => g.OrderBy(x => x.order).First().t.Shown)
            .ToList();
}
