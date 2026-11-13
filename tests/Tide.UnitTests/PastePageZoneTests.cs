using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>
/// A zone that takes the whole page (an action's Files, in its rail) in wwwroot/js/paste-drop.js, run in Node over a small
/// stand-in for the page, as <see cref="DialogTabTests"/> runs dialog.js. It takes a pasted screenshot only with nothing
/// focused: a paste into the top bar's search box, a field elsewhere or a dialog is that control's, not a new file.
/// </summary>
public class PastePageZoneTests
{
    private const string Page = """
        const vm = require('vm'), fs = require('fs');
        function el(id, tag, parent, attrs) {
            const e = { id, tagName: tag, type: '', attrs: attrs || {}, parent, isContentEditable: false };
            e.getClientRects = () => [];   // the zone sits in a closed rail: hidden, as a page zone may be
            e.contains = o => { for (let x = o; x; x = x.parent) if (x === e) return true; return false; };
            e.closest = sel => { for (let x = e; x; x = x.parent) if (x.attrs.role && sel.includes('"' + x.attrs.role + '"')) return x; return null; };
            e.dispatchEvent = () => {};
            return e;
        }
        const body = el('body', 'BODY', null);
        const zone = el('files', 'DIV', body), input = el('paste-in', 'INPUT', zone);
        const search = el('search', 'BUTTON', body);
        const dialog = el('dialog', 'DIV', body, { role: 'dialog' }), field = el('field', 'BUTTON', dialog);
        const byId = { files: zone, 'paste-in': input };
        const listeners = {};
        let pushed = 0;
        Object.defineProperty(input, 'files', { set() { pushed++; } });
        global.document = { body, documentElement: el('html', 'HTML', null), activeElement: body,
            getElementById: id => byId[id] || null,
            addEventListener: (t, f) => { (listeners[t] || (listeners[t] = [])).push(f); } };
        global.window = global;
        global.DataTransfer = function () { const files = []; this.items = { add: f => files.push(f) }; this.files = files; };
        global.File = function (parts, name, o) { this.name = name; this.type = o.type; };
        global.Event = function (t) { this.type = t; };
        vm.runInThisContext(fs.readFileSync(process.argv[2], 'utf8'));
        tidePasteDrop.register('files', 'paste-in', null, true, false, true);

        function paste(focus, target) {
            document.activeElement = focus;
            const before = pushed;
            const shot = { kind: 'file', type: 'image/png', getAsFile: () => ({ type: 'image/png' }) };
            listeners.paste.forEach(f => f({ target, clipboardData: { types: ['Files'], items: [shot] }, preventDefault() {} }));
            return pushed > before;
        }
        console.log(JSON.stringify({
            nothingFocused: paste(body, body),
            searchFocused: paste(search, search),
            inADialog: paste(field, field),
        }));
        """;

    [Fact]
    public async Task APageZone_TakesAPastedScreenshot_OnlyWithNothingFocused()
    {
        var script = Path.Combine(Path.GetTempPath(), "tide-paste-test-" + Guid.NewGuid().ToString("N") + ".js");
        await File.WriteAllTextAsync(script, Page);
        try
        {
            var psi = new ProcessStartInfo("node", [script, Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "wwwroot", "js", "paste-drop.js")])
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
                var said = JsonElement.Parse(stdout);
                said.GetProperty("nothingFocused").GetBoolean().Should().BeTrue("with nothing focused, the page zone takes the screenshot");
                said.GetProperty("searchFocused").GetBoolean().Should().BeFalse("a paste with the search box focused isn't a new file");
                said.GetProperty("inADialog").GetBoolean().Should().BeFalse("nor one inside a dialog");
            }
        }
        finally { File.Delete(script); }
    }
}
