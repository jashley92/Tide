namespace Tide.Web.Components.Shared;

/// <summary>
/// The cascading value a page sets to draw everything under it read-only: present mode in rehearsal. Each component that
/// saves takes it (<c>[CascadingParameter(Name = ReadOnlyCascade.Name)]</c>), offers nothing that saves, and refuses where
/// it saves, so no write path runs under it whichever component starts it.
/// </summary>
public static class ReadOnlyCascade
{
    /// <summary>The cascading value's name.</summary>
    public const string Name = "TideReadOnly";
}
