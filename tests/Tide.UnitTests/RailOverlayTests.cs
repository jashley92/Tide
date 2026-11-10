using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>
/// The context rail's script (tideRail in wwwroot/js/theme.js), run in Node over a small stand-in for the page, as
/// <see cref="DialogTabTests"/> runs dialog.js. On a phone the open rail covers the whole window, so the page under it
/// is made inert (out of the tab order, hidden from assistive technology) until it closes; a side overlay and a docked
/// rail leave the page as it is. Focus goes into the rail as an overlay opens and back to its button as it closes.
/// </summary>
public class RailOverlayTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Tide.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repo root (Tide.sln).");
    }

    // The page: <html>, the four parts under the rail, the rail with its button, and a dialog that can open in the top
    // bar. `where` stands for the stylesheet: what it says of the rail at this window width.
    private const string Page = """
        const vm = require('vm'), fs = require('fs');
        function el(id) {
            const attrs = {};
            return { id, attrs, isConnected: true,
                getAttribute: n => (n in attrs ? attrs[n] : null), hasAttribute: n => n in attrs,
                setAttribute: (n, v) => { attrs[n] = String(v); }, removeAttribute: n => { delete attrs[n]; },
                toggleAttribute(n, on) { if (on) attrs[n] = ''; else delete attrs[n]; },
                focus() { document.activeElement = this; }, contains(other) { return other === this; } };
        }
        const root = el('html'), rail = el('tide-rail'), button = el('tide-rail-toggle');
        const under = { '.tide-skip-link': el('skip'), '.sidebar': el('sidebar'), '.top-row': el('top'), '#main-content': el('content') };
        const where = { docks: false, covers: false };
        let railThere = true, dialog = null;
        const listeners = { keydown: [], click: [], resize: [] }, observers = [];
        global.document = {
            documentElement: root, body: el('body'), activeElement: null,
            getElementById: id => id === 'tide-rail' ? (railThere ? rail : null) : id === 'tide-rail-toggle' ? button : null,
            querySelectorAll: sel => sel.includes('dialog') ? (dialog ? [dialog] : []) : sel.split(',').map(s => under[s.trim()]).filter(Boolean),
            addEventListener: (t, f) => { (listeners[t] || (listeners[t] = [])).push(f); },
        };
        global.window = global;
        global.addEventListener = (t, f) => { (listeners[t] || (listeners[t] = [])).push(f); };
        global.localStorage = { getItem: () => null, setItem() {}, removeItem() {} };
        global.getComputedStyle = e => ({ getPropertyValue: name => e !== rail ? '' : name === '--tide-rail-docks' ? (where.docks ? '1' : '')
            : name === '--tide-rail-covers' ? (where.covers ? '1' : '') : '' });
        global.MutationObserver = function (f) { this.observe = () => { observers.push(f); }; this.disconnect = () => { observers.splice(observers.indexOf(f), 1); }; };
        vm.runInThisContext(fs.readFileSync(process.argv[2], 'utf8'));

        const changed = () => observers.slice().forEach(f => f());   // the page's markup changed
        const key = k => listeners.keydown.forEach(f => f({ key: k, target: document.activeElement, getModifierState: () => false, preventDefault() {} }));
        const state = () => ({
            inert: Object.values(under).filter(e => e.hasAttribute('inert')).map(e => e.id).join(','),
            railInert: rail.hasAttribute('inert'),
            over: root.hasAttribute('data-rail-over'),
            expanded: button.getAttribute('aria-expanded'),
            focus: document.activeElement ? document.activeElement.id : null,
            watching: observers.length,
        });
        function reset(at) {
            Object.assign(where, { docks: false, covers: false }, at);
            railThere = true; dialog = null; document.activeElement = button;
            root.removeAttribute('data-rail'); tideRail.start();
        }
        const out = {};

        reset({ covers: true });
        tideRail.toggle(); out.phoneOpen = state();
        tideRail.toggle(); out.phoneClosed = state();
        tideRail.toggle(); key('Escape'); out.phoneEscape = state();

        reset({});
        tideRail.toggle(); out.sideOpen = state();

        reset({ docks: true });
        out.docked = state();
        tideRail.toggle(); out.dockedClosed = state();

        // Open on a phone, then the page changes to one with no rail.
        reset({ covers: true });
        tideRail.toggle(); railThere = false; changed(); out.railGone = state();

        // Open on a phone, then the window widens to where the rail is a side overlay.
        reset({ covers: true });
        tideRail.toggle(); where.covers = false; listeners.resize.forEach(f => f()); out.widened = state();

        // Open on a phone, then a dialog opens in the page under it (the palette, from Ctrl+K).
        reset({ covers: true });
        tideRail.toggle(); dialog = el('palette'); changed(); out.dialogUnder = state();

        // A dialog inside the rail itself is the rail's own.
        reset({ covers: true });
        tideRail.toggle(); dialog = rail; changed(); out.dialogInRail = state();

        console.log(JSON.stringify(out));
        """;

    private sealed record State(string Inert, bool RailInert, bool Over, string? Expanded, string? Focus, int Watching);

    private static async Task<Dictionary<string, State>> Run()
    {
        var script = Path.Combine(Path.GetTempPath(), "tide-rail-test-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(script, Page);
        try
        {
            var psi = new ProcessStartInfo("node", [script, Path.Combine(Root(), "src", "Tide.Web", "wwwroot", "js", "theme.js")])
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
                return JsonSerializer.Deserialize<Dictionary<string, State>>(stdout, JsonSerializerOptions.Web)!;
            }
        }
        finally { File.Delete(script); }
    }

    private const string Everything = "skip,sidebar,top,content";

    [Fact]
    public async Task OnAPhone_TheOpenRail_MakesThePageUnderItInert_AndClosingGivesItBack()
    {
        var said = await Run();

        said["phoneOpen"].Should().Be(new State(Everything, RailInert: false, Over: true, "true", "tide-rail", 1),
            "the rail covers the window: Tab stays in it, and focus moves into it");
        said["phoneClosed"].Should().Be(new State("", false, false, "false", "tide-rail-toggle", 0), "closed, the page is back and focus is on the button");
        said["phoneEscape"].Should().Be(new State("", false, false, "false", "tide-rail-toggle", 0), "Escape closes it the same way");
    }

    [Fact]
    public async Task ASideOverlay_AndADockedRail_LeaveThePageAsItIs()
    {
        var said = await Run();

        said["sideOpen"].Should().Be(new State("", false, true, "true", "tide-rail", 0), "the page is still in view beside a side overlay");
        said["docked"].Should().Be(new State("", false, false, "true", "tide-rail-toggle", 0));
        said["dockedClosed"].Should().Be(new State("", false, false, "false", "tide-rail-toggle", 0));
    }

    [Fact]
    public async Task NothingStaysInert_WhenTheRailGoes_TheWindowWidens_OrADialogOpensUnderIt()
    {
        var said = await Run();

        said["railGone"].Should().Be(new State("", false, false, "false", "tide-rail", 0), "a page with no rail has nothing over it");
        said["widened"].Inert.Should().BeEmpty("a side overlay leaves the page in view");
        said["widened"].Over.Should().BeTrue();
        said["dialogUnder"].Inert.Should().BeEmpty("a dialog opened in the page must be usable");
        said["dialogUnder"].Over.Should().BeFalse("so the rail closes for it");
        said["dialogUnder"].Focus.Should().Be("tide-rail", "and focus is left for the dialog to take");
        said["dialogInRail"].Should().Be(new State(Everything, false, true, "true", "tide-rail", 1), "a dialog in the rail is the rail's own");
    }
}
