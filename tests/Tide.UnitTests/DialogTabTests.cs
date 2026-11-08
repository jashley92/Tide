using System.Diagnostics;
using FluentAssertions;

namespace Tide.UnitTests;

/// <summary>
/// Tab in a dialog (wwwroot/js/dialog.js), run in Node over a small stand-in for the page, as
/// <see cref="FilterMenuKeysTests"/> runs filters.js. Tab cycles through the dialog's controls between its first and its
/// last. A control inside an inert part (the ATT&amp;CK picker's folded columns) can't take focus, so it must never be
/// that first or last: if it were, Tab from the real last control would leave the dialog, and Shift+Tab would go nowhere.
/// </summary>
public class DialogTabTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Tide.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repo root (Tide.sln).");
    }

    // Just enough of a DOM for dialog.js: elements with classes and attributes, closest/querySelector(All) by tag, class
    // and [attr] / [attr="v"] / :not([attr]) selectors, and a document listener.
    private const string Page = """
        const vm = require('vm'), fs = require('fs');
        function el(id, tag, cls, attrs, kids) {
            const e = { id, tagName: tag.toUpperCase(), cls: cls ? cls.split(' ') : [], attrs: attrs || {}, children: kids || [], parentElement: null };
            e.children.forEach(k => k.parentElement = e);
            e.getAttribute = n => (n in e.attrs ? e.attrs[n] : null);
            e.hasAttribute = n => n in e.attrs;
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
        global.document = { addEventListener: (t, f) => listeners.push(f), activeElement: null, querySelector: sel => page.querySelector(sel) };
        global.window = global;
        vm.runInThisContext(fs.readFileSync(process.argv[2], 'utf8'));
        // A key on the focused element: where focus is after dialog.js has handled it ("browser" when dialog.js left the
        // key to the browser's own Tab).
        function press(target, key, shift) {
            document.activeElement = target;
            const e = { key, shiftKey: !!shift, target, prevented: false, preventDefault() { this.prevented = true; } };
            listeners.forEach(f => f(e));
            return e.prevented ? document.activeElement.id : 'browser';
        }
        const cell = id => el(id, 'div', 'tide-atkm-cell', { role: 'button', tabindex: '0' });
        // The picker with folded columns at both ends of its controls: before its filter box, and after Apply.
        const box = el('box', 'input', 'form-control');
        const apply = el('apply', 'button', 'btn btn-primary');
        const panel = el('panel', 'div', 'tide-atkm-panel', { tabindex: '-1' }, [
            el('foldedFirst', 'div', 'tide-atkm-col tide-atkm-col-hidden', { inert: '' }, [cell('c1')]),
            box,
            el('shown', 'div', 'tide-atkm-col', {}, [cell('c2')]),
            apply,
            el('foldedLast', 'div', 'tide-atkm-col tide-atkm-col-hidden', { inert: '' }, [cell('c3'), cell('c4')]),
        ]);
        // A confirmation open on the page, its first part inert, with focus outside it.
        const ok = el('ok', 'button', 'btn btn-primary');
        const modal = el('modal', 'div', 'tide-modal', { tabindex: '-1' }, [el('busy', 'div', '', { inert: '' }, [el('x', 'button', 'btn')]), ok]);
        const outside = el('outside', 'button', 'btn');
        const page = el('page', 'div', '', {}, [outside, modal]);
        const out = {
            tabFromApply: press(apply, 'Tab'),
            shiftTabFromBox: press(box, 'Tab', true),
            tabFromPanel: press(panel, 'Tab'),
            shiftTabFromPanel: press(panel, 'Tab', true),
            tabInTheMiddle: press(box, 'Tab'),
            tabIntoConfirmation: press(outside, 'Tab'),
        };
        console.log(JSON.stringify(out));
        """;

    [Fact]
    public async Task Tab_CyclesBetweenTheControlsThatCanTakeFocus_LeavingOutTheOnesInAFoldedColumn()
    {
        var script = Path.Combine(Path.GetTempPath(), "tide-dialog-test-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(script, Page);
        try
        {
            var psi = new ProcessStartInfo("node", [script, Path.Combine(Root(), "src", "Tide.Web", "wwwroot", "js", "dialog.js")])
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
                string At(string name) => said.GetProperty(name).GetString()!;
                At("tabFromApply").Should().Be("box", "Tab from the last control that can take focus goes round to the first, not out of the dialog");
                At("shiftTabFromBox").Should().Be("apply", "Shift+Tab from the first goes round to the last, not to a folded cell");
                At("tabFromPanel").Should().Be("box", "with the dialog itself focused, Tab goes to its first control");
                At("shiftTabFromPanel").Should().Be("apply", "and Shift+Tab to its last");
                At("tabInTheMiddle").Should().Be("browser", "between the ends, the browser's own Tab moves on");
                At("tabIntoConfirmation").Should().Be("ok", "Tab from behind an open confirmation goes to its first control that can take focus");
            }
        }
        finally { File.Delete(script); }
    }
}
