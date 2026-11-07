using System.Diagnostics;
using FluentAssertions;

namespace Tide.UnitTests;

/// <summary>
/// The filter bar's popover from the keyboard (wwwroot/js/filters.js), run in Node over a small stand-in for the page:
/// the elements FilterBar draws, and the keys a person presses. Tab leaves a menu of choices closed (they aren't tab
/// stops), but a typed filter's form (its box, Remove, Apply) is moved through with Tab, closing only past its ends: it
/// closed on the first Tab, losing what was typed and, in a date box, the step from month to day.
/// </summary>
public class FilterMenuKeysTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Tide.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repo root (Tide.sln).");
    }

    // Just enough of a DOM for filters.js: elements with classes and attributes, closest/querySelector by tag, class and
    // [attr] / [attr="v"] / :not(...) selectors, a document listener, and clicks counted.
    private const string Page = """
        const vm = require('vm'), fs = require('fs');
        function el(tag, cls, attrs, kids) {
            const e = { tagName: tag.toUpperCase(), cls: cls ? cls.split(' ') : [], attrs: attrs || {}, children: kids || [], parentElement: null, clicks: 0 };
            e.children.forEach(k => k.parentElement = e);
            e.classList = { contains: c => e.cls.includes(c) };
            e.getAttribute = n => (n in e.attrs ? e.attrs[n] : null);
            e.hasAttribute = n => n in e.attrs;
            e.click = () => { e.clicks++; };
            e.focus = () => { document.activeElement = e; };
            e.matches = sel => sel.split(',').some(one => compound(e, one.trim()));
            e.closest = sel => { for (let x = e; x; x = x.parentElement) if (x.matches(sel)) return x; return null; };
            e.querySelectorAll = sel => { const out = []; (function walk(x) { x.children.forEach(k => { if (k.matches(sel)) out.push(k); walk(k); }); })(e); return out; };
            e.querySelector = sel => e.querySelectorAll(sel)[0] || null;
            return e;
        }
        function attr(e, a) { const m = /^\[([\w-]+)(?:="([^"]*)")?\]$/.exec(a); return m[2] === undefined ? e.hasAttribute(m[1]) : e.getAttribute(m[1]) === m[2]; }
        function compound(e, sel) {
            const parts = sel.match(/:not\((\[[^\]]+\])\)|\[[^\]]+\]|\.[\w-]+|^[a-z]+/g) || [];
            return parts.every(p => p.startsWith(':not(') ? !attr(e, p.slice(5, -1)) : p.startsWith('[') ? attr(e, p)
                : p.startsWith('.') ? e.cls.includes(p.slice(1)) : e.tagName === p.toUpperCase());
        }
        const listeners = [];
        global.document = { addEventListener: (t, f) => listeners.push(f), activeElement: null };
        global.window = global;
        window.tideRoving = () => null;
        vm.runInThisContext(fs.readFileSync(process.argv[2], 'utf8'));
        function press(target, key, shift) {
            const e = { key, shiftKey: !!shift, ctrlKey: false, metaKey: false, altKey: false, target, prevented: false, preventDefault() { this.prevented = true; } };
            listeners.forEach(f => f(e));
        }
        function bar(pop) {
            const backdrop = el('div', 'tide-popover-backdrop');
            el('div', 'tide-filter-menu', {}, [el('button', 'tide-filter-add'), backdrop, pop]);
            return backdrop;
        }
        const out = {};
        // A typed filter: the way back (not a tab stop), the box, Remove, Apply.
        const back = el('button', 'tide-filter-item tide-filter-back', { role: 'menuitem', tabindex: '-1' });
        const box = el('input', 'form-control', { type: 'date' });
        const remove = el('button', 'btn btn-link', { type: 'button' });
        const apply = el('button', 'btn btn-primary', { type: 'submit' });
        const typed = bar(el('div', 'tide-popover tide-filter-pop', {}, [el('form', '', { role: 'dialog' }, [back, box, remove, apply])]));
        press(box, 'Tab'); out.tabFromBox = typed.clicks;
        press(remove, 'Tab'); out.tabFromRemove = typed.clicks;
        press(apply, 'Tab', true); out.shiftTabFromApply = typed.clicks;
        press(apply, 'Tab'); out.tabPastApply = typed.clicks;
        press(box, 'Tab', true); out.shiftTabBeforeBox = typed.clicks - out.tabPastApply;
        // A menu of choices: none is a tab stop, so Tab leaves it closed.
        const item = el('button', 'tide-filter-item', { role: 'menuitem', tabindex: '-1' });
        const menu = bar(el('div', 'tide-popover tide-filter-pop', {}, [el('div', '', { role: 'menu' }, [item])]));
        press(item, 'Tab'); out.tabInMenu = menu.clicks;
        console.log(JSON.stringify(out));
        """;

    [Fact]
    public async Task Tab_MovesThroughATypedFiltersForm_AndLeavesOnlyPastItsEnds_WhileAMenuClosesAtOnce()
    {
        var script = Path.Combine(Path.GetTempPath(), "tide-filters-test-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(script, Page);
        try
        {
            var psi = new ProcessStartInfo("node", [script, Path.Combine(Root(), "src", "Tide.Web", "wwwroot", "js", "filters.js")])
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
                int Clicks(string name) => said.GetProperty(name).GetInt32();
                Clicks("tabFromBox").Should().Be(0, "Tab from the box goes on to Remove, with the form still open");
                Clicks("tabFromRemove").Should().Be(0, "and from Remove to Apply");
                Clicks("shiftTabFromApply").Should().Be(0, "Shift+Tab goes back through it");
                Clicks("tabPastApply").Should().Be(1, "Tab past Apply leaves it, closed");
                Clicks("shiftTabBeforeBox").Should().Be(1, "as does Shift+Tab before the box");
                Clicks("tabInMenu").Should().Be(1, "a menu's choices aren't tab stops: Tab leaves it closed");
            }
        }
        finally { File.Delete(script); }
    }
}
