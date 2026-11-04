using Tide.Application.Intake;
using Tide.Domain.Entities;

namespace Tide.Application.Assist;

/// <summary>
/// What <see cref="ArticleText"/> removes from an article's page besides its built-in rules: the CSS selectors of adverts
/// (<c>Intake:AdSelectors</c>, with the source's own <see cref="Source.ArticleRemoveSelector"/>), taken out before the body
/// is looked for, and the advert labels (<c>Intake:AdLabels</c>), lines dropped from the text found ("Advertisement",
/// "Story continues below"). A label matches a whole line, ignoring case and the punctuation around it; one ending in "*"
/// matches a short line starting with it. Both lists are set on Sources.
/// </summary>
public sealed record ArticleCleanup(IReadOnlyList<string> Selectors, IReadOnlyList<string> Labels)
{
    public const int MaxEntries = 50;
    public const int MaxSelectorLength = 200;
    public const int MaxLabelLength = 80;

    /// <summary>The longest line a label ending in "*" removes: past that it's a sentence that happens to start alike.</summary>
    public const int MaxPrefixLine = 120;

    /// <summary>Advert markup the class-name rules can't see: Google's tags, ad-slot data attributes, AMP's ad elements.</summary>
    public const string DefaultSelectors =
        "ins.adsbygoogle; [data-ad]; [data-ad-slot]; [data-ad-unit]; [data-adunit]; [data-ad-placeholder]; [data-google-query-id]; " +
        "[id^=div-gpt-ad]; amp-ad; amp-embed; amp-sticky-ad";

    public const string DefaultLabels =
        "Advertisement; Advertisements; Advert; Ad; Ads; Sponsored; Sponsored content; Paid content; Promoted content; " +
        "Story continues below*; Article continues below*; Content continues below*; Continue reading below*; " +
        "Scroll to continue*; Skip advert*; Skip ad*";

    /// <summary>The defaults, as an install that never saved either list has them.</summary>
    public static ArticleCleanup Default { get; } = new(SponsoredFilter.Parse(DefaultSelectors), SponsoredFilter.Parse(DefaultLabels));

    /// <summary>The lists in force (saved on Sources, else the defaults), with <paramref name="source"/>'s own selector.</summary>
    public static ArticleCleanup For(IntakeOptions o, Source? source = null) => For(o, source?.ArticleRemoveSelector);

    /// <summary>The lists in force, with a source's remove selector as typed (the Sources page's Test).</summary>
    public static ArticleCleanup For(IntakeOptions o, string? sourceSelector)
    {
        var selectors = SponsoredFilter.Parse(o.AdSelectors ?? DefaultSelectors);
        if (!string.IsNullOrWhiteSpace(sourceSelector)) selectors = [.. selectors, sourceSelector.Trim()];
        return new(selectors, SponsoredFilter.Parse(o.AdLabels ?? DefaultLabels));
    }

    /// <summary>The selector list as it's stored ("ins.adsbygoogle; [data-ad]"). Throws <see cref="ArgumentException"/>
    /// saying what's wrong: more than <see cref="MaxEntries"/>, one too long, or one the parser doesn't accept.</summary>
    public static string NormalizeSelectors(string? text)
    {
        var entries = SponsoredFilter.Parse(text);
        if (entries.Count > MaxEntries) throw new ArgumentException($"The advert selectors are at most {MaxEntries}.");
        if (entries.FirstOrDefault(s => s.Length > MaxSelectorLength) is { } longer)
            throw new ArgumentException($"The advert selector '{longer[..20]}…' is longer than {MaxSelectorLength} characters.");
        if (entries.FirstOrDefault(s => !ArticleText.IsSelector(s)) is { } bad)
            throw new ArgumentException($"'{bad}' isn't a CSS selector TIDE can use.");
        return string.Join("; ", entries);
    }

    /// <summary>The label list as it's stored ("Advertisement; Story continues below*"). Throws <see cref="ArgumentException"/>
    /// saying what's wrong: more than <see cref="MaxEntries"/>, one too long, or one with no letter or digit.</summary>
    public static string NormalizeLabels(string? text)
    {
        var entries = SponsoredFilter.Parse(text);
        if (entries.Count > MaxEntries) throw new ArgumentException($"The advert labels are at most {MaxEntries}.");
        if (entries.FirstOrDefault(s => s.Length > MaxLabelLength) is { } longer)
            throw new ArgumentException($"The advert label '{longer[..20]}…' is longer than {MaxLabelLength} characters.");
        if (entries.FirstOrDefault(s => !s.Any(char.IsLetterOrDigit)) is { } bare)
            throw new ArgumentException($"The advert label '{bare}' needs a letter or digit.");
        return string.Join("; ", entries);
    }

    // The punctuation an advert label is set off with ("— Advertisement —", "- Advertisement:", "ADVERTISEMENT ·").
    private static readonly char[] Dressing = [' ', '\t', '-', '–', '—', ':', '|', '•', '·', '*', '_', '.', '…', '>', '<', '[', ']', '(', ')'];

    /// <summary>Whether <paramref name="line"/> is only an advert label.</summary>
    public bool IsLabel(string line)
    {
        var bare = line.Trim(Dressing);
        if (bare.Length == 0) return false;
        foreach (var label in Labels)
        {
            if (label.EndsWith('*'))
            {
                var start = label.TrimEnd('*').Trim(Dressing);
                if (start.Length > 0 && line.Length <= MaxPrefixLine && bare.StartsWith(start, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(bare, label.Trim(Dressing), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
