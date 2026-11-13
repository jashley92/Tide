using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>
/// The formatting toolbar's text transforms (wwwroot/js/markdown-toolbar.js), run in Node, on long and odd inputs: a
/// selection of tens of thousands of spaces, a list line with a stray carriage return, a long run of spaces inside an
/// item. Each gives the same result it always has, and quickly: none of the script's patterns can backtrack over a long
/// run of spaces (each case here took about a second, or several, when they could).
/// </summary>
public class MarkdownToolbarTransformTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Tide.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repo root (Tide.sln).");
    }

    // Each case: the transform's result (JSON, or null) and how long it took.
    private const string Cases = """
        const t = require(process.argv[2]);
        const sp = ' '.repeat(40000);
        const cases = {
            boldAroundSpaces: () => t.bold({ text: 'a' + sp + 'b', start: 0, end: sp.length + 2 }),
            boldOnlySpaces: () => t.bold({ text: 'x' + sp + 'y', start: 1, end: sp.length + 1 }),
            boldSpacesEachSide: () => t.bold({ text: '  word  ', start: 0, end: 8 }),
            linkAroundSpaces: () => t.link({ text: 'a' + sp + 'b', start: 0, end: sp.length + 2 }),
            linkAddressWithSpaceAfter: () => t.link({ text: 'https://example.com \t', start: 0, end: 21 }),
            italicOverLines: () => t.italic({ text: 'a' + sp + 'b   \n- c 　', start: 0, end: sp.length + 11 }),
            bulletsOnACarriageReturn: () => t.bullets({ text: '-' + sp + '\rx', start: 0, end: 1 }),
            onListCarriageReturn: () => t.onList({ text: '-  a\r', start: 2, end: 2 }),
            numbersOnASpacedItem: () => t.numbers({ text: '1.' + sp + 'x', start: 0, end: 1 }),
            enterInsideSpaces: () => t.enter({ text: '- a' + sp + 'b', start: 20003, end: 20003 }),
            enterAfterAMarkerAndSpaces: () => t.enter({ text: '3)   ', start: 5, end: 5 }),
        };
        const out = {};
        for (const [name, run] of Object.entries(cases)) {
            const began = process.hrtime.bigint();
            const r = run();
            out[name] = { ms: Number(process.hrtime.bigint() - began) / 1e6, result: r === undefined ? null : r };
        }
        console.log(JSON.stringify(out));
        """;

    private static async Task<JsonElement> Run()
    {
        var script = Path.Combine(Path.GetTempPath(), "tide-md-transforms-" + Guid.NewGuid().ToString("N") + ".js");
        await File.WriteAllTextAsync(script, Cases);
        try
        {
            var psi = new ProcessStartInfo("node", [script, Path.Combine(Root(), "src", "Tide.Web", "wwwroot", "js", "markdown-toolbar.js")])
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

    private static readonly string Spaces = new(' ', 40000);

    [Fact]
    public async Task LongAndOddInputs_GiveTheSameResults_Quickly()
    {
        var said = await Run();
        foreach (var c in said.EnumerateObject())
            c.Value.GetProperty("ms").GetDouble().Should().BeLessThan(200, "{0} is linear in the input", c.Name);

        string Text(string name) => said.GetProperty(name).GetProperty("result").GetProperty("text").GetString()!;
        (int, int) Sel(string name)
        {
            var r = said.GetProperty(name).GetProperty("result");
            return (r.GetProperty("start").GetInt32(), r.GetProperty("end").GetInt32());
        }

        // A selection with spaces inside is wrapped whole; spaces either side stay outside the marks.
        Text("boldAroundSpaces").Should().Be("**a" + Spaces + "b**");
        Sel("boldAroundSpaces").Should().Be((2, 40004));
        said.GetProperty("boldOnlySpaces").GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null, "a selection of only spaces is left alone");
        Text("boldSpacesEachSide").Should().Be("  **word**  ");
        Sel("boldSpacesEachSide").Should().Be((4, 8));

        Text("linkAroundSpaces").Should().Be("[a" + Spaces + "b](https://)");
        Sel("linkAroundSpaces").Should().Be((40005, 40013), "the address to type is selected");
        Text("linkAddressWithSpaceAfter").Should().Be("[text](https://example.com) \t");
        Sel("linkAddressWithSpaceAfter").Should().Be((1, 5));

        // Over lines, each line's text is wrapped after its marker, and trailing spaces of any kind are dropped.
        Text("italicOverLines").Should().Be("*a" + Spaces + "b*\n- *c*");
        Sel("italicOverLines").Should().Be((0, 40010));

        // A carriage return in the item's text: not a list line, so Bullets makes it one.
        Text("bulletsOnACarriageReturn").Should().Be("- -" + Spaces + "\rx");
        Sel("bulletsOnACarriageReturn").Should().Be((0, 3));
        said.GetProperty("onListCarriageReturn").GetProperty("result").GetBoolean().Should().BeFalse("a line with a carriage return isn't a list line");

        // A numbered item with a long gap after its number is still one: Numbers takes the number and the gap off.
        Text("numbersOnASpacedItem").Should().Be("x");

        // Enter in a run of spaces splits the item there, dropping the spaces either side of the break.
        Text("enterInsideSpaces").Should().Be("- a\n- b");
        Sel("enterInsideSpaces").Should().Be((6, 6));
        // Enter on an empty top-level item ends the list.
        Text("enterAfterAMarkerAndSpaces").Should().Be("");
    }
}
