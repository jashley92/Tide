using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Tide.Application.Assist;

/// <summary>What <see cref="ArticleText.Extract"/> found: the body's text and how it was found ("the page's article
/// markup"), or why nothing usable was (<see cref="Problem"/>, safe to show).</summary>
public sealed record ExtractedText(string Text, string How, string? Problem = null)
{
    public bool Usable => Problem is null;
}

/// <summary>
/// The readable body of an article page, whatever the site's design: TIDE's half of "Fetch the article". It tries, in
/// order, a selector the source's administrator set, the article text the page publishes for search engines (JSON-LD
/// <c>articleBody</c>), the page's own <c>&lt;article&gt;</c> markup, and then a reader-mode scoring pass (Mozilla
/// Readability's approach): menus, adverts, comments and "related" boxes are dropped, every block is scored on its
/// paragraphs, commas and how much of it is links, and the best block is kept with neighbours that score nearly as well.
/// Adverts go first, whichever way the body is then found (<see cref="ArticleCleanup"/>): blocks whose class or id names an
/// ad slot, the selectors an administrator listed, and afterwards the lines that only label one ("Advertisement").
/// Tables and code blocks are kept, since that's where indicators usually are. No script runs: a page built by script
/// comes back too short, and the caller falls back to the provider or to pasting. A page too deeply nested to parse in
/// reasonable time is refused before parsing, and the parse and the rest of the work run under a time budget.
/// </summary>
public static partial class ArticleText
{
    /// <summary>Less than this isn't an article (a teaser, a sign-in page, a shell a script would fill).</summary>
    public const int MinChars = 500;

    /// <summary>The most kept: a long report is cut here, and the caller says so.</summary>
    public const int MaxChars = 40_000;

    /// <summary>The most elements a page may hold open at once (nested, or never closed) before it's refused unread.</summary>
    public const int MaxOpenElements = 512;

    // The most open elements the pre-scan may look back through (or open again), in all, before it refuses the page. A
    // walk for an element none of which is open is skipped, so ordinary pages need little: 4 MB of a long report's markup
    // about 66,000, and 8 MB inside a hundred levels of wrapper divs next to none. One piling up end tags that a scope
    // stops short (each walking back through hundreds of elements to no effect) gets here in well under a second, even on
    // a slow server (each step is a few set lookups: 20 million took 7.6 s on a CI runner).
    internal const long MaxScanWork = 2_000_000;

    /// <summary>How long parsing and reading a page may take, unless the caller says (<c>Intake:ReadSeconds</c>), before it's
    /// refused. An ordinary page at the largest size fetched (8 MB) takes about 2.5 s on a cold start, so this leaves room
    /// for a busy server; a slow one is given longer in the setting.</summary>
    public static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(6);

    // The most <pre> and <listing> start tags in a row: the parser takes each one, and the start tag straight after it,
    // by recursion (to drop a newline that follows), so 300 in a row overflow a thread's stack and take the process
    // down. No real page puts more than a couple together.
    internal const int MaxPreRun = 32;

    // The stack the reading runs on: the parser and the walk through its tree recurse, and a thread the caller has (an
    // IIS worker's can be 256 KB) may not hold what the pre-scan lets through. 511 <pre>s in a row take about 3 MB.
    private const int ReaderStack = 16 * 1024 * 1024;

    // Why a page wasn't read. Each caller adds what to do instead (open the site, or paste the text). The refusals name
    // the reason: a page built too deep isn't a slow server's doing, and one that ran out of time might be.
    private const string TooComplex = "TIDE can't read this page safely: its markup is nested too deeply.";

    private static string TooSlow(TimeSpan budget) =>
        $"TIDE couldn't read this page within {budget.TotalSeconds:0.#} seconds (an administrator can allow longer on Sources).";

    /// <summary>The page's text, read on a thread of its own (see <see cref="ExtractAsync"/>), with the caller waiting for it.</summary>
    public static ExtractedText Extract(byte[] page, string? selector = null, ArticleCleanup? cleanup = null, CancellationToken ct = default) =>
        ExtractAsync(page, selector, ct: ct, cleanup: cleanup).GetAwaiter().GetResult();

    // A string goes to the parser as UTF-8 behind its mark, which outranks any charset the markup declares.
    public static ExtractedText Extract(string html, string? selector = null, ArticleCleanup? cleanup = null, CancellationToken ct = default) =>
        OnReaderThread(() => TooDeep(html) ? new("", "", TooComplex) : Read([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(html)], selector, TimeBudget, cleanup, ct))
            .GetAwaiter().GetResult();

    /// <summary>The page's text, read on a thread of its own with a large stack, so no page can overflow the caller's; the
    /// caller's thread isn't held while it's read (at most <paramref name="budget"/>, or <see cref="TimeBudget"/>, besides
    /// the pre-scan's second or so).</summary>
    public static Task<ExtractedText> ExtractAsync(byte[] page, string? selector = null, TimeSpan? budget = null, ArticleCleanup? cleanup = null, CancellationToken ct = default) =>
        OnReaderThread(() =>
        {
            if (page.Length >= 4 && page[0] == '%' && page[1] == 'P' && page[2] == 'D' && page[3] == 'F')
                return new("", "", "The address is a PDF. Open it and paste its text instead.");
            // Tag names are ASCII, so Latin-1 finds them in any ASCII-compatible encoding; a UTF-16 page says so in its mark.
            var text = page.Length >= 2 && page[0] == 0xFF && page[1] == 0xFE ? Encoding.Unicode.GetString(page)
                : page.Length >= 2 && page[0] == 0xFE && page[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(page)
                : Encoding.Latin1.GetString(page);
            return TooDeep(text) ? new ExtractedText("", "", TooComplex) : Read(page, selector, budget ?? TimeBudget, cleanup, ct);
        });

    // Runs the reading on a new thread with ReaderStack, in the caller's culture; its result, refusal or cancellation
    // comes back through the task.
    internal static Task<ExtractedText> OnReaderThread(Func<ExtractedText> read)
    {
        var done = new TaskCompletionSource<ExtractedText>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var thread = new Thread(() =>
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
            try { done.SetResult(read()); }
            catch (OperationCanceledException e) { done.SetCanceled(e.CancellationToken); }
            catch (Exception e) { done.SetException(e); }
        }, ReaderStack) { IsBackground = true, Name = "TIDE article reader" };
        thread.Start();
        return done.Task;
    }

    /// <summary>Whether <paramref name="selector"/> is a CSS selector the parser accepts (a source's body selector is checked on save).</summary>
    public static bool IsSelector(string selector)
    {
        try
        {
            new HtmlParser().ParseDocument("<html></html>").QuerySelectorAll(selector);
            return true;
        }
        catch (DomException) { return false; }
    }

    // Parses and reads under the time budget: running out of it is a refusal, but the caller's own cancellation isn't.
    // The parser takes the page a piece at a time from a stream that looks at the budget first, so the parse is held to
    // it too: the pre-scan is the quick answer to the usual hostile page, and this is the bound for any it misses.
    internal static ExtractedText Read(byte[] page, string? selector, TimeSpan budget, ArticleCleanup? cleanup, CancellationToken ct)
    {
        using var left = CancellationTokenSource.CreateLinkedTokenSource(ct);
        left.CancelAfter(budget);
        try
        {
            using var stream = new BudgetedStream(page, left.Token);
            return Extract(new HtmlParser().ParseDocument(stream), selector, cleanup ?? ArticleCleanup.Default, left.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new("", "", TooSlow(budget)); }
    }

    // The page as the parser reads it: at most a kilobyte a read, each after a look at the budget. The parser builds the
    // tree as it reads, so a page whose building is slow is stopped within a kilobyte of the budget running out.
    private sealed class BudgetedStream(byte[] page, CancellationToken ct) : MemoryStream(page, writable: false)
    {
        private const int Piece = 1024;

        // Every other read comes here: a stream derived from MemoryStream reads spans through this one.
        public override int Read(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            return base.Read(buffer, offset, Math.Min(count, Piece));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
    }

    // The HTML parser's cost grows with the square of how many elements are open at once (each block's start tag looks
    // back through them), so 200,000 unclosed <div>s would take minutes. This counts them first, in one pass, reading
    // tags as the tokenizer does: a quoted attribute value is skipped whole (a ">" in one doesn't end the tag), and a
    // self-closing slash doesn't stop an HTML element opening, for the parser either. Comments are skipped, and so is
    // script and style text, but only in HTML: inside <svg> or <math> a <style> or <title> is markup like any other, and
    // a tag that breaks out of foreign content (such as <div>) closes it. What's open is kept as the parser keeps it, in
    // outline (see Tree): the next <p>, <li>, <td> or <tr> closes the one before it, a cell's or paragraph's end closes
    // what's inside, and a formatting element a block's end closed opens again in the text that follows. It errs towards
    // counting more than the parser holds open, and the time budget bounds whatever it misses. A run of <pre> and
    // <listing> start tags with nothing between them (not even text) is refused past MaxPreRun.
    internal static bool TooDeep(ReadOnlySpan<char> html)
    {
        var tree = new Tree();
        var i = 0;
        var pres = 0; // <pre> and <listing> start tags in a row
        while (html[i..].IndexOf('<') is var lt and >= 0)
        {
            // Text between tags reopens the formatting elements a block closed; a page built to make this scan slow is refused.
            if (lt > 0 && tree.Reconstruct() || tree.Work > MaxScanWork) return true;
            i += lt + 1;
            var rest = html[i..];
            // "</>" is nothing to the tokenizer, so it doesn't end a run; text, a comment or any other tag does.
            if (rest.StartsWith("/>"))
            {
                i += 2;
                continue;
            }
            var run = lt > 0 ? 0 : pres;
            pres = 0;
            if (rest.StartsWith("!--"))
            {
                // A comment ends at "-->" or "--!>", and "<!-->" is one already.
                var dashes = rest[1..];
                var shut = Earliest(dashes.IndexOf("-->"), dashes.IndexOf("--!>"));
                if (shut < 0) return false;
                i += 1 + shut + (dashes[shut..].StartsWith("--!>") ? 4 : 3);
                continue;
            }
            var end = rest.StartsWith("/");
            var tag = end ? rest[1..] : rest;
            var name = TagName(tag);
            if (name is null) continue;
            var (length, selfClosing) = TagEnd(tag);
            if (length < 0) return false;
            i += (end ? 1 : 0) + length;
            if (!end && name is "pre" or "listing" && (pres = run + 1) > MaxPreRun) return true;
            if (end)
            {
                tree.End(name);
                continue;
            }
            if (tree.Inside is var here and not Content.Html)
            {
                if (!BreakOut.Contains(name))
                {
                    var inside = here == Content.Svg
                        ? name is "foreignobject" or "desc" or "title" ? Content.Html : Content.Svg
                        : name is "mi" or "mo" or "mn" or "ms" or "mtext" ? Content.Html : Content.Math;
                    if (!selfClosing && tree.Push(name, inside, foreign: true)) return true;
                    continue;
                }
                tree.LeaveForeign();
            }
            if (name is "svg" or "math")
            {
                if (tree.Reconstruct() || !selfClosing && tree.Push(name, name == "svg" ? Content.Svg : Content.Math, foreign: true)) return true;
            }
            else if (name == "plaintext") return false;
            else if (RawText.Contains(name))
            {
                if (html[i..].IndexOf("</" + name, StringComparison.OrdinalIgnoreCase) is not (var close and >= 0)) return false;
                i += close;
            }
            else if (tree.Start(name, tag[name.Length..(length - 1)])) return true;
        }
        return false;
    }

    // How an open element's content is read: as HTML, or as SVG or MathML (where raw text and void elements don't apply).
    private enum Content { Html, Svg, Math }

    // The parser's stack of open elements and its list of active formatting elements, kept as the HTML standard's tree
    // builder keeps them, closely enough to count: the implied ends (a <p> closed by the next block, an <li> by the next
    // <li>, a cell by the next cell or row), the scopes an end tag can't reach past (a table, a cell), the end tag that
    // meets a special element first and is ignored, and the formatting elements (<b>, <i>, <a>...) that a block's end
    // closes but the next text opens again, at most three alike. The adoption agency's moves are cut to taking the
    // misnested element off the stack, which leaves this holding open at least what the parser does.
    private sealed class Tree
    {
        private sealed class Node(string name, Content inside, bool foreign, string key)
        {
            public string Name { get; } = name;
            public Content Inside { get; } = inside;
            public bool Foreign { get; } = foreign;
            public string Key { get; } = key; // a formatting element's attributes: of those alike, three at most are reopened
            public bool Open { get; set; } = true;
        }

        private readonly List<Node> open = [];
        private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
        private readonly List<Node?> formatting = []; // null is a marker (a cell, caption or object): what's before it stays outside

        public Content Inside => open.Count == 0 ? Content.Html : open[^1].Inside;

        // How many open elements the walks back through the stack and the list have looked at, in all.
        public long Work { get; private set; }

        // Opens an element, and says whether that's one too many.
        public bool Push(string name, Content inside, bool foreign = false, string key = "")
        {
            var node = new Node(name, inside, foreign, key);
            open.Add(node);
            counts[name] = counts.GetValueOrDefault(name) + 1;
            if (!foreign && Formatting.Contains(name))
            {
                // Noah's Ark: a fourth formatting element alike since the last marker drops the earliest from the list.
                var alike = 0;
                var earliest = -1;
                for (var f = formatting.Count - 1; f >= 0 && formatting[f] is { } other; f--, Work++)
                    if (other.Name == name && other.Key == key && ++alike >= 3) earliest = f;
                if (earliest >= 0) formatting.RemoveAt(earliest);
                formatting.Add(node);
            }
            else if (!foreign && Markers.Contains(name)) formatting.Add(null);
            return open.Count > MaxOpenElements;
        }

        // An HTML start tag, once what it implies has closed; it says whether that's one too many.
        public bool Start(string name, ReadOnlySpan<char> attributes)
        {
            if (Ignored.Contains(name)) return false;
            switch (name)
            {
                case "li": CloseItem("li"); break;
                case "dd" or "dt": CloseItem("dd", "dt"); break;
                case "td" or "th" or "tr" or "tbody" or "thead" or "tfoot" or "caption" or "colgroup" or "col":
                    // A table part closes the cell, row or section before it, back to its table; outside a table it's ignored.
                    if (!IsOpen("table", "td", "th", "caption", "tr", "tbody", "thead", "tfoot")) return false;
                    var at = Last(n => n.Name == "table" || name switch
                    {
                        "td" or "th" => n.Name is "td" or "th" or "caption" or "tr" or "tbody" or "thead" or "tfoot",
                        "tr" => n.Name is "tr" or "tbody" or "thead" or "tfoot",
                        _ => false,
                    });
                    if (at < 0) return false;
                    var same = name is "td" or "th" ? open[at].Name is "td" or "th" or "caption" : name == "tr" && open[at].Name == "tr";
                    PopTo(same ? at : at + 1);
                    break;
                case "table":
                    // A table straight inside a table (not in a cell) closes it.
                    if (IsOpen("table") && Last(n => n.Name is "table" or "td" or "th" or "caption") is var table and >= 0 && open[table].Name == "table")
                        PopTo(table);
                    break;
                case "a":
                    Adopt("a", always: true);
                    break;
                case "button" or "nobr":
                    if (InScope(name) is var again and >= 0)
                        if (name == "nobr") Adopt("nobr");
                        else PopTo(again);
                    break;
                case "option" or "optgroup":
                    if (open.Count > 0 && open[^1].Name == "option") PopTo(open.Count - 1);
                    break;
            }
            if (ClosesP.Contains(name) || name is "li" or "dd" or "dt") CloseP();
            if (IsHeading(name) && open.Count > 0 && IsHeading(open[^1].Name)) PopTo(open.Count - 1);
            if (!ClosesP.Contains(name) && !Unreopening.Contains(name) && Reconstruct()) return true;
            if (Void.Contains(name)) return false;
            return Push(name, Content.Html, key: Formatting.Contains(name) ? attributes.Trim().TrimEnd('/').TrimEnd().ToString() : "");
        }

        // An end tag: back to its element when the parser would get there, otherwise nothing.
        public void End(string name)
        {
            // In SVG or MathML an end tag closes its element, as far down as foreign content goes; past that it's HTML's.
            if (Last(n => !n.Foreign || n.Name == name) is var foreign and >= 0 && open[foreign].Foreign)
            {
                PopTo(foreign);
                return;
            }
            if (Formatting.Contains(name) && Adopt(name)) return;
            if (IsHeading(name))
            {
                if (IsOpen("h1", "h2", "h3", "h4", "h5", "h6") && Scope(IsHeading) is var heading and >= 0) PopTo(heading);
                return;
            }
            if (counts.GetValueOrDefault(name) == 0) return;
            if (name is "td" or "th" or "tr" or "tbody" or "thead" or "tfoot" or "caption" or "table")
            {
                // A table's parts close only inside their own table.
                if (Last(n => n.Name == name || n.Name == "table") is var part and >= 0 && open[part].Name == name) PopTo(part);
                return;
            }
            if (Special.Contains(name))
            {
                var block = name switch
                {
                    "p" => InScope("p", "button"),
                    "li" => InScope("li", "ol", "ul"),
                    _ => InScope(name),
                };
                if (block >= 0) PopTo(block);
                return;
            }
            // Any other end tag closes its element, unless it meets a special element first.
            if (Last(n => n.Name == name || Special.Contains(n.Name)) is var other and >= 0 && open[other].Name == name) PopTo(other);
        }

        // Before text or an inline start tag, the formatting elements a block's end closed open again, in order. It says
        // whether that's one too many.
        public bool Reconstruct()
        {
            if (formatting.Count == 0 || formatting[^1] is not { Open: false }) return false;
            var from = formatting.Count - 1;
            while (from > 0 && formatting[from - 1] is { Open: false }) from--;
            // Each one opened again is work too: the elements a page has opened again and again are what its tree is made of.
            for (var f = from; f < formatting.Count; f++, Work++)
            {
                var closed = formatting[f]!;
                open.Add(formatting[f] = new Node(closed.Name, Content.Html, false, closed.Key));
                counts[closed.Name] = counts.GetValueOrDefault(closed.Name) + 1;
                if (open.Count > MaxOpenElements) return true;
            }
            return false;
        }

        // A tag that breaks out of SVG or MathML closes it.
        public void LeaveForeign()
        {
            while (open.Count > 0 && open[^1].Inside != Content.Html) PopTo(open.Count - 1);
        }

        // Closes the elements from position "at" up. A cell, caption or object closed takes the formatting opened in it.
        private void PopTo(int at)
        {
            while (open.Count > at)
            {
                var node = open[^1];
                open.RemoveAt(open.Count - 1);
                Closed(node);
                if (node.Foreign || !Markers.Contains(node.Name)) continue;
                while (formatting.Count > 0)
                {
                    var last = formatting[^1];
                    formatting.RemoveAt(formatting.Count - 1);
                    if (last is null) break;
                }
            }
        }

        private void Closed(Node node)
        {
            node.Open = false;
            counts[node.Name]--;
        }

        // Whether any of these is open: when none is, the walk that looks for one is skipped, so a block's start tag (which
        // looks for a <p> to close) costs nothing on a page with none open, however deep it is.
        private bool IsOpen(params ReadOnlySpan<string> names)
        {
            foreach (var name in names)
                if (counts.GetValueOrDefault(name) > 0) return true;
            return false;
        }

        // Where the innermost open "name" is, when it's nearer than a scope's edge (see Scope), or -1.
        private int InScope(string name, string? edge = null, string? edge2 = null) => IsOpen(name) ? Scope(n => n == name, edge, edge2) : -1;

        // Where the innermost open element "match" finds is, when it's nearer than a scope's edge (a table, a cell, an
        // object, or one of those given), or -1.
        private int Scope(Func<string, bool> match, string? edge = null, string? edge2 = null) =>
            Last(n => match(n.Name) || Boundaries.Contains(n.Name) || n.Name == edge || n.Name == edge2) is var f and >= 0 && match(open[f].Name) ? f : -1;

        // The innermost open element from position "from" up that "stop" stops at, or -1. Each one looked at is work.
        private int Last(Func<Node, bool> stop, int from = 0)
        {
            for (var f = open.Count - 1; f >= from; f--, Work++)
                if (stop(open[f])) return f;
            return -1;
        }

        private void CloseP()
        {
            if (InScope("p", "button") is var p and >= 0) PopTo(p);
        }

        // An <li> closes an open <li> (a <dd> or <dt> one of those) unless a block other than <address>, <div> or <p> is nearer.
        private void CloseItem(string item, string? other = null)
        {
            if (!IsOpen(item) && (other is null || !IsOpen(other))) return;
            if (Last(n => n.Name == item || n.Name == other || Special.Contains(n.Name) && n.Name is not ("address" or "div" or "p")) is var f and >= 0
                && open[f].Name is var found && (found == item || found == other))
                PopTo(f);
        }

        // The adoption agency, cut down: a formatting element's end closes it and what's inside, unless a special element
        // is. Then the element only leaves the stack: the parser moves the special one out from under it, which is no
        // deeper. An <a> start does the same for an <a> still open, and takes it off the stack whatever its scope. It
        // says whether the element was in the list at all (when it isn't, its end tag is an ordinary one).
        private bool Adopt(string name, bool always = false)
        {
            var listed = formatting.Count - 1;
            for (; listed >= 0 && formatting[listed] is { } other && other.Name != name; listed--) Work++;
            if (listed < 0 || formatting[listed] is not { } element) return false;
            var at = element.Open ? Last(n => n == element) : -1;
            if (!always && at >= 0 && Last(n => Boundaries.Contains(n.Name), at + 1) >= 0) return true; // out of scope: ignored
            formatting.RemoveAt(listed);
            if (at < 0) return true;
            if (always || Last(n => Special.Contains(n.Name), at + 1) >= 0)
            {
                open.RemoveAt(at);
                Closed(element);
            }
            else PopTo(at);
            return true;
        }

        private static bool IsHeading(string name) => name is ['h', >= '1' and <= '6'];
    }

    private static int Earliest(int a, int b) => a < 0 ? b : b < 0 ? a : Math.Min(a, b);

    // A tag's name as the tokenizer reads it, up to a space, "/" or ">", lower-cased, or null when what follows "<" isn't a tag.
    private static string? TagName(ReadOnlySpan<char> tag)
    {
        if (tag.IsEmpty || !char.IsAsciiLetter(tag[0])) return null;
        var n = 1;
        while (n < tag.Length && !IsSpace(tag[n]) && tag[n] is not ('/' or '>')) n++;
        return tag[..n].ToString().ToLowerInvariant();
    }

    // How long a tag is, from its name to just past its ">", and whether it closed itself with "/>", following the
    // tokenizer's attribute states; a length of -1 when the page ends inside it.
    private static (int Length, bool SelfClosing) TagEnd(ReadOnlySpan<char> tag)
    {
        var i = 1;
        while (i < tag.Length && !IsSpace(tag[i]) && tag[i] is not ('/' or '>')) i++;
        var named = false; // just after an attribute's name, where "=" starts its value
        while (i < tag.Length)
        {
            var c = tag[i++];
            if (c == '>') return (i, false);
            if (c == '/')
            {
                if (i < tag.Length && tag[i] == '>') return (i + 1, true);
                named = false;
            }
            else if (IsSpace(c)) { /* between attributes, or before an "=" */ }
            else if (c == '=' && named)
            {
                while (i < tag.Length && IsSpace(tag[i])) i++;
                if (i < tag.Length && tag[i] is '"' or '\'')
                {
                    var quote = tag[i];
                    if (tag[(i + 1)..].IndexOf(quote) is not (var shut and >= 0)) return (-1, false);
                    i += shut + 2;
                }
                else
                    while (i < tag.Length && !IsSpace(tag[i]) && tag[i] != '>') i++;
                named = false;
            }
            else
            {
                // An attribute's name runs to a space, "/", ">" or "=" (a "=" or quote it starts with is part of it).
                while (i < tag.Length && !IsSpace(tag[i]) && tag[i] is not ('/' or '>' or '=')) i++;
                named = true;
            }
        }
        return (-1, false);
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    // Start tags that open nothing: void elements, and the document's own (the parser already has those open).
    private static readonly HashSet<string> Void =
        ["area", "base", "basefont", "bgsound", "br", "col", "embed", "hr", "img", "image", "input", "keygen", "link", "meta", "param", "source", "track", "wbr"];

    private static readonly HashSet<string> Ignored = ["html", "head", "body", "frameset", "frame"];

    // Start tags that close an open <p> first.
    private static readonly HashSet<string> ClosesP =
    [
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "fieldset", "figcaption", "figure",
        "footer", "header", "hgroup", "main", "menu", "nav", "ol", "p", "search", "section", "summary", "ul", "h1", "h2", "h3", "h4", "h5", "h6",
        "pre", "listing", "form", "table", "hr", "xmp",
    ];

    // Start tags (besides those) before which closed formatting elements aren't opened again.
    private static readonly HashSet<string> Unreopening =
    [
        "li", "dd", "dt", "caption", "col", "colgroup", "tbody", "td", "tfoot", "th", "thead", "tr", "html", "head", "body", "frameset", "frame",
        "base", "basefont", "bgsound", "link", "meta", "template", "textarea", "rb", "rp", "rt", "rtc",
    ];

    // The formatting elements, and the elements that keep what's opened in them from reopening outside.
    private static readonly HashSet<string> Formatting = ["a", "b", "big", "code", "em", "font", "i", "nobr", "s", "small", "strike", "strong", "tt", "u"];

    private static readonly HashSet<string> Markers = ["applet", "caption", "marquee", "object", "td", "th", "template"];

    // The standard's "special" elements: an ordinary end tag that meets one before its own element is ignored.
    private static readonly HashSet<string> Special =
    [
        "address", "applet", "area", "article", "aside", "base", "basefont", "bgsound", "blockquote", "body", "br", "button", "caption",
        "center", "col", "colgroup", "dd", "details", "dir", "div", "dl", "dt", "embed", "fieldset", "figcaption", "figure", "footer", "form",
        "frame", "frameset", "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hgroup", "hr", "html", "iframe", "img", "input", "keygen",
        "li", "link", "listing", "main", "marquee", "menu", "meta", "nav", "noembed", "noframes", "noscript", "object", "ol", "p", "param",
        "plaintext", "pre", "script", "search", "section", "select", "source", "style", "summary", "table", "tbody", "td", "template",
        "textarea", "tfoot", "th", "thead", "title", "tr", "track", "ul", "wbr", "xmp",
        "mi", "mo", "mn", "ms", "mtext", "annotation-xml", "foreignobject", "desc",
    ];

    // The edges of an element's scope: an end tag doesn't reach past them.
    private static readonly HashSet<string> Boundaries =
        ["applet", "caption", "html", "table", "td", "th", "marquee", "object", "template", "mi", "mo", "mn", "ms", "mtext", "annotation-xml", "foreignobject", "desc", "title"];

    // Elements whose content is text up to their end tag (in HTML; in SVG and MathML they're ordinary elements).
    private static readonly HashSet<string> RawText = ["script", "style", "textarea", "title", "xmp", "iframe", "noembed", "noframes"];

    // Start tags that close any SVG or MathML open around them and carry on as HTML.
    private static readonly HashSet<string> BreakOut =
    [
        "b", "big", "blockquote", "body", "br", "center", "code", "dd", "div", "dl", "dt", "em", "embed", "h1", "h2", "h3", "h4", "h5", "h6",
        "head", "hr", "i", "img", "li", "listing", "menu", "meta", "nobr", "ol", "p", "pre", "ruby", "s", "small", "span", "strong", "strike",
        "sub", "sup", "table", "tt", "u", "ul", "var",
    ];

    private static ExtractedText Extract(IDocument doc, string? selector, ArticleCleanup cleanup, CancellationToken ct)
    {
        RemoveAds(doc, cleanup, ct);
        if (!string.IsNullOrWhiteSpace(selector))
        {
            IHtmlCollection<IElement> picked;
            try { picked = doc.QuerySelectorAll(selector.Trim()); }
            catch (DomException) { return new("", "", $"The source's article body selector “{selector.Trim()}” isn't a valid CSS selector."); }
            if (picked.Length > 0) return Judge(string.Join("\n\n", picked.Select(e => Render(e, ct))), "the source's selector", cleanup);
        }
        if (FromLinkedData(doc) is { Length: >= MinChars } published) return Judge(Clean(published), "the article text the page publishes", cleanup);

        Strip(doc, ct);
        var articles = doc.QuerySelectorAll("article, [itemprop=articleBody]").Select(e => (e, text: Render(e, ct))).Where(x => x.text.Length >= MinChars).ToList();
        if (articles.Count > 0) return Judge(articles.MaxBy(x => x.text.Length).text, "the page's article markup", cleanup);

        return doc.Body is { } body && Best(body, ct) is { } best ? Judge(best, "reader-mode scoring", cleanup) : Judge("", "reader-mode scoring", cleanup);
    }

    // Too short, or a sign-in or subscription wall: refused with the reason; otherwise kept, cut at MaxChars. The lines
    // that only label an advert go first, so they count towards neither.
    private static ExtractedText Judge(string text, string how, ArticleCleanup cleanup)
    {
        text = DropLabels(text, cleanup).Trim();
        if (text.Length < 1500 && WallRegex().IsMatch(text))
            return new(text, how, "The page asks you to sign in or subscribe. Paste the article instead.");
        if (text.Length < MinChars)
            return new(text, how, $"Only {text.Length.ToString("N0", CultureInfo.CurrentCulture)} characters came back; the page is probably built by script.");
        return new(text.Length > MaxChars ? text[..MaxChars] : text, how);
    }

    // schema.org's articleBody, from any JSON-LD block (alone, in a list, or in an @graph).
    private static string? FromLinkedData(IDocument doc)
    {
        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (FindBody(json.RootElement, 0) is { } body) return body;
            }
            catch (JsonException) { /* a broken block: try the next */ }
        }
        return null;
    }

    private static string? FindBody(JsonElement e, int depth)
    {
        if (depth > 6) return null;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("articleBody", out var b) && b.ValueKind == JsonValueKind.String) return b.GetString();
                foreach (var p in e.EnumerateObject())
                    if (FindBody(p.Value, depth + 1) is { } found) return found;
                return null;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (FindBody(item, depth + 1) is { } found) return found;
                return null;
            default:
                return null;
        }
    }

    // Takes out what's never the article: scripts and styles, navigation, headers and footers, forms, and blocks whose
    // class or id reads as a sidebar, comments, sharing, adverts or "related" (unless it also reads as the content).
    private static void Strip(IDocument doc, CancellationToken ct)
    {
        foreach (var e in doc.QuerySelectorAll("script, style, noscript, template, svg, iframe, form, nav, header, footer, aside, button, select, " +
                                                "[role=navigation], [role=banner], [role=contentinfo], [role=complementary], [aria-hidden=true], [hidden]").ToList())
            e.Remove();
        foreach (var e in doc.QuerySelectorAll("[class], [id]").ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (e.LocalName is "body" or "html" or "article" or "main") continue;
            var names = Names(e);
            if (UnlikelyRegex().IsMatch(names) && !LikelyRegex().IsMatch(names)) e.Remove();
        }
    }

    private static string Names(IElement e) => (e.ClassName ?? "") + " " + (e.Id ?? "");

    /// <summary>The most text (outside links) a block named as an advert may hold and still be taken out by its name: past
    /// this it's more likely a wrapper with a careless name around the article itself.</summary>
    internal const int MaxAdText = 1000;

    // Takes out the adverts, before the body is looked for: blocks whose class or id names an ad slot (as a whole name,
    // so "header", "shadow" and "download" aren't), unless one holds more than MaxAdText of its own text, and whatever
    // the listed selectors find. A selector the parser refuses is passed over (they're checked as they're saved).
    private static void RemoveAds(IDocument doc, ArticleCleanup cleanup, CancellationToken ct)
    {
        foreach (var e in doc.QuerySelectorAll("[class], [id]").ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (e.LocalName is "body" or "html" or "main" || e.ParentElement is null) continue;
            if (IsAdName(e) && OwnText(e, ct) <= MaxAdText) e.Remove();
        }
        foreach (var selector in cleanup.Selectors)
        {
            ct.ThrowIfCancellationRequested();
            List<IElement> found;
            try { found = [.. doc.QuerySelectorAll(selector)]; }
            catch (DomException) { continue; }
            foreach (var e in found)
                if (e.LocalName is not ("body" or "html") && e.ParentElement is not null) e.Remove();
        }
    }

    // Whether one of the element's class names, or its id, names an advert slot: "ad", "ads", "advert", "ad-slot",
    // "inline-ad", "adunit", "taboola"... A name that says whether the page has adverts ("has-ads", "no-ads", "ad-free")
    // doesn't.
    internal static bool IsAdName(IElement e)
    {
        foreach (var name in (e.ClassName ?? "").Split(' ', '\t', '\n', '\r', '\f').Append(e.Id ?? ""))
            if (name.Length > 1 && AdNameRegex().IsMatch(name) && !NotAnAdRegex().IsMatch(name)) return true;
        return false;
    }

    // An element's text outside its links.
    private static int OwnText(IElement e, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Squash(e.TextContent).Length - e.QuerySelectorAll("a").Sum(a => Squash(a.TextContent).Length);
    }

    // The lines that only label an advert ("Advertisement", "Story continues below"), taken out of the text found.
    private static string DropLabels(string text, ArticleCleanup cleanup) =>
        cleanup.Labels.Count == 0 ? text : Clean(string.Join("\n", text.Split('\n').Where(line => !cleanup.IsLabel(line))));

    // Reader-mode scoring: each paragraph-like block scores 1 + its commas + a point per 100 characters (at most 3); the
    // score goes to its parent in full and its grandparent by half. A parent's own class or id adds or takes 25, and its
    // total is cut by its link density. The best one is kept, with siblings that score at least a fifth as well or are
    // themselves a solid paragraph.
    private static string? Best(IElement body, CancellationToken ct)
    {
        var scores = new Dictionary<IElement, double>();
        foreach (var p in body.QuerySelectorAll("p, pre, td, blockquote, li, h2, h3"))
        {
            ct.ThrowIfCancellationRequested();
            var text = Squash(p.TextContent);
            if (text.Length < 25) continue;
            var points = 1 + text.Count(c => c == ',') + Math.Min(text.Length / 100, 3);
            if (p.ParentElement is { } parent)
            {
                scores[parent] = scores.GetValueOrDefault(parent, Weight(parent)) + points;
                if (parent.ParentElement is { } grand) scores[grand] = scores.GetValueOrDefault(grand, Weight(grand)) + points / 2.0;
            }
        }
        if (scores.Count == 0) return null;
        var ranked = scores.Select(kv => (e: kv.Key, score: kv.Value * (1 - LinkDensity(kv.Key, ct)))).ToList();
        var top = ranked.MaxBy(x => x.score);
        var threshold = Math.Max(10, top.score * 0.2);
        var kept = new List<IElement>();
        IEnumerable<IElement> siblings = top.e.ParentElement is { } parentOfTop ? parentOfTop.Children : [top.e];
        foreach (var sibling in siblings)
        {
            if (sibling == top.e) { kept.Add(sibling); continue; }
            ct.ThrowIfCancellationRequested();
            var score = ranked.FirstOrDefault(x => x.e == sibling).score;
            var text = Squash(sibling.TextContent);
            if (score >= threshold || (sibling.LocalName == "p" && text.Length > 80 && LinkDensity(sibling, ct) < 0.25)) kept.Add(sibling);
        }
        return string.Join("\n\n", kept.Select(e => Render(e, ct)));
    }

    private static double Weight(IElement e)
    {
        var w = e.LocalName switch { "div" or "section" or "main" => 5, "pre" or "td" or "blockquote" => 3, "form" or "ul" or "ol" => -3, _ => 0 };
        var names = Names(e);
        if (LikelyRegex().IsMatch(names)) w += 25;
        if (UnlikelyRegex().IsMatch(names)) w -= 25;
        return w;
    }

    private static double LinkDensity(IElement e, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var all = Squash(e.TextContent).Length;
        if (all == 0) return 1;
        var links = e.QuerySelectorAll("a").Sum(a => Squash(a.TextContent).Length);
        return Math.Min(1, (double)links / all);
    }

    // An element's text with its block structure: paragraphs and headings on lines of their own, list items as "- ",
    // table rows with their cells joined by " | ", and code blocks as they are.
    private static string Render(IElement root, CancellationToken ct)
    {
        var sb = new StringBuilder();
        Walk(root, sb, ct);
        return Clean(sb.ToString());
    }

    private static void Walk(INode node, StringBuilder sb, CancellationToken ct)
    {
        foreach (var child in node.ChildNodes)
        {
            ct.ThrowIfCancellationRequested();
            if (child is IText t) { sb.Append(t.Data); continue; }
            if (child is not IElement e) continue;
            switch (e.LocalName)
            {
                case "br": sb.Append('\n'); break;
                case "pre": sb.Append("\n\n").Append(e.TextContent.Trim('\n')).Append("\n\n"); break;
                case "li": sb.Append("\n- "); Walk(e, sb, ct); sb.Append('\n'); break;
                case "tr":
                    sb.Append('\n').AppendJoin(" | ", e.Children.Where(c => c.LocalName is "td" or "th").Select(c => Squash(c.TextContent))).Append('\n');
                    break;
                case "p" or "div" or "section" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "table" or "ul" or "ol" or "figure" or "dl" or "dd" or "dt":
                    sb.Append("\n\n"); Walk(e, sb, ct); sb.Append("\n\n"); break;
                default: Walk(e, sb, ct); break;
            }
        }
    }

    // Runs of spaces become one, three or more line breaks become two, and each line is trimmed.
    internal static string Clean(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => SpacesRegex().Replace(l, " ").Trim());
        return BlankLinesRegex().Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    private static string Squash(string text) => SpacesRegex().Replace(text, " ").Trim();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();

    [GeneratedRegex(@"comment|sidebar|promo|related|share|social|newsletter|subscribe|advert|\bads?\b|ad-|sponsor|cookie|banner|footer|masthead|\bnav|menu|popup|modal|breadcrumb|pagination|author-bio|recommend|trending", RegexOptions.IgnoreCase)]
    private static partial Regex UnlikelyRegex();

    [GeneratedRegex(@"article|body|content|entry|main|post|story|text|blog", RegexOptions.IgnoreCase)]
    private static partial Regex LikelyRegex();

    // An advert slot's class name or id, as a whole: "ad", "ads", "advert(s)", "advertisement", one starting "ad-" or
    // "ad_", ending "-ad(s)" or "_ad(s)", or with "-ad-" inside ("inline-ad", "ad-slot", "story_ad_1"), the ad servers' and
    // widgets' own names (adslot, adunit, adsbygoogle, dfp, gpt-ad, Taboola, Outbrain...), and sponsored-link boxes.
    [GeneratedRegex(@"^(ads?|adverts?|advertisement|advertising|advertorial|ad[-_].*|.*[-_]ads?|.*[-_]ads?[-_].*|.*[-_]advert.*|ad(slot|unit|box|wrap|wrapper|container|zone|space|block|banner|label|placeholder|holder)s?[-_\d]?.*|.*adsbygoogle.*|dfp[-_].*|.*gpt[-_]ad.*|google[-_]?ads?([-_].*)?|mpu([-_].*)?|.*taboola.*|.*outbrain.*|teads.*|connatix.*|dianomi.*|.*native[-_]?ad.*|sponsored[-_](content|post|links?|stories)|promoted[-_](content|stories|links?))$", RegexOptions.IgnoreCase)]
    private static partial Regex AdNameRegex();

    // Names with "ad" in that describe the page, not a slot in it: "has-ads", "no-ads", "ad-free", "ads-enabled".
    [GeneratedRegex(@"^(has|no|with|without|show|hide|is|not)[-_]ads?$|^ads?[-_](free|enabled|disabled|off|on|loaded|lazy|ready)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotAnAdRegex();

    [GeneratedRegex(@"sign in to (continue|read)|log ?in to (continue|read)|subscribe to (continue|read|unlock)|create a free account|for subscribers only|this content is for (members|subscribers)|enable javascript|javascript is (disabled|required)", RegexOptions.IgnoreCase)]
    private static partial Regex WallRegex();
}
