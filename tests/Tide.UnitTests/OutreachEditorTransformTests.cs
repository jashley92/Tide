using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>
/// Writing an outreach template (wwwroot/js/outreach-editor.js), run in Node: what typing "{{" offers and writes, what
/// the Insert menu puts in, and the section it wraps round a selection, in the body and in the one-line subject.
/// </summary>
public class OutreachEditorTransformTests
{
    private const string Cases = """
        const t = require(process.argv[2]);
        const names = [{ n: 'article.title' }, { n: 'article.cves' }, { n: 'article.cve-links' }, { n: 'article.cves-detailed' },
            { n: 'indicators' }, { n: 'indicators:domain' }, { n: 'product.name' }];
        const long = 'x'.repeat(100000);
        const out = {
            suggestAfterBraces: t.suggest({ text: 'Hello {{art', start: 11, end: 11 }),
            suggestSection: t.suggest({ text: 'a\n{{#ind', start: 8, end: 8 }),
            suggestClosed: t.suggest({ text: 'See {{article.title}} ', start: 22, end: 22 }),
            suggestOtherLine: t.suggest({ text: '{{art\nx', start: 7, end: 7 }),
            suggestWithSelection: t.suggest({ text: '{{art', start: 2, end: 5 }),
            suggestLongLine: t.suggest({ text: long + '{{cve', start: long.length + 5, end: long.length + 5 }),
            matchesStart: t.matches(names, 'article.c').map(p => p.n),
            matchesPart: t.matches(names, 'cve').map(p => p.n),
            matchesNothing: t.matches(names, '').length,
            complete: t.complete({ text: 'See {{cve and', start: 9, end: 9 }, { from: 4, sign: '', query: 'cve' }, 'article.cves'),
            completeKeepsBraces: t.complete({ text: '{{art}}', start: 5, end: 5 }, { from: 0, sign: '', query: 'art' }, 'article.title'),
            completeSection: t.complete({ text: '{{#ind', start: 6, end: 6 }, { from: 0, sign: '#', query: 'ind' }, 'indicators'),
            insert: t.insert({ text: 'Hello , thanks', start: 6, end: 6 }, '{{product.contact|there}}'),
            sectionAroundLines: t.section({ text: 'Hi\nCVEs:\n{{article.cves}}\nBye', start: 3, end: 26 }, 'article.cves', false),
            sectionNothingSelected: t.section({ text: 'Hi\n', start: 3, end: 3 }, 'article.cves', false),
            sectionMidLine: t.section({ text: 'Hi there', start: 2, end: 2 }, 'indicators', false),
            sectionInline: t.section({ text: 'Re: ', start: 4, end: 4 }, 'article.cves', true),
        };
        console.log(JSON.stringify(out));
        """;

    private static async Task<JsonElement> Run()
    {
        var script = Path.Combine(Path.GetTempPath(), "tide-oi-transforms-" + Guid.NewGuid().ToString("N") + ".js");
        await File.WriteAllTextAsync(script, Cases);
        try
        {
            var psi = new ProcessStartInfo("node", [script, Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "wwwroot", "js", "outreach-editor.js")])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            Process p;
            try { p = Process.Start(psi)!; }
            catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("This test needs Node.js (node) on the PATH.", ex); }
            using (p)
            {
                var stderr = p.StandardError.ReadToEndAsync();
                var stdout = await p.StandardOutput.ReadToEndAsync();
                p.WaitForExit(60_000).Should().BeTrue("node finishes");
                p.ExitCode.Should().Be(0, "the script ran: {0}", await stderr);
                return JsonElement.Parse(stdout);
            }
        }
        finally { File.Delete(script); }
    }

    // An edit's text with it applied, and where the cursor ends up.
    private static (string Text, int Caret) Applied(string text, JsonElement edit)
    {
        var from = edit.GetProperty("from").GetInt32();
        var to = edit.GetProperty("to").GetInt32();
        return (text[..from] + edit.GetProperty("insert").GetString() + text[to..], edit.GetProperty("caret").GetInt32());
    }

    [Fact]
    public async Task TypingBraces_OffersTheNamesThatMatch_AndWritesTheOneChosen_BracesClosed()
    {
        var said = await Run();

        var found = said.GetProperty("suggestAfterBraces");
        (found.GetProperty("from").GetInt32(), found.GetProperty("sign").GetString(), found.GetProperty("query").GetString()).Should().Be((6, "", "art"));
        said.GetProperty("suggestSection").GetProperty("sign").GetString().Should().Be("#");
        said.GetProperty("suggestSection").GetProperty("query").GetString().Should().Be("ind");
        foreach (var none in new[] { "suggestClosed", "suggestOtherLine", "suggestWithSelection" })
            said.GetProperty(none).ValueKind.Should().Be(JsonValueKind.Null, none);
        said.GetProperty("suggestLongLine").GetProperty("query").GetString().Should().Be("cve", "only the end of a long line is read");

        said.GetProperty("matchesStart").EnumerateArray().Select(e => e.GetString()).Should().Equal("article.cves", "article.cve-links", "article.cves-detailed");
        said.GetProperty("matchesPart").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(["article.cves", "article.cve-links", "article.cves-detailed"], "a part of the name starts with it");
        said.GetProperty("matchesNothing").GetInt32().Should().Be(7, "everything, for {{ alone");

        Applied("See {{cve and", said.GetProperty("complete")).Should().Be(("See {{article.cves}} and", 20));
        Applied("{{art}}", said.GetProperty("completeKeepsBraces")).Should().Be(("{{article.title}}", 17), "the }} already typed isn't doubled");
        Applied("{{#ind", said.GetProperty("completeSection")).Should().Be(("{{#indicators}}", 15));
    }

    [Fact]
    public async Task TheInsertMenu_WritesAtTheCursor_AndWrapsASectionOnLinesOfItsOwn_OrInlineInTheSubject()
    {
        var said = await Run();

        Applied("Hello , thanks", said.GetProperty("insert")).Should().Be(("Hello {{product.contact|there}}, thanks", 31));

        Applied("Hi\nCVEs:\n{{article.cves}}\nBye", said.GetProperty("sectionAroundLines")).Text
            .Should().Be("Hi\n{{#article.cves}}\nCVEs:\n{{article.cves}}\n{{else}}\n{{/article.cves}}\nBye");
        var (empty, caret) = Applied("Hi\n", said.GetProperty("sectionNothingSelected"));
        empty.Should().Be("Hi\n{{#article.cves}}\n{{article.cves}}\n{{else}}\n{{/article.cves}}", "round the placeholder itself");
        caret.Should().Be("Hi\n{{#article.cves}}\n{{article.cves}}".Length, "after the placeholder, not on the {{else}} line");
        Applied("Hi there", said.GetProperty("sectionMidLine")).Text
            .Should().Be("Hi\n{{#indicators}}\n{{indicators}}\n{{else}}\n{{/indicators}}\n there", "its tags on lines of their own");
        Applied("Re: ", said.GetProperty("sectionInline")).Text.Should().Be("Re: {{#article.cves}}{{article.cves}}{{/article.cves}}");
    }

    // The editor writes the syntax itself: what it writes has to pass the check a template meets when it's saved.
    [Fact]
    public async Task WhatTheEditorWrites_PassesTheCheckOnSave()
    {
        var said = await Run();
        var written = new[]
        {
            Applied("See {{cve and", said.GetProperty("complete")).Text,
            Applied("{{#ind", said.GetProperty("completeSection")).Text + "{{/indicators}}",
            Applied("Hello , thanks", said.GetProperty("insert")).Text,
            Applied("Hi\nCVEs:\n{{article.cves}}\nBye", said.GetProperty("sectionAroundLines")).Text,
            Applied("Hi\n", said.GetProperty("sectionNothingSelected")).Text,
            Applied("Hi there", said.GetProperty("sectionMidLine")).Text,
            Applied("Re: ", said.GetProperty("sectionInline")).Text,
        };
        foreach (var text in written)
            FluentActions.Invoking(() => Tide.Application.Actions.OutreachFill.Check(text)).Should().NotThrow(text);
    }
}
