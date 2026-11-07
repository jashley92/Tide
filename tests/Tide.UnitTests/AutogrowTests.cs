using System.Diagnostics;
using FluentAssertions;

namespace Tide.UnitTests;

/// <summary>
/// The text-box fallback for browsers without CSS field-sizing (wwwroot/js/autogrow.js), run in Node over a small
/// stand-in for the page. It set every box's height to auto on any change to the page, so a page scrolled near its
/// bottom jumped (the page was briefly shorter), and it didn't fit a box that came into view (a details opening, a
/// hidden tab shown), which kept the height it had while hidden.
/// </summary>
public class AutogrowTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Tide.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repo root (Tide.sln).");
    }

    // Just enough of a page: text boxes 20px a line when shown and 0 wide when hidden; setting a height of auto
    // shortens the page, so the browser pulls the scroll up (as it does near the bottom); the observers and a frame run
    // when the test says.
    private const string Page = """
        const vm = require('vm'), fs = require('fs');
        const listeners = {};
        let mutated = null, resized = null;
        const boxes = [];
        global.window = global;
        global.scrollX = 0; global.scrollY = 900;
        global.addEventListener = () => {};
        global.scrollTo = (x, y) => { global.scrollX = x; global.scrollY = y; };
        global.requestAnimationFrame = f => f();
        global.MutationObserver = class { constructor(f) { mutated = f; } observe() {} };
        global.ResizeObserver = class { constructor(f) { resized = f; } observe(el) { el.observed = true; } };
        function box(value, width) {
            const b = { value, clientWidth: width, autos: 0, observed: false, matches: s => s === 'textarea.tide-autogrow' };
            let h = '';
            b.style = {};
            Object.defineProperty(b.style, 'height', { get: () => h, set: v => { h = v; if (v === 'auto') { b.autos++; global.scrollY = Math.min(global.scrollY, 500); } } });
            Object.defineProperty(b, 'scrollHeight', { get: () => b.clientWidth ? 20 * b.value.split('\n').length : 0 });
            Object.defineProperty(b, 'offsetHeight', { get: () => 2 });
            Object.defineProperty(b, 'clientHeight', { get: () => 0 });
            boxes.push(b);
            return b;
        }
        global.document = {
            documentElement: {},
            addEventListener: (t, f) => (listeners[t] = listeners[t] || []).push(f),
            querySelectorAll: () => boxes.slice(),
        };
        vm.runInThisContext(fs.readFileSync(process.argv[2], 'utf8'));
        const out = {};
        const shown = box('one\ntwo', 300), other = box('a', 300), hidden = box('x\ny\nz', 0);
        mutated();   // Blazor draws the page
        out.firstDraw = [shown.autos, other.autos, hidden.autos];
        out.firstHeight = shown.style.height;
        global.scrollY = 900;
        mutated();   // something else on the page changes
        out.nothingChanged = [shown.autos, other.autos];
        out.scrollAfterNothing = global.scrollY;
        other.value = 'a\nb\nc';
        mutated();
        out.oneChanged = [shown.autos, other.autos];
        out.otherHeight = other.style.height;
        out.scrollKept = global.scrollY;
        out.observed = hidden.observed;
        hidden.clientWidth = 300;   // its tab shown
        if (resized) resized([{ target: hidden }]);
        out.hiddenHeightOnShow = hidden.style.height;
        // A box in a details, hidden until it opens.
        const inside = box('p\nq', 0);
        const details = { open: true, querySelectorAll: () => [inside] };
        mutated();
        out.insideBeforeOpen = inside.autos;
        inside.clientWidth = 300;
        (listeners.toggle || []).forEach(f => f({ target: details }));
        out.insideHeightOnOpen = inside.style.height;
        console.log(JSON.stringify(out));
        """;

    [Fact]
    public async Task OnlyChangedBoxesAreRefitted_TheScrollIsKept_AndABoxIsFittedWhenItComesIntoView()
    {
        var script = Path.Combine(Path.GetTempPath(), "tide-autogrow-test-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(script, Page);
        try
        {
            var psi = new ProcessStartInfo("node", [script, Path.Combine(Root(), "src", "Tide.Web", "wwwroot", "js", "autogrow.js")])
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
                var said = System.Text.Json.JsonDocument.Parse(stdout).RootElement;
                int[] Ints(string name) => [.. said.GetProperty(name).EnumerateArray().Select(e => e.GetInt32())];
                string Text(string name) => said.GetProperty(name).ToString();
                Ints("firstDraw").Should().Equal([1, 1, 0], "the shown boxes are fitted when drawn, the hidden one isn't");
                Text("firstHeight").Should().Be("42px");
                Ints("nothingChanged").Should().Equal([1, 1], "a change elsewhere on the page re-fits no box");
                said.GetProperty("scrollAfterNothing").GetInt32().Should().Be(900);
                Ints("oneChanged").Should().Equal([1, 2], "only the box whose text changed is re-fitted");
                Text("otherHeight").Should().Be("62px");
                said.GetProperty("scrollKept").GetInt32().Should().Be(900, "fitting a box puts the scroll back where it was");
                said.GetProperty("observed").GetBoolean().Should().BeTrue("a box is watched for coming into view");
                Text("hiddenHeightOnShow").Should().Be("62px", "a box is fitted when its tab is shown");
                said.GetProperty("insideBeforeOpen").GetInt32().Should().Be(0);
                Text("insideHeightOnOpen").Should().Be("42px", "and when the details it's in opens");
            }
        }
        finally { File.Delete(script); }
    }
}
