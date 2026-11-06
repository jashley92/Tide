using Microsoft.AspNetCore.Components;
using Tide.Application.Search;

namespace Tide.Web.Components.Shared;

/// <summary>
/// What a tag card says that's its own: its title and the list it links to ("Threat actors", All profiles at
/// adversaries), its field's class, the picker's id prefix, length and wording, the words for none tagged, for adding one
/// ("Add 'X' as a new adversary") and for having added it, the hint with nothing to offer, the nouns its count uses
/// ("1 adversary", "3 adversaries"), and what it asks before tagging a retired one. <see cref="ComboLabel"/> is the
/// picker's own hidden label, for a card whose head is no label.
/// </summary>
public sealed record TagCardWords(string Title, string ListHref, string ListName, string FieldClass, string PickPrefix, int MaxLength,
    string Placeholder, string ListLabel, string Help, string? ComboLabel, string NoneTagged, string AddAs, string AddedTail, string NothingYet,
    string One, string Many, string RetiredAsk);

/// <summary>The editor's tag card (TagCard.razor), for a card to inherit with its own words, head and rows.</summary>
public abstract partial class TagCard<TTag>
{
    /// <summary>What the picker searches, tags, untags and adds through.</summary>
    protected abstract IArticleTagSource Source { get; }

    protected abstract TagCardWords Words { get; }

    /// <summary>Reads the article's tags (and, when <paramref name="offer"/>, what the picker offers with nothing typed).</summary>
    protected abstract Task<IReadOnlyList<TagPick>> ReadAsync(bool offer);

    /// <summary>Reads what each tag's row shows of how much else is about it, after <see cref="ReadAsync"/>.</summary>
    protected abstract Task ReadDetailsAsync();

    /// <summary>Takes what the prerendered card (of this kind, for this article) read in <see cref="ReadAsync"/> and
    /// <see cref="ReadDetailsAsync"/>, so the first interactive draw shows its tags rather than a loading row.</summary>
    protected abstract void Adopt(TagCard<TTag> prerendered);

    /// <summary>The article's tags; null until read.</summary>
    protected abstract IReadOnlyList<TTag>? Tagged { get; }

    /// <summary>Those the article's text names but isn't tagged with ("Names X").</summary>
    protected abstract IReadOnlyList<TTag> Suggested { get; }

    protected abstract Guid IdOf(TTag tag);

    protected abstract string NameOf(TTag tag);

    /// <summary>What's added to a tag's row class ("tide-stack-tag" and this).</summary>
    protected virtual string RowClass(TTag tag) => "";

    /// <summary>The field's head: its title and the link to the list (not boxed).</summary>
    protected abstract RenderFragment FieldHead { get; }

    /// <summary>A tag's row: its name, an <see cref="UntagButton"/>, and what else the card shows of it.</summary>
    protected abstract RenderFragment<TTag> TaggedRow { get; }

    /// <summary>Drawn after "Names X", before the picker.</summary>
    protected virtual RenderFragment? AfterSuggested => null;

    /// <summary>Drawn after the picker (for anyone, not only analysts).</summary>
    protected virtual RenderFragment? AfterPicker => null;
}
