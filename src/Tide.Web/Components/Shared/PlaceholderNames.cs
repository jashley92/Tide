namespace Tide.Web.Components.Shared;

/// <summary>
/// The placeholders a [data-oi] form offers as "{{" is typed (js/placeholder-editor.js reads its data-oi-names): each
/// one's name, what it becomes, and whether a section can be about it.
/// </summary>
public static class PlaceholderNames
{
    public static string Json(IEnumerable<(string Name, string Description, bool Section)> names) =>
        System.Text.Json.JsonSerializer.Serialize(names.Select(p => p.Section
            ? (object)new { n = p.Name, d = p.Description, s = true }
            : new { n = p.Name, d = p.Description }));
}
