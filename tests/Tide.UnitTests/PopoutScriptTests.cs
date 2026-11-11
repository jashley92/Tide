using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>
/// A popout closing when it's done with (wwwroot/js/popout.js), run in Node over a small stand-in for the page, as
/// <see cref="RailOverlayTests"/> runs theme.js. Triage's Attach to… stayed open after a click elsewhere, and Escape then
/// did nothing to it: it heard Escape only in its own box, so that Escape went to Triage, which closed the reading pane.
/// Now Escape anywhere, a press outside or focus outside closes it, and that Escape is the popout's alone.
/// </summary>
public class PopoutScriptTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Tide.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repo root (Tide.sln).");
    }

    // The page: a window and a document whose listeners are called as a browser calls them (the window's captured ones,
    // then the document's, then the page's own as the key bubbles up) until one stops it. The page's own keydown stands
    // for Triage's keys. Two popouts, each an element holding its button and panel, and an element outside both.
    private const string Page = """
        const vm = require('vm'), fs = require('fs');
        const on = { window: [], capture: [], bubble: [] };
        const add = list => (type, f, capture) => list(capture).push({ type, f });
        const remove = list => (type, f, capture) => { const l = list(capture), i = l.findIndex(x => x.type === type && x.f === f); if (i >= 0) l.splice(i, 1); };
        global.window = global;
        global.getComputedStyle = () => ({});
        global.addEventListener = add(() => on.window);
        global.removeEventListener = remove(() => on.window);
        global.document = { addEventListener: add(c => c ? on.capture : on.bubble), removeEventListener: remove(c => c ? on.capture : on.bubble) };
        vm.runInThisContext(fs.readFileSync(process.argv[2], 'utf8'));

        const heard = [];
        document.addEventListener('keydown', e => heard.push('page:' + e.key));
        // A popout's holder, its button (which takes focus back after Escape) and its box, and what .NET is told.
        function holder(name) {
            const button = { isConnected: true, focus() { heard.push(name + ':focus'); } };
            const h = { button, kids: [button, {}], attrs: {}, children: [] };
            h.contains = x => x === h || h.kids.includes(x);
            h.hasAttribute = n => n in h.attrs;
            h.setAttribute = (n, v) => { h.attrs[n] = v; };
            h.removeAttribute = n => { delete h.attrs[n]; };
            return h;
        }
        const a = holder('a'), b = holder('b'), outside = {};
        const watch = (h, name) => tidePopout.watch(h, h.button, { invokeMethodAsync: m => { heard.push(name + ':' + m); return Promise.resolve(); } });
        function fire(type, props) {
            let stopped = false;
            const e = Object.assign({ type, isComposing: false, preventDefault() { e.prevented = true; }, stopImmediatePropagation() { stopped = true; } }, props);
            for (const list of [on.window, on.capture, on.bubble])
                for (const l of list.slice()) { if (stopped) return e; if (l.type === type) l.f(e); }
            return e;
        }
        const listening = () => on.window.length + on.capture.length;
        const out = {};
        const step = (name, f) => { heard.length = 0; f(); out[name] = { heard: heard.slice(), listening: listening() }; };

        let id;
        step('escapeOutside', () => { watch(a, 'a'); fire('keydown', { key: 'Escape', target: outside }); });
        step('nextEscape', () => fire('keydown', { key: 'Escape', target: outside }));
        step('escapeInside', () => { watch(a, 'a'); fire('keydown', { key: 'Escape', target: a.kids[1] }); });
        step('otherKey', () => { watch(a, 'a'); fire('keydown', { key: 'j', target: outside }); });
        step('pressInside', () => fire('pointerdown', { target: a.kids[0] }));
        step('focusInside', () => fire('focusin', { target: a.kids[1] }));
        step('pressOutside', () => fire('pointerdown', { target: outside }));
        step('focusOutside', () => { watch(a, 'a'); fire('focusin', { target: outside }); });
        step('unwatched', () => { id = watch(a, 'a'); tidePopout.unwatch(id); tidePopout.unwatch(id); fire('keydown', { key: 'Escape', target: outside }); });
        step('twoOpen', () => { watch(a, 'a'); watch(b, 'b'); fire('keydown', { key: 'Escape', target: outside }); });
        step('thenTheFirst', () => fire('keydown', { key: 'Escape', target: outside }));
        // A press on a part of it that takes no focus: the browser focuses the holder, which takes focus while it's open.
        let tabindex;
        step('pressOnBarePart', () => { id = watch(a, 'a'); tabindex = a.attrs.tabindex; fire('pointerdown', { target: a }); fire('focusin', { target: a }); });
        tidePopout.unwatch(id);
        out.tabindex = { heard: ['open:' + tabindex, 'closed:' + a.attrs.tabindex], listening: listening() };
        // A dialog opened from inside a popout gives focus back to the popout's button (the palette over + Filter).
        id = watch(a, 'a');
        const named = x => x === a.button ? 'a-button' : x === null ? 'none' : 'other';
        out.opener = { heard: [named(tidePopout.opener(a.kids[1])), named(tidePopout.opener(outside))], listening: 0 };
        tidePopout.unwatch(id);
        out.opener.heard.push(named(tidePopout.opener(a.kids[1])));
        console.log(JSON.stringify(out));
        """;

    private sealed record Step(string[] Heard, int Listening);

    private static async Task<Dictionary<string, Step>> Run()
    {
        var script = Path.Combine(Path.GetTempPath(), "tide-popout-test-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(script, Page);
        try
        {
            var psi = new ProcessStartInfo("node", [script, Path.Combine(Root(), "src", "Tide.Web", "wwwroot", "js", "popout.js")])
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
                return JsonSerializer.Deserialize<Dictionary<string, Step>>(stdout, JsonSerializerOptions.Web)!;
            }
        }
        finally { File.Delete(script); }
    }

    [Fact]
    public async Task Escape_AnywhereOnThePage_ClosesTheOpenPopout_AndThePageDoesntHearIt()
    {
        var said = await Run();

        said["escapeOutside"].Should().BeEquivalentTo(new Step(["a:Dismiss", "a:focus"], 0), "focus outside it: it closes, focus goes back to its button, and Triage's keys don't hear that Escape");
        said["nextEscape"].Should().BeEquivalentTo(new Step(["page:Escape"], 0), "the next Escape is the page's again");
        said["escapeInside"].Should().BeEquivalentTo(new Step(["a:Dismiss", "a:focus"], 0), "focus in it: the same");
        said["otherKey"].Heard.Should().Equal(["page:j"], "other keys are the page's as ever");
    }

    [Fact]
    public async Task APressOrFocusOutside_ClosesIt_AndInsideLeavesItOpen()
    {
        var said = await Run();

        said["pressInside"].Should().BeEquivalentTo(new Step([], 3), "its button and its box are part of it");
        said["focusInside"].Should().BeEquivalentTo(new Step([], 3));
        said["pressOutside"].Should().BeEquivalentTo(new Step(["a:Dismiss"], 0), "a click elsewhere closes it, focus staying where it went");
        said["focusOutside"].Should().BeEquivalentTo(new Step(["a:Dismiss"], 0), "so does Tab out of it");
    }

    // A click on the Attach picker's hint line, or the export panel's label, sent focus to the row or the page around it,
    // and that focus outside closed it.
    [Fact]
    public async Task APressOnAPartThatTakesNoFocus_LeavesItOpen_TheHolderTakingFocus()
    {
        var said = await Run();

        said["pressOnBarePart"].Should().BeEquivalentTo(new Step([], 3), "focus goes to the holder, which is part of it");
        said["tabindex"].Heard.Should().Equal(["open:-1", "closed:undefined"], "it takes focus (out of the Tab order) only while open");
    }

    [Fact]
    public async Task AnElementInAnOpenPopout_IsOwnedByItsButton_ForADialogToGiveFocusBackTo()
    {
        var said = await Run();

        said["opener"].Heard.Should().Equal(["a-button", "none", "none"], "only while it's open, and only for what's in it");
    }

    [Fact]
    public async Task Unwatching_StopsListening_AndEscapeClosesOnlyTheLastOpened()
    {
        var said = await Run();

        said["unwatched"].Should().BeEquivalentTo(new Step(["page:Escape"], 0), "closed another way, it leaves nothing listening (twice is harmless)");
        said["twoOpen"].Should().BeEquivalentTo(new Step(["b:Dismiss", "b:focus"], 3), "one Escape closes one popout, the last opened");
        said["thenTheFirst"].Should().BeEquivalentTo(new Step(["a:Dismiss", "a:focus"], 0));
    }
}
