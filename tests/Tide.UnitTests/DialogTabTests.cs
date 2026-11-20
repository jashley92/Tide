using System.Diagnostics;
using AwesomeAssertions;

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
            e.getClientRects = () => [{}];
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
        global.document = { addEventListener: (t, f) => listeners.push(f), activeElement: null, querySelector: sel => page.querySelector(sel), querySelectorAll: sel => page.querySelectorAll(sel) };
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
        // A confirmation open on the page, its first part inert, with focus outside it; a rail open over the page before it
        // in the markup is modal too, but the confirmation is the one on top.
        const ok = el('ok', 'button', 'btn btn-primary');
        const modal = el('modal', 'div', 'tide-modal', { tabindex: '-1' }, [el('busy', 'div', '', { inert: '' }, [el('x', 'button', 'btn')]), ok]);
        const outside = el('outside', 'button', 'btn');
        const rail = el('rail', 'aside', 'tide-rail', { 'aria-modal': 'true' }, [el('railButton', 'button', 'btn')]);
        const page = el('page', 'div', '', {}, [outside, rail, modal]);
        const out = {
            tabFromApply: press(apply, 'Tab'),
            shiftTabFromBox: press(box, 'Tab', true),
            tabFromPanel: press(panel, 'Tab'),
            shiftTabFromPanel: press(panel, 'Tab', true),
            tabInTheMiddle: press(box, 'Tab'),
            tabIntoConfirmation: press(outside, 'Tab'),
        };
        // A dialog opened over another (a confirmation over the picker): each close gives focus back to what opened that
        // dialog, the inner one's opener inside the outer, then the outer one's on the page. The elements need isConnected;
        // Blazor takes a closed dialog out of the page before it calls close.
        [outside, box, apply, ok].forEach(e => e.isConnected = true);
        outside.focus();
        window.tideDialog.open(box);             // A opens from the page; focus in A
        apply.focus();                           // A's Apply opens B
        window.tideDialog.open(ok);
        ok.isConnected = false;                  // B closes
        window.tideDialog.close();
        out.afterInnerClose = document.activeElement.id;
        box.isConnected = false;                 // A closes
        window.tideDialog.close();
        out.afterOuterClose = document.activeElement.id;

        // Dialogs that close without calling close (the palette running a command, the guide ending, one thrown away
        // while open) leave nothing behind: a later close never sends focus to what opened them.
        const dialog = (id, kid) => el(id, 'div', 'tide-modal', { role: 'dialog' }, [kid]);
        const aOpener = el('aOpener', 'button', 'btn'), aField = el('aField', 'button', 'btn'), aOther = el('aOther', 'button', 'btn');
        const pBox = el('pBox', 'input', 'form-control'), bOk = el('bOk', 'button', 'btn');
        const a = el('a', 'div', 'tide-modal', { role: 'dialog' }, [aField, aOther]), p = dialog('p', pBox), b = el('b', 'div', 'tide-modal', { role: 'alertdialog' }, [bOk]);
        [aOpener, aField, aOther, pBox, bOk, a, p, b].forEach(e => e.isConnected = true);
        aOpener.focus();
        window.tideDialog.open(aField);          // A opens from the page
        aOther.focus();
        window.tideDialog.open(pBox);            // the palette opens from A's other control...
        p.isConnected = false;                   // ...and runs a command: gone, with no close
        aField.focus();
        window.tideDialog.open(bOk);             // B opens from A's field
        b.isConnected = false;
        window.tideDialog.close();               // B closes
        out.staleInnerClose = document.activeElement.id;
        a.isConnected = false;
        window.tideDialog.close();               // A closes
        out.staleOuterClose = document.activeElement.id;
        // The palette's command run (forget), then a close nothing matches: focus stays where the command put it.
        p.isConnected = true;
        aOther.focus();
        window.tideDialog.open(pBox);
        p.isConnected = false;
        window.tideDialog.forget();
        aOpener.focus();
        window.tideDialog.close();
        out.unmatchedClose = document.activeElement.id;
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
                var said = System.Text.Json.JsonElement.Parse(stdout);
                string At(string name) => said.GetProperty(name).GetString()!;
                At("tabFromApply").Should().Be("box", "Tab from the last control that can take focus goes round to the first, not out of the dialog");
                At("shiftTabFromBox").Should().Be("apply", "Shift+Tab from the first goes round to the last, not to a folded cell");
                At("tabFromPanel").Should().Be("box", "with the dialog itself focused, Tab goes to its first control");
                At("shiftTabFromPanel").Should().Be("apply", "and Shift+Tab to its last");
                At("tabInTheMiddle").Should().Be("browser", "between the ends, the browser's own Tab moves on");
                At("tabIntoConfirmation").Should().Be("ok", "Tab from behind an open confirmation goes to its first control that can take focus, not to a rail under it");
                At("afterInnerClose").Should().Be("apply", "a dialog opened over another gives focus back to the control in the outer one that opened it");
                At("afterOuterClose").Should().Be("outside", "and the outer one, closing after it, gives focus back to its own opener on the page");
                At("staleInnerClose").Should().Be("aField", "a dialog that closed without saying so (the palette) is skipped");
                At("staleOuterClose").Should().Be("aOpener", "and the outer dialog gives focus back to its own opener, not the palette's");
                At("unmatchedClose").Should().Be("aOpener", "a forgotten dialog leaves nothing for a later close to send focus to");
            }
        }
        finally { File.Delete(script); }
    }
}
