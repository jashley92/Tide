using System.Diagnostics;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>
/// Alt+Z presses the newest toast's Undo (wwwroot/js/toasts.js), run in Node over a stand-in for the page: only Alt+Z
/// (by the key's place, so a Mac's Option+Z, which types another character, counts), only while an Undo shows, and
/// nothing else is taken from the page.
/// </summary>
public class ToastUndoKeyTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Tide.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repo root (Tide.sln).");
    }

    // The page: the newest Undo button (or none), a document listener, and each key's clicks and whether it was taken.
    private const string Page = """
        const vm = require('vm'), fs = require('fs');
        const listeners = [];
        const undo = { clicks: 0, click() { this.clicks++; } };
        let showing = true;
        global.document = {
            addEventListener: (t, f) => { if (t === 'keydown') listeners.push(f); },
            querySelector: sel => (showing && sel === '.tide-toasts [data-toast-undo="newest"]' ? undo : null),
        };
        global.window = global;
        vm.runInThisContext(fs.readFileSync(process.argv[2], 'utf8'));
        function press(key, code, mods) {
            const e = Object.assign({ key, code, altKey: false, ctrlKey: false, metaKey: false, shiftKey: false, repeat: false,
                defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } }, mods || {});
            const before = undo.clicks;
            listeners.forEach(f => f(e));
            return { clicked: undo.clicks - before, taken: e.defaultPrevented };
        }
        const out = {
            altZ: press('z', 'KeyZ', { altKey: true }),
            optionZ: press('Ω', 'KeyZ', { altKey: true }),
            plainZ: press('z', 'KeyZ'),
            ctrlZ: press('z', 'KeyZ', { ctrlKey: true }),
            altShiftZ: press('Z', 'KeyZ', { altKey: true, shiftKey: true }),
            altX: press('x', 'KeyX', { altKey: true }),
            held: press('z', 'KeyZ', { altKey: true, repeat: true }),
        };
        showing = false;
        out.noToast = press('z', 'KeyZ', { altKey: true });
        console.log(JSON.stringify(out));
        """;

    [Fact]
    public async Task AltZ_PressesTheNewestUndo_OnlyWhileOneShows_AndNoOtherKeyIsTaken()
    {
        var script = Path.Combine(Path.GetTempPath(), "tide-toasts-test-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(script, Page);
        try
        {
            var psi = new ProcessStartInfo("node", [script, Path.Combine(Root(), "src", "Tide.Web", "wwwroot", "js", "toasts.js")])
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
                var said = System.Text.Json.JsonElement.Parse(stdout);
                (int Clicked, bool Taken) Key(string name) => (said.GetProperty(name).GetProperty("clicked").GetInt32(), said.GetProperty(name).GetProperty("taken").GetBoolean());
                Key("altZ").Should().Be((1, true), "Alt+Z presses the newest Undo, and the browser doesn't also act on it");
                Key("optionZ").Should().Be((1, true), "Option+Z on a Mac types another character, and is the same key");
                Key("plainZ").Should().Be((0, false), "a bare letter is the page's (or the box's)");
                Key("ctrlZ").Should().Be((0, false), "Ctrl+Z is the browser's own undo, in a box");
                Key("altShiftZ").Should().Be((0, false));
                Key("altX").Should().Be((0, false));
                Key("held").Should().Be((0, false), "a key held down doesn't undo again");
                Key("noToast").Should().Be((0, false), "with no Undo showing, Alt+Z is left alone");
            }
        }
        finally { File.Delete(script); }
    }
}
