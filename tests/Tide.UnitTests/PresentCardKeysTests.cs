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
        const open = new Set();   // the presenter's rail over the slide, a step of Learn to present
        let paged = 0;
        const body = { scrollHeight: 100, clientHeight: 100, scrollTop: 0, scrollBy(o) { paged += Math.sign(o.top); }, scrollTo() {} };
        const panel = { position: null };
        body.parentElement = panel;
        global.document = {
            querySelector: sel => (open.has(sel) ? { contains: () => true, isConnected: true, focus() {} } : null), getElementById: () => null, body: {}, activeElement: null,
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
        function press(key, shiftKey) {
            said.length = 0;
            const e = { key, shiftKey: !!shiftKey, target: {}, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
            listeners.keydown.forEach(f => f(e));
            return said.join(',');
        }
        const keys = ['ArrowRight', 'ArrowLeft', 'Home', 'End', 'Enter', 'Backspace', ' ', 'Escape'];
        const out = {};
        for (const where of ['fixed', 'static', null]) {
            card = where;
            out[where || 'none'] = keys.map(k => press(k));
        }
        // Over the slide, a card with more below and above: Space and Shift+Space page it, the slide stays.
        card = 'fixed';
        Object.assign(body, { scrollHeight: 300, scrollTop: 100 });
        out.spaceMidCard = press(' ') + ':' + paged;
        out.shiftSpaceMidCard = press(' ', true) + ':' + paged;
        body.scrollTop = 0;
        out.shiftSpaceAtTop = press(' ', true) + ':' + paged;
        body.scrollTop = 200;
        out.spaceAtEnd = press(' ') + ':' + paged;
        Object.assign(body, { scrollHeight: 100, scrollTop: 0 });
        // The presenter's rail open over the slide, and a step of Learn to present: the card holds the slide's keys there too.
        const moving = ['ArrowRight', 'ArrowLeft', 'Home', 'End'];
        open.add('#present-rail[aria-modal="true"]');
        out.railOver = moving.map(k => press(k));
        card = null;
        out.railNone = moving.map(k => press(k));
        open.delete('#present-rail[aria-modal="true"]');
        open.add('.tide-modal[data-guide-card]');
        card = 'fixed';
        out.stepOver = moving.map(k => press(k));
        card = null;
        out.stepNone = moving.map(k => press(k));
        console.log(JSON.stringify(out));
        """;

    [Fact]
    public async Task ACardOverTheSlide_HoldsTheKeysThatMoveIt_WithTheRailOrAStepOpenToo_PagingItInstead_ButEscapeClosesIt_AndOneBesideTheSlideDoesnt()
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
                string One(string name) => said.GetProperty(name).GetString()!;
                One("spaceMidCard").Should().Be(":1", "Space pages a card over the slide with more below, and the slide stays");
                One("shiftSpaceMidCard").Should().Be(":0", "Shift+Space pages it back up");
                One("shiftSpaceAtTop").Should().Be(":0", "at its top, Shift+Space doesn't go back a slide");
                One("spaceAtEnd").Should().Be(":0", "at its end, Space doesn't move on");
                string[] slides = ["next", "prev", "first", "last"];
                At("railOver").Should().Equal(["", "", "", ""], "the presenter's rail over the slide doesn't let the arrows, Home or End close the card");
                At("railNone").Should().Equal(slides, "with no card, they move the slide under the rail");
                At("stepOver").Should().Equal(["", "", "", ""], "nor does a step of Learn to present");
                At("stepNone").Should().Equal(slides, "with no card, a step lets them move the slides");
            }
        }
        finally { File.Delete(script); }
    }
}
