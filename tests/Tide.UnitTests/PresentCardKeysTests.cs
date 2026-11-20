using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>
/// An action or card open over the slide in present mode (wwwroot/js/present.js), run in Node over a small stand-in for
/// the page, as <see cref="PresentRailKeysTests"/> is. The keys that move the slide don't while it's open over the slide,
/// so it isn't closed under whoever is reading it; Escape still closes it. One beside the slide leaves them to the slide.
/// </summary>
public class PresentCardKeysTests
{
    private const string Page = """
        const vm = require('vm'), fs = require('fs');
        const listeners = {};
        let card = null;   // the open card's position: 'fixed' (over the slide), 'static' (beside it), or none
        const body = { scrollHeight: 100, clientHeight: 100, scrollTop: 0, scrollBy() {}, scrollTo() {} };
        const panel = { position: null };
        body.parentElement = panel;
        global.document = {
            querySelector: () => null, getElementById: () => null, body: {}, activeElement: null,
            querySelectorAll: sel => (card && sel.indexOf('tide-present-action-body') >= 0 ? [body] : []),
            addEventListener: (t, f) => { (listeners[t] || (listeners[t] = [])).push(f); }, removeEventListener() {},
        };
        global.window = global;
        global.getComputedStyle = () => ({ position: card });
        global.matchMedia = () => ({ matches: true, addEventListener() {}, removeEventListener() {} });
        global.requestAnimationFrame = () => 1;
        global.performance = global.performance || { now: () => 0 };
        global.MutationObserver = function () { this.observe = () => {}; this.disconnect = () => {}; };
        global.tideShortcuts = { typing: () => false, inMenu: () => false, on: () => true, isLetter: () => false, still: () => true };
        vm.runInThisContext(fs.readFileSync(process.argv[2], 'utf8'));
        const said = [];
        tidePresent.start({ invokeMethodAsync: (m, k) => { if (m === 'OnPresentKey') said.push(k); return Promise.resolve(); } });
        function press(key) {
            said.length = 0;
            const e = { key, shiftKey: false, target: {}, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
            listeners.keydown.forEach(f => f(e));
            return said.join(',');
        }
        const keys = ['ArrowRight', 'ArrowLeft', 'Home', 'End', 'Enter', 'Backspace', ' ', 'Escape'];
        const out = {};
        for (const where of ['fixed', 'static', null]) {
            card = where;
            out[where || 'none'] = keys.map(press);
        }
        console.log(JSON.stringify(out));
        """;

    [Fact]
    public async Task ACardOverTheSlide_HoldsTheKeysThatMoveIt_ButEscapeClosesIt_AndOneBesideTheSlideDoesnt()
    {
        var script = Path.Combine(Path.GetTempPath(), "tide-present-card-keys-" + Guid.NewGuid().ToString("N") + ".js");
        await File.WriteAllTextAsync(script, Page);
        try
        {
            var psi = new ProcessStartInfo("node", [script, Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "wwwroot", "js", "present.js")])
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
                string[] At(string name) => said.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();
                string[] moving = ["next", "prev", "first", "last", "next", "prev", "next", "close"];
                At("fixed").Should().Equal(["", "", "", "", "", "", "", "close"],
                    "over the slide, the arrows, Home, End, Enter, Backspace and Space at the card's end move nothing; Escape closes it");
                At("static").Should().Equal(moving, "beside the slide, they move the slide as ever");
                At("none").Should().Equal(moving, "with nothing open, they move the slide");
            }
        }
        finally { File.Delete(script); }
    }
}
