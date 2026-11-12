using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>
/// The presenter's rail open over the slide (wwwroot/js/present.js), run in Node over a small stand-in for the page, as
/// <see cref="DialogTabTests"/> runs dialog.js. ] and Escape close it; but with a confirmation open over it (Raise or Add
/// asking about a limit), they're the confirmation's: Escape cancels it alone, and the rail stays behind it. When the
/// rail stops being drawn with focus in it, focus goes to Rail rather than the page's body.
/// </summary>
public class PresentRailKeysTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Tide.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repo root (Tide.sln).");
    }

    private const string Page = """
        const vm = require('vm'), fs = require('fs');
        const open = new Set(['#present-rail[aria-modal="true"]']);
        const listeners = {}, ids = {};
        const media = () => ({ matches: true, addEventListener() {}, removeEventListener() {} });
        global.document = {
            querySelector: sel => (open.has(sel) ? {} : null), getElementById: id => ids[id] || null, body: {}, activeElement: null,
            addEventListener: (t, f) => { (listeners[t] || (listeners[t] = [])).push(f); }, removeEventListener() {},
        };
        global.window = global;
        global.matchMedia = media;
        global.requestAnimationFrame = () => 1;
        global.MutationObserver = function () { this.observe = () => {}; this.disconnect = () => {}; };
        global.tideShortcuts = { typing: () => false, inMenu: () => false, on: () => true, isLetter: () => false };
        vm.runInThisContext(fs.readFileSync(process.argv[2], 'utf8'));
        const said = [];
        tidePresent.start({ invokeMethodAsync: (m, k) => { if (k !== undefined) said.push(k); return Promise.resolve(); } });
        function press(key) {
            said.length = 0;
            const e = { key, target: {}, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
            listeners.keydown.forEach(f => f(e));
            return said.join(',') + (e.defaultPrevented ? '!' : '');
        }
        const out = { escape: press('Escape'), bracket: press(']') };
        open.add('.tide-modal');
        Object.assign(out, { escapeUnderDialog: press('Escape'), bracketUnderDialog: press(']') });
        // The rail went with focus in it: focus, dropped to the body, goes to Rail; focus elsewhere stays where it is.
        const toggle = ids['present-rail-toggle'] = { focus() { document.activeElement = toggle; } };
        document.activeElement = document.body;
        tidePresent.refocus();
        out.refocused = document.activeElement === toggle;
        const elsewhere = document.activeElement = {};
        tidePresent.refocus();
        out.leftAlone = document.activeElement === elsewhere;
        console.log(JSON.stringify(out));
        """;

    [Fact]
    public async Task BracketAndEscape_CloseTheRail_ButNotUnderAnOpenConfirmation_AndFocusOutlivesTheRail()
    {
        var script = Path.Combine(Path.GetTempPath(), "tide-present-keys-" + Guid.NewGuid().ToString("N") + ".js");
        await File.WriteAllTextAsync(script, Page);
        try
        {
            var psi = new ProcessStartInfo("node", [script, Path.Combine(Root(), "src", "Tide.Web", "wwwroot", "js", "present.js")])
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
                var said = JsonDocument.Parse(stdout).RootElement;
                string At(string name) => said.GetProperty(name).GetString()!;
                At("escape").Should().Be("rail!", "Escape closes the rail open over the slide");
                At("bracket").Should().Be("rail!", "and so does ]");
                At("escapeUnderDialog").Should().Be("", "a confirmation over the rail has Escape: it cancels it alone");
                At("bracketUnderDialog").Should().Be("", "and ] doesn't shut the rail behind it");
                said.GetProperty("refocused").GetBoolean().Should().BeTrue("focus dropped to the body as the rail went goes to Rail");
                said.GetProperty("leftAlone").GetBoolean().Should().BeTrue("focus anywhere else is left where it is");
            }
        }
        finally { File.Delete(script); }
    }
}
