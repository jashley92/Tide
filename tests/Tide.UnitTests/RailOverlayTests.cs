using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>
/// The context rail's script (tideRail in wwwroot/js/theme.js), run in Node over a small stand-in for the page, as
/// <see cref="DialogTabTests"/> runs dialog.js. The open overlay is modal at every width it's an overlay (a phone, or a
/// window narrower than where the rail docks): the page under it is made inert (out of the tab order, hidden from
/// assistive technology) until it closes. A docked rail leaves the page as it is. Focus goes into the rail as the overlay
/// opens and back to its button as it closes.
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
            return { id, attrs, isConnected: true, getClientRects: () => [{}],
                getAttribute: n => (n in attrs ? attrs[n] : null), hasAttribute: n => n in attrs,
                setAttribute: (n, v) => { attrs[n] = String(v); }, removeAttribute: n => { delete attrs[n]; },
                toggleAttribute(n, on) { if (on) attrs[n] = ''; else delete attrs[n]; },
                focus() { document.activeElement = this; }, contains(other) { return other === this; } };
        }
        const root = el('html'), rail = el('tide-rail'), button = el('tide-rail-toggle');
        const under = { '.tide-skip-link': el('skip'), '.sidebar': el('sidebar'), '.top-row': el('top'), '#main-content': el('content') };
        const where = { docks: false };
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
        global.getComputedStyle = e => ({ getPropertyValue: name => e !== rail ? '' : name === '--tide-rail-docks' ? (where.docks ? '1' : '') : '' });
        global.MutationObserver = function (f) { this.observe = () => { observers.push(f); }; this.disconnect = () => { observers.splice(observers.indexOf(f), 1); }; };
        vm.runInThisContext(fs.readFileSync(process.argv[2], 'utf8'));

        const changed = () => observers.slice().forEach(f => f());   // the page's markup changed
        const key = k => listeners.keydown.forEach(f => f({ key: k, target: document.activeElement, getModifierState: () => false, preventDefault() {}, stopImmediatePropagation() {} }));
        const state = () => ({
            inert: Object.values(under).filter(e => e.hasAttribute('inert')).map(e => e.id).join(','),
            railInert: rail.hasAttribute('inert'),
            over: root.hasAttribute('data-rail-over'),
            expanded: button.getAttribute('aria-expanded'),
            focus: document.activeElement ? document.activeElement.id : null,
            watching: observers.length,
            role: rail.getAttribute('role'),
            modal: rail.getAttribute('aria-modal'),
        });
        function reset(at) {
            Object.assign(where, { docks: false }, at);
            railThere = true; dialog = null; document.activeElement = button;
            root.removeAttribute('data-rail'); tideRail.start();
        }
        const out = {};

        reset({});
        tideRail.toggle(); out.overOpen = state();
        tideRail.toggle(); out.overClosed = state();
        tideRail.toggle(); key('Escape'); out.overEscape = state();

        reset({ docks: true });
        out.docked = state();
        tideRail.toggle(); out.dockedClosed = state();

        // Open, then the page changes to one with no rail.
        reset({});
        tideRail.toggle(); railThere = false; changed(); out.railGone = state();

        // Open, then the window widens to where the rail docks.
        reset({});
        tideRail.toggle(); where.docks = true; listeners.resize.forEach(f => f()); out.widened = state();

        // Open, then a dialog opens in the page under it (the palette, from Ctrl+K).
        reset({});
        tideRail.toggle(); dialog = el('palette'); changed(); out.dialogUnder = state();

        // A dialog inside the rail itself is the rail's own.
        reset({});
        tideRail.toggle(); dialog = rail; changed(); out.dialogInRail = state();

        // A dialog that's in the markup but not showing (the reconnect dialog) is nothing: the rail's markup changing
        // (its form opening) leaves it open.
        reset({});
        tideRail.toggle(); dialog = Object.assign(el('reconnect'), { getClientRects: () => [] }); changed(); out.hiddenDialog = state();

        console.log(JSON.stringify(out));
        """;

    // Role and Modal: the rail's role and aria-modal, set only while it's an open overlay (a dialog); a docked or closed
    // rail is the aside's own complementary landmark.
    private sealed record State(string Inert, bool RailInert, bool Over, string? Expanded, string? Focus, int Watching,
        string? Role = null, string? Modal = null);

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
    public async Task TheOpenOverlay_MakesThePageUnderItInert_AndClosingGivesItBack()
    {
        var said = await Run();

        said["overOpen"].Should().Be(new State(Everything, RailInert: false, Over: true, "true", "tide-rail", 1, "dialog", "true"),
            "the overlay is modal, and says so: Tab stays in it, and focus moves into it");
        said["overClosed"].Should().Be(new State("", false, false, "false", "tide-rail-toggle", 0), "closed, the page is back and focus is on the button");
        said["overEscape"].Should().Be(new State("", false, false, "false", "tide-rail-toggle", 0), "Escape closes it the same way");
    }

    [Fact]
    public async Task ADockedRail_LeavesThePageAsItIs()
    {
        var said = await Run();

        said["docked"].Should().Be(new State("", false, false, "true", "tide-rail-toggle", 0));
        said["dockedClosed"].Should().Be(new State("", false, false, "false", "tide-rail-toggle", 0));
    }

    [Fact]
    public async Task NothingStaysInert_WhenTheRailGoes_ItDocks_OrADialogOpensUnderIt()
    {
        var said = await Run();

        (said["railGone"] with { Role = null, Modal = null }).Should().Be(new State("", false, false, "false", "tide-rail", 0), "a page with no rail has nothing over it (the rail that went keeps its attributes, gone with it)");
        said["widened"].Inert.Should().BeEmpty("a docked rail leaves the page as it is");
        said["widened"].Over.Should().BeFalse("where it docks, it isn't an overlay");
        said["dialogUnder"].Inert.Should().BeEmpty("a dialog opened in the page must be usable");
        said["dialogUnder"].Over.Should().BeFalse("so the rail closes for it");
        said["dialogUnder"].Focus.Should().Be("tide-rail-toggle", "focus that was in it goes to the Context button, for the dialog to take and give back there");
        said["dialogInRail"].Should().Be(new State(Everything, false, true, "true", "tide-rail", 1, "dialog", "true"), "a dialog in the rail is the rail's own");
        said["hiddenDialog"].Over.Should().BeTrue("a hidden dialog in the markup doesn't close the rail");
    }
}
