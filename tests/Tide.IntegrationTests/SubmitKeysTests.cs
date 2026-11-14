using System.Text.RegularExpressions;
using AwesomeAssertions;
using Tide.Web.Components.Layout;

namespace Tide.IntegrationTests;

/// <summary>
/// Ctrl+Enter in a text box (EP-90, wwwroot/js/submitkeys.js). A test can't press the keys, so these check what the
/// script reads: each form with one main action marks its container data-submit-scope and exactly one button in it
/// data-submit, the pages load the script by a relative address (so it works under a sub-path), and the shortcuts list
/// has the keys.
/// </summary>
public partial class SubmitKeysTests(TideApp app) : IClassFixture<TideApp>
{
    // The forms marked, by file: how many scopes each has. A mark lost (or one added) shows here.
    public static TheoryData<string, int> Marked => new()
    {
        { "Pages/ReviewPage.razor", 1 },
        { "Pages/Present.razor", 2 },
        { "Pages/OutreachTemplatesPage.razor", 1 },
        { "Pages/SavedQueriesPage.razor", 1 },
        { "Pages/Stories.razor", 1 },
        { "Pages/AdversariesPage.razor", 1 },
        { "Pages/StackPage.razor", 1 },
        { "Pages/BriefPage.razor", 1 },
        { "Today/MeetingCard.razor", 1 },
        { "Pages/ExternalTeams.razor", 2 },
        { "Pages/Admin/AccessAdmin.razor", 2 },
        { "Pages/Admin/SourcesAdmin.razor", 4 },
        { "Pages/Admin/TeamsAdmin.razor", 4 },
        { "Pages/Admin/EmailTemplatesAdmin.razor", 3 },   // the template, its copies, and the archive copy
        { "Pages/Admin/QueryTemplatesAdmin.razor", 1 },
        { "Pages/Admin/RotationAdmin.razor", 1 },
        { "Shared/OutreachPanel.razor", 1 },
        { "Shared/HuntPanel.razor", 2 },
        { "Shared/ActionFilesPanel.razor", 2 },
        { "Shared/DraftAssist.razor", 1 },
        { "Shared/IndicatorAdd.razor", 1 },
        { "Shared/CorrectionForm.razor", 1 },
        { "Shared/DistributePanel.razor", 1 },
    };

    private static string Components()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Tide.sln"))) dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, "src", "Tide.Web", "Components");
    }

    // Each data-submit-scope element's markup, opening tag to its matching close (by counting tags of its name).
    private static List<string> Scopes(string text)
    {
        var scopes = new List<string>();
        foreach (Match open in ScopeTag().Matches(text))
        {
            var name = open.Groups["name"].Value;
            var depth = 1;
            var at = open.Index + open.Length;
            foreach (Match tag in new Regex($@"<(/?){name}\b").Matches(text, at))
            {
                if (tag.Groups[1].Value == "/") depth--; else depth++;
                if (depth != 0) continue;
                scopes.Add(text[open.Index..(tag.Index + tag.Length)]);
                break;
            }
            depth.Should().Be(0, $"the <{name} data-submit-scope> at {open.Index} closes");
        }
        return scopes;
    }

    [Theory]
    [MemberData(nameof(Marked))]
    public void EachMarkedForm_HasOneSubmitButton_InItsScope(string file, int count)
    {
        var text = File.ReadAllText(Path.Combine(Components(), file));
        var scopes = Scopes(text);
        scopes.Should().HaveCount(count, $"{file} marks {count} forms");
        foreach (var scope in scopes)
        {
            SubmitAttr().Count(scope).Should().Be(1, $"a scope in {file} names one button to press:\n{scope}");
            ScopeTag().Count(scope).Should().Be(1, "scopes don't nest");
            var mark = SubmitAttr().Match(scope);
            scope[scope.LastIndexOf('<', mark.Index)..].Should().StartWith("<button", $"the marked control in {file} is a button");
        }
    }

    // Anywhere in the app (the forms above and any marked since): a data-submit button is inside a scope, one to each.
    [Fact]
    public void EveryDataSubmitButton_IsTheOneOfAScope()
    {
        foreach (var file in Directory.EnumerateFiles(Components(), "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var buttons = SubmitAttr().Count(text);
            if (buttons == 0 && !text.Contains("data-submit-scope", StringComparison.Ordinal)) continue;
            var scopes = Scopes(text);
            scopes.Sum(s => SubmitAttr().Count(s)).Should().Be(buttons, $"each data-submit in {Path.GetFileName(file)} is inside a data-submit-scope");
            scopes.Should().OnlyContain(s => SubmitAttr().Count(s) == 1, $"each scope in {Path.GetFileName(file)} has exactly one data-submit");
        }
    }

    [Fact]
    public async Task AMarkedForm_RendersItsMarks()
    {
        var html = await app.CreateClient().GetStringAsync("/admin/rotation");
        var managers = html.IndexOf("Managers to notify", StringComparison.Ordinal);
        managers.Should().BePositive();
        var card = html[html.LastIndexOf("<div", managers, StringComparison.Ordinal)..];
        card.Should().StartWith("<div class=\"tide-card\" data-submit-scope");
        SaveSubmitButton().IsMatch(card).Should().BeTrue();
    }

    [Fact]
    public async Task ThePageLoadsTheScript_ByARelativeAddress()
    {
        var html = await app.CreateClient().GetStringAsync("/articles");
        html.Should().Contain("<script src=\"js/submitkeys.js?v=").And.NotContain("src=\"/js/submitkeys.js");
        var js = await app.CreateClient().GetStringAsync("/js/submitkeys.js");
        js.Should().Contain("[data-submit-scope]").And.Contain("isComposing").And.Contain(".disabled");
    }

    [Fact]
    public void TheShortcutsList_HasCtrlEnter_Everywhere()
    {
        var key = Shortcuts.Everywhere.Keys.Single(k => k.Keys.Contains("Ctrl Enter"));
        key.Keys.Should().Equal("Ctrl Enter", "⌘ Enter");
        key.What.Should().Be("Post or save the text box you're in");
        key.Letter.Should().BeFalse("a modifier shortcut isn't turned off by the letter switch");
    }

    [GeneratedRegex(@"<(?<name>\w+)\b[^>]*\sdata-submit-scope[\s>]")]
    private static partial Regex ScopeTag();

    [GeneratedRegex(@"\sdata-submit(?![-\w])")]
    private static partial Regex SubmitAttr();

    [GeneratedRegex(@"<button[^>]*\sdata-submit(?![-\w])[^>]*>Save</button>")]
    private static partial Regex SaveSubmitButton();
}
