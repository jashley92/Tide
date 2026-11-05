// The formatting toolbar over a Markdown text box (MarkdownToolbar.razor). CSP-safe: an external file, wired by event
// delegation, so Blazor needs no interop call. The toolbar names its box (data-md-for); its buttons, and these keys in
// the box, edit the box's text in place:
//   Ctrl+B and Ctrl+I: bold and italic. On a list line, Tab indents and Shift+Tab outdents (off one, Tab moves focus as
//   usual; Escape then Tab always leaves). Enter on a list line starts the next item; Enter on an empty item ends the list.
// Edits go through execCommand('insertText') where the browser has it, so Ctrl+Z undoes them, and each fires an input
// event (and a change event, for a box bound on change) so Blazor's binding and the autosave see it.
// The text transforms are small pure functions on { text, start, end } (window.tideMarkdown.transforms), testable apart.
(function (root) {
    'use strict';

    // A list line: indent, marker ("-", "*", "+", "1." or "1)"), the space after it, and the item's text.
    var LIST = /^( *)([-*+]|\d{1,9}[.)])( +|$)(.*)$/;
    var FENCE = /^ {0,3}(`{3,}|~{3,})/;

    function parse(line) {
        var m = LIST.exec(line);
        if (!m) return null;
        // "**bold**" or "---" aren't list lines: the marker must be followed by a space or end the line.
        var ordered = /\d/.test(m[2]);
        return {
            indent: m[1].length, marker: m[2], gap: m[3], content: m[4], ordered: ordered,
            num: ordered ? parseInt(m[2], 10) : 0, delim: ordered ? m[2].slice(-1) : '',
            col: m[1].length + m[2].length + (m[3].length || 1)   // where the item's text starts: a child's indent
        };
    }

    // Which lines are in a fenced code block (the fences included): nothing here touches them.
    function fenced(lines) {
        var out = [], open = null;
        for (var i = 0; i < lines.length; i++) {
            var m = FENCE.exec(lines[i]);
            if (open) {
                out.push(true);
                if (m && m[1][0] === open[0] && m[1].length >= open.length && lines[i].trim() === m[1]) open = null;
            } else if (m) { out.push(true); open = m[1]; }
            else out.push(false);
        }
        return out;
    }

    function indentOf(line) { return line.length - line.replace(/^ +/, '').length; }
    function blank(line) { return line.trim() === ''; }

    // The lines the selection covers: [first, last], and the offset each line starts at.
    function span(text, start, end) {
        var lines = text.split('\n'), starts = [], pos = 0;
        for (var i = 0; i < lines.length; i++) { starts.push(pos); pos += lines[i].length + 1; }
        function at(offset) {
            for (var j = lines.length - 1; j >= 0; j--) if (starts[j] <= offset) return j;
            return 0;
        }
        var first = at(start), last = at(end);
        // A selection ending at the start of a line (a triple-click) doesn't take that line.
        if (end > start && last > first && starts[last] === end) last--;
        return { lines: lines, starts: starts, first: first, last: last };
    }

    function offsets(lines) {
        var starts = [], pos = 0;
        for (var i = 0; i < lines.length; i++) { starts.push(pos); pos += lines[i].length + 1; }
        return starts;
    }

    // The lines that belong to item i: those after it, up to the first non-blank line indented less than its text.
    function childrenEnd(lines, i, col) {
        var j = i + 1, end = i;
        for (; j < lines.length; j++) {
            if (blank(lines[j])) continue;
            if (indentOf(lines[j]) < col) break;
            end = j;
        }
        return end;
    }

    // Moves lines [from, to] by delta spaces (out no further than the margin).
    function shift(lines, from, to, delta) {
        for (var k = from; k <= to; k++) {
            if (blank(lines[k]) || delta === 0) continue;
            lines[k] = delta > 0 ? ' '.repeat(delta) + lines[k] : lines[k].slice(Math.min(-delta, indentOf(lines[k])));
        }
    }

    // Replaces line i's indent and marker, moving its children with its text so they stay its children.
    function remark(lines, i, indent, marker) {
        var p = parse(lines[i]);
        var head = marker ? ' '.repeat(indent) + marker + ' ' : ' '.repeat(indent);
        var newCol = head.length;
        var kids = childrenEnd(lines, i, p.col);
        lines[i] = head + p.content;
        shift(lines, i + 1, kids, newCol - p.col);
    }

    // Numbers again each numbered list that holds a touched line (touched[k] truthy): from 1 when its first item's entry
    // is true (a new list), else on from its first item's own number.
    function renumber(lines, touched) {
        var fence = fenced(lines), open = [], runs = [];
        for (var i = 0; i < lines.length; i++) {
            if (fence[i] || blank(lines[i])) continue;
            var p = parse(lines[i]), ind = indentOf(lines[i]);
            // A line closes the lists indented deeper than it (a list item: as deep as it, unless it's the same kind).
            open = open.filter(function (r) { return p ? r.indent < ind || (r.indent === ind && p.ordered) : r.indent < ind; });
            if (!p) continue;
            if (!p.ordered) continue;
            var run = open.filter(function (r) { return r.indent === ind; })[0];
            if (!run) { run = { indent: ind, items: [] }; open.push(run); runs.push(run); }
            run.items.push(i);
        }
        runs.forEach(function (run) {
            if (!run.items.some(function (k) { return touched[k]; })) return;
            var n = touched[run.items[0]] === true ? 1 : parse(lines[run.items[0]]).num;
            run.items.forEach(function (k) {
                var p = parse(lines[k]);
                if (p.num !== n || p.gap !== ' ') remark(lines, k, p.indent, n + p.delim);
                n++;
            });
        });
    }

    // The new text with the selected lines selected whole (after wrapping each).
    function result(lines, first, last) {
        var starts = offsets(lines);
        return { text: lines.join('\n'), start: starts[first], end: starts[last] + lines[last].length };
    }

    // The new text with the selection kept where it was in its lines' text: a change at the start of a line (a marker,
    // an indent) leaves what's after the cursor alone, so the cursor keeps its distance from the line's end. A selection
    // from the start of a line still starts there.
    function kept(sp, lines, state) {
        var starts = offsets(lines);
        function at(offset, line, isStart) {
            var col = offset - sp.starts[line], oldLen = sp.lines[line].length;
            if (isStart && col === 0 && state.end > state.start) return starts[line];
            return starts[line] + Math.max(0, Math.min(lines[line].length, lines[line].length - (oldLen - col)));
        }
        var start = at(state.start, sp.first, true);
        if (state.end === state.start) return { text: lines.join('\n'), start: start, end: start };
        // A selection ending at the start of the next line (a triple-click) still does.
        var end = sp.last + 1 < lines.length && state.end === sp.starts[sp.last + 1] ? starts[sp.last + 1] : at(state.end, sp.last, false);
        return { text: lines.join('\n'), start: start, end: end };
    }

    // Bold (**) or italic (*): wraps the selection, unwraps it when it's already wrapped, or puts the pair at the cursor.
    function wrap(state, mark) {
        var t = state.text, s = state.start, e = state.end;
        if (s === e) return { text: t.slice(0, s) + mark + mark + t.slice(e), start: s + mark.length, end: s + mark.length };
        var sel = t.slice(s, e);
        if (sel.indexOf('\n') >= 0) return wrapLines(state, mark);
        // A double-click takes the space after a word: it stays outside the marks.
        var lead = sel.length - sel.replace(/^\s+/, '').length, trail = sel.length - sel.replace(/\s+$/, '').length;
        if (lead === sel.length) return null;
        s += lead; e -= trail; sel = t.slice(s, e);
        var n = mark.length;
        function marked(before, after) {
            if (n === 2) return before.slice(-2) === '**' && after.slice(0, 2) === '**';
            // Italic: one star either side, not the inside of a bold pair (three stars is both).
            var b = before.slice(-3), a = after.slice(0, 3);
            return (b === '***' || (b.slice(-1) === '*' && b.slice(-2) !== '**')) && (a === '***' || (a[0] === '*' && a.slice(0, 2) !== '**'));
        }
        if (marked(t.slice(0, s), t.slice(e)))
            return { text: t.slice(0, s - n) + sel + t.slice(e + n), start: s - n, end: e - n };
        if (sel.length > 2 * n && marked(sel.slice(0, n), sel.slice(-n)) && sel.slice(0, n) === mark && sel.slice(-n) === mark)
            return { text: t.slice(0, s) + sel.slice(n, -n) + t.slice(e), start: s, end: e - 2 * n };
        return { text: t.slice(0, s) + mark + sel + mark + t.slice(e), start: s + n, end: e + n };
    }

    // Whether a line's text is wrapped whole in the mark: "**a**" for bold; "*a*" (not "**a**") or "***a***" for italic.
    // Not "**a** and **b**": that's two pieces, not one wrapped line.
    function wrappedBy(body, mark) {
        var n = mark.length;
        if (body.length <= 2 * n) return false;
        var inner = body.slice(n, -n);
        if (n === 2) return body.slice(0, 2) === '**' && body.slice(-2) === '**' && inner.indexOf('**') < 0;
        var opens = body.slice(0, 3) === '***' || (body[0] === '*' && body[1] !== '*');
        var closes = body.slice(-3) === '***' || (body.slice(-1) === '*' && body.slice(-2, -1) !== '*');
        return opens && closes && inner.replace(/\*\*/g, '').indexOf('*') < 0;
    }

    // Over several lines, each line's text is wrapped (Markdown's emphasis doesn't cross lines well), after any marker;
    // when every one already is, they're all unwrapped, as one line's selection is.
    function wrapLines(state, mark) {
        var sp = span(state.text, state.start, state.end), lines = sp.lines.slice(), fence = fenced(lines), rows = [];
        for (var i = sp.first; i <= sp.last; i++) {
            if (fence[i] || blank(lines[i])) continue;
            var p = parse(lines[i]);
            var head = p ? lines[i].length - p.content.length : indentOf(lines[i]);
            var body = lines[i].slice(head).replace(/\s+$/, '');
            if (body) rows.push({ i: i, head: head, body: body });
        }
        var all = rows.length > 0 && rows.every(function (r) { return wrappedBy(r.body, mark); });
        rows.forEach(function (r) {
            var line = lines[r.i].slice(0, r.head);
            if (all) lines[r.i] = line + r.body.slice(mark.length, -mark.length);
            else if (!wrappedBy(r.body, mark)) lines[r.i] = line + mark + r.body + mark;
        });
        return result(lines, sp.first, sp.last);
    }

    // A link: the selection becomes its text, with the address to type selected (or, for a selected address, the text).
    function link(state) {
        var t = state.text, s = state.start, e = state.end, sel = t.slice(s, e);
        if (sel.indexOf('\n') >= 0) return null;
        // A double-click takes the space after a word: it stays outside the link, as it does outside bold.
        var lead = sel.length - sel.replace(/^\s+/, '').length, trail = sel.length - sel.replace(/\s+$/, '').length;
        if (lead < sel.length) { s += lead; e -= trail; } else s = e;
        sel = t.slice(s, e);
        if (/^(https?:\/\/|mailto:)\S+$/i.test(sel)) {
            var out = '[text](' + sel + ')';
            return { text: t.slice(0, s) + out + t.slice(e), start: s + 1, end: s + 5 };
        }
        var label = sel || 'text', url = 'https://';
        var ins = '[' + label + '](' + url + ')';
        var at = s + label.length + 3;
        return sel ? { text: t.slice(0, s) + ins + t.slice(e), start: at, end: at + url.length }
            : { text: t.slice(0, s) + ins + t.slice(e), start: s + 1, end: s + 1 + label.length };
    }

    // A bulleted ("-") or numbered list on the selected lines; off again when they all already are one.
    function toggleList(state, ordered) {
        var sp = span(state.text, state.start, state.end), lines = sp.lines.slice(), fence = fenced(lines), touched = {};
        var rows = [];
        for (var i = sp.first; i <= sp.last; i++) if (!fence[i] && !blank(lines[i])) rows.push(i);
        if (rows.length === 0) {
            // An empty line: start an item there.
            if (fence[sp.first]) return null;
            var ind = indentOf(lines[sp.first]);
            lines[sp.first] = ' '.repeat(ind) + (ordered ? '1. ' : '- ');
            touched[sp.first] = true;
            renumber(lines, touched);
            var at = offsets(lines)[sp.first] + lines[sp.first].length;
            return { text: lines.join('\n'), start: at, end: at };
        }
        var all = rows.every(function (k) { var p = parse(lines[k]); return p && p.ordered === ordered; });
        rows.forEach(function (k) {
            var p = parse(lines[k]);
            if (all) { remark(lines, k, p.indent, null); return; }
            if (!p) {
                var ind = indentOf(lines[k]);
                lines[k] = ' '.repeat(ind) + (ordered ? '1. ' : '- ') + lines[k].slice(ind);
            } else if (p.ordered !== ordered) remark(lines, k, p.indent, ordered ? '1.' : '-');
            touched[k] = true;
        });
        renumber(lines, touched);
        if (all) renumber(lines, neighbours(lines, sp.first, sp.last));
        return kept(sp, lines, state);
    }

    // The list item just after a range: a list split by taking items out of it is numbered again.
    function neighbours(lines, first, last) {
        var t = {};
        for (var k = last + 1; k < lines.length; k++) if (parse(lines[k])) { t[k] = true; break; }
        return t;
    }

    // Indent (one level deeper: under the item before it) or outdent (out to its parent's level) the selected list
    // lines, each with its children. Markdig, as CommonMark, nests an item under another when it starts where the
    // other's text starts: two spaces under "- ", three under "1. ". Returns null when no line can move.
    function indent(state, out) {
        var sp = span(state.text, state.start, state.end), lines = sp.lines.slice(), fence = fenced(lines);
        var moved = {}, touched = {}, any = false;
        for (var i = sp.first; i <= sp.last; i++) {
            if (moved[i] || fence[i]) continue;
            var p = parse(lines[i]);
            if (!p) continue;
            var to = out ? outdentTo(lines, fence, i, p) : indentTo(lines, fence, i, p);
            if (to === null || to === p.indent) continue;
            var kids = childrenEnd(lines, i, p.col);
            shift(lines, i, kids, to - p.indent);
            for (var k = i; k <= kids; k++) { moved[k] = true; if (parse(lines[k])) touched[k] = true; }
            any = true;
        }
        if (!any) return null;
        // Moved numbered items start their new list at 1, or carry on the one they joined; the list they left closes up.
        renumber(lines, firstsOnly(lines, touched));
        return kept(sp, lines, state);
    }

    // For renumber: a moved item starts its new list at 1 when it's the first item there, or carries on the list it
    // joined; the list it came out of closes up.
    function firstsOnly(lines, touched) {
        var t = {};
        Object.keys(touched).forEach(function (k) {
            k = +k;
            t[k] = previousSibling(lines, k) === null ? true : 'keep';
            for (var j = k + 1; j < lines.length; j++) if (parse(lines[j]) && !touched[j]) { if (!(j in t)) t[j] = 'keep'; break; }
        });
        return t;
    }

    function previousSibling(lines, i) {
        var p = parse(lines[i]);
        for (var j = i - 1; j >= 0; j--) {
            if (blank(lines[j])) continue;
            var q = parse(lines[j]), ind = indentOf(lines[j]);
            if (q && ind === p.indent) return j;
            if (ind < p.indent || (!q && ind === 0)) return null;
        }
        return null;
    }

    function indentTo(lines, fence, i, p) {
        for (var j = i - 1; j >= 0; j--) {
            if (fence[j] || blank(lines[j])) continue;
            var q = parse(lines[j]);
            if (!q) { if (indentOf(lines[j]) === 0) return null; continue; }   // a paragraph: the list ended above it
            if (q.indent === p.indent) return q.col;   // its sibling above: under that one's text
            if (q.indent < p.indent) return null;      // its parent: it's that item's first child already
        }
        return null;
    }

    function outdentTo(lines, fence, i, p) {
        if (p.indent === 0) return null;
        for (var j = i - 1; j >= 0; j--) {
            if (fence[j] || blank(lines[j])) continue;
            var q = parse(lines[j]);
            if (!q) { if (indentOf(lines[j]) === 0) break; continue; }
            if (q.indent < p.indent) return q.indent;   // its parent: out to that one's level
        }
        return 0;
    }

    // Enter on a list line: the next item, with the same indent and marker (the next number); on an empty item, the
    // list ends there. Null when Enter should just be Enter.
    function enter(state) {
        if (state.start !== state.end) return null;
        var sp = span(state.text, state.start, state.end), lines = sp.lines.slice(), i = sp.first;
        if (fenced(lines)[i]) return null;
        var p = parse(lines[i]);
        if (!p) return null;
        var col = state.start - sp.starts[i], head = lines[i].length - p.content.length;
        if (col < head) return null;   // the cursor's in the indent or the marker
        var starts;
        if (p.content.trim() === '') {
            // A blank line, then the cursor: text typed straight under an item would be read as more of that item.
            var gapped = i > 0 && !blank(lines[i - 1]);
            if (gapped) lines.splice(i, 1, '', ''); else lines[i] = '';
            renumber(lines, neighbours(lines, i, gapped ? i + 1 : i));
            starts = offsets(lines);
            var to = gapped ? i + 1 : i;
            return { text: lines.join('\n'), start: starts[to], end: starts[to] };
        }
        var before = lines[i].slice(0, col).replace(/\s+$/, ''), after = lines[i].slice(col).replace(/^\s+/, '');
        var marker = p.ordered ? (p.num + 1) + p.delim : p.marker;
        lines.splice(i, 1, before, ' '.repeat(p.indent) + marker + ' ' + after);
        var touched = {}; touched[i + 1] = 'keep';
        renumber(lines, touched);
        starts = offsets(lines);
        var at = starts[i + 1] + (lines[i + 1].length - after.length);
        return { text: lines.join('\n'), start: at, end: at };
    }

    // Whether any selected line is a list line (outside a code block): Tab is then the toolbar's, not the page's.
    function onList(state) {
        var sp = span(state.text, state.start, state.end), fence = fenced(sp.lines);
        for (var i = sp.first; i <= sp.last; i++) if (!fence[i] && parse(sp.lines[i])) return true;
        return false;
    }

    var transforms = {
        bold: function (s) { return wrap(s, '**'); },
        italic: function (s) { return wrap(s, '*'); },
        link: link,
        bullets: function (s) { return toggleList(s, false); },
        numbers: function (s) { return toggleList(s, true); },
        indent: function (s) { return indent(s, false); },
        outdent: function (s) { return indent(s, true); },
        enter: enter,
        onList: onList
    };

    if (typeof module !== 'undefined' && module.exports) { module.exports = transforms; return; }

    // ----- In the page -----

    function toolbarFor(box) {
        if (!box || !box.id || box.tagName !== 'TEXTAREA') return null;
        var bar = document.querySelector('.tide-md-toolbar[data-md-for="' + CSS.escape(box.id) + '"]');
        return bar && !box.readOnly && !box.disabled ? bar : null;
    }

    // Puts the new text in, changing only the part that differs, as typing would (so Ctrl+Z takes it back).
    function apply(box, bar, r) {
        if (!r) return false;
        var old = box.value, neu = r.text;
        if (box.maxLength > 0 && neu.length > box.maxLength && neu.length > old.length) return false;   // it wouldn't fit
        if (neu !== old) {
            var p = 0, s = 0;
            while (p < old.length && p < neu.length && old[p] === neu[p]) p++;
            while (s < old.length - p && s < neu.length - p && old[old.length - 1 - s] === neu[neu.length - 1 - s]) s++;
            var ins = neu.slice(p, neu.length - s), fired = false;
            var heard = function () { fired = true; };
            box.focus();
            box.setSelectionRange(p, old.length - s);
            box.addEventListener('input', heard);
            try { if (ins) document.execCommand('insertText', false, ins); else document.execCommand('delete', false); }
            catch (x) { /* no execCommand: set the value below */ }
            box.removeEventListener('input', heard);
            if (box.value !== neu) { box.value = neu; fired = false; }
            if (!fired) box.dispatchEvent(new Event('input', { bubbles: true }));
            if (bar.hasAttribute('data-md-change')) box.dispatchEvent(new Event('change', { bubbles: true }));
        }
        box.focus();
        box.setSelectionRange(r.start === undefined ? neu.length : r.start, r.end === undefined ? neu.length : r.end);
        return true;
    }

    function run(box, bar, action) {
        var f = transforms[action];
        if (!f) return false;
        return apply(box, bar, f({ text: box.value, start: box.selectionStart, end: box.selectionEnd }));
    }

    // Escape, then Tab, leaves the box even on a list line.
    var released = null;

    document.addEventListener('keydown', function (e) {
        var box = e.target, bar = toolbarFor(box);
        if (!bar || e.isComposing || e.altKey) return;
        var key = e.key, mod = e.ctrlKey || e.metaKey;
        if (key === 'Escape') { released = box; return; }
        // Shift (for Shift+Tab) pressed on its own isn't the next key: after Escape, Shift+Tab still leaves.
        if (key === 'Shift' || key === 'Control' || key === 'Meta' || key === 'Alt') return;
        var free = released === box;
        released = null;
        var state = function () { return { text: box.value, start: box.selectionStart, end: box.selectionEnd }; };
        if (mod && !e.shiftKey && (key === 'b' || key === 'B' || key === 'i' || key === 'I')) {
            e.preventDefault();
            run(box, bar, key.toLowerCase() === 'b' ? 'bold' : 'italic');
            return;
        }
        if (mod) return;
        if (key === 'Tab' && !free && transforms.onList(state())) {
            // A list line: Tab nests it. When it can't move (the list's first item, or already at the margin), it's
            // still the box's key, so focus doesn't jump away mid-list; Escape first leaves.
            e.preventDefault();
            run(box, bar, e.shiftKey ? 'outdent' : 'indent');
            return;
        }
        if (key === 'Enter' && !e.shiftKey) {
            var r = transforms.enter(state());
            if (r && apply(box, bar, r)) e.preventDefault();
        }
    }, true);

    // Escape, then leaving the box some other way (a click elsewhere): back in it later, Tab on a list line nests again.
    document.addEventListener('focusout', function (e) {
        if (e.target === released) released = null;
    });

    // A button: mousedown keeps the selection in the box; click (or Enter or Space on it) runs it.
    document.addEventListener('mousedown', function (e) {
        if (e.target.closest && e.target.closest('.tide-md-toolbar button')) e.preventDefault();
    });
    document.addEventListener('click', function (e) {
        var btn = e.target.closest && e.target.closest('.tide-md-toolbar button[data-md]');
        if (!btn) return;
        var bar = btn.closest('.tide-md-toolbar'), box = document.getElementById(bar.getAttribute('data-md-for'));
        if (!box || box.readOnly || box.disabled) return;
        rove(bar, btn);
        run(box, bar, btn.getAttribute('data-md'));
    });

    // The toolbar is one Tab stop; arrow keys, Home and End move between its buttons.
    function rove(bar, to) {
        bar.querySelectorAll('button').forEach(function (b) { b.tabIndex = b === to ? 0 : -1; });
    }
    document.addEventListener('keydown', function (e) {
        var btn = e.target.closest && e.target.closest('.tide-md-toolbar button');
        if (!btn || e.ctrlKey || e.metaKey || e.altKey) return;
        var bar = btn.closest('.tide-md-toolbar'), all = Array.prototype.slice.call(bar.querySelectorAll('button:not([disabled])'));
        var i = all.indexOf(btn), to = null;
        if (e.key === 'ArrowRight' || e.key === 'ArrowDown') to = all[(i + 1) % all.length];
        else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') to = all[(i - 1 + all.length) % all.length];
        else if (e.key === 'Home') to = all[0];
        else if (e.key === 'End') to = all[all.length - 1];
        if (!to) return;
        e.preventDefault();
        rove(bar, to);
        to.focus();
    });

    root.tideMarkdown = { transforms: transforms };
})(typeof window !== 'undefined' ? window : globalThis);
