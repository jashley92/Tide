// Writing an outreach template (OutreachTemplatesPage, OutreachInsertMenu.razor). CSP-safe: an external file, wired by
// event delegation, so Blazor needs no interop call. In a form marked [data-oi] (its placeholders in data-oi-names, as
// JSON [{ n: name, d: what it becomes, s: whether a section can be about it }]), the subject and body ([data-oi-target]) take placeholders two ways:
//   - The Insert menu's buttons: [data-oi-insert] puts its text where the cursor was in the box last used (the body
//     when none was); [data-oi-section] wraps what was selected in that placeholder's section, or starts one round it.
//   - Typing "{{" (or "{{#", "{{/") opens a list of the names that match what follows, under the box ([data-oi-list-for]).
//     Up and Down move through it, Enter or Tab writes the name and closes the braces, Escape closes the list.
// Edits go through execCommand('insertText') where the browser has it, so Ctrl+Z undoes them, and fire an input event
// and a change event so Blazor's binding hears them. Typing fires a change too, a moment after the last key, so the page
// hears the draft (and checks it) when typing pauses rather than on every key or only on leaving the box. The text work
// is small pure functions on { text, start, end }, testable apart.
(function (root) {
    'use strict';

    // --- the text ---------------------------------------------------------------------------------------------------

    // What's typed after the last "{{" before the cursor on its line, when it could still become a placeholder's name.
    var OPEN = /\{\{([#/]?)([A-Za-z0-9.:+\-]{0,40})$/;

    function suggest(state) {
        if (state.start !== state.end) return null;
        var before = state.text.slice(Math.max(0, state.start - 48), state.start);
        var lineStart = before.lastIndexOf('\n');
        if (lineStart >= 0) before = before.slice(lineStart + 1);
        var m = OPEN.exec(before);
        if (!m) return null;
        return { from: state.start - m[0].length, sign: m[1], query: m[2] };
    }

    // The names that start with what's typed, then those with a part that does ("cves" finds article.cves), at most 8.
    function matches(names, query) {
        var q = (query || '').toLowerCase(), first = [], then = [];
        names.forEach(function (p) {
            var n = p.n.toLowerCase();
            if (n.indexOf(q) === 0) first.push(p);
            else if (q && n.split(/[.:\-]/).some(function (part) { return part.indexOf(q) === 0; })) then.push(p);
        });
        return first.concat(then).slice(0, 8);
    }

    // The name written in, braces closed (or the "}}" already there kept), the cursor after them.
    function complete(state, found, name) {
        var t = state.text, end = state.end;
        var closing = t.slice(end, end + 2) === '}}' ? 2 : 0;
        var piece = '{{' + found.sign + name + '}}';
        return { from: found.from, to: end + closing, insert: piece, caret: found.from + piece.length };
    }

    // Text put in place of the selection, the cursor after it.
    function insert(state, piece) {
        return { from: state.start, to: state.end, insert: piece, caret: state.start + piece.length };
    }

    // A section round the selection, or round the placeholder itself when nothing is selected. In the body its tags go on
    // lines of their own, with an {{else}} line to fill or delete; in the subject, inline.
    function section(state, name, inline) {
        var t = state.text, s = state.start, e = state.end;
        var inner = t.slice(s, e), trailing = '';
        if (inner.slice(-1) === '\n') { inner = inner.slice(0, -1); trailing = '\n'; }
        if (!inner.trim()) inner = '{{' + name + '}}';
        var open = '{{#' + name + '}}', close = '{{/' + name + '}}';
        if (inline) {
            var piece = open + inner + close;
            return { from: s, to: e, insert: piece, caret: s + piece.length };
        }
        var lead = s > 0 && t[s - 1] !== '\n' ? '\n' : '';
        var after = trailing || (e < t.length && t[e] !== '\n' ? '\n' : '');
        var head = lead + open + '\n' + inner + '\n{{else}}\n';
        var block = head + close + after;
        // The cursor on the empty line after {{else}} would put text there: it goes after the placeholder instead.
        return { from: s, to: e, insert: block, caret: s + lead.length + open.length + 1 + inner.length };
    }

    var transforms = { suggest: suggest, matches: matches, complete: complete, insert: insert, section: section };

    if (typeof module !== 'undefined' && module.exports) { module.exports = transforms; return; }

    // --- the page ---------------------------------------------------------------------------------------------------

    var doc = root.document;

    function scope(el) { return el && el.closest ? el.closest('[data-oi]') : null; }
    function isTarget(el) { return el && el.matches && el.matches('[data-oi-target]'); }
    function inline(el) { return el.tagName !== 'TEXTAREA'; }

    function namesOf(form) {
        if (!form.__oiNames) {
            try { form.__oiNames = JSON.parse(form.getAttribute('data-oi-names') || '[]'); }
            catch (_) { form.__oiNames = []; }
        }
        return form.__oiNames;
    }

    // The box a menu button writes into: the one last used in its form, else the body.
    function targetFor(form) {
        var last = form.__oiLast;
        if (last && last.isConnected) return last;
        return form.querySelector('[data-oi-target][data-oi-default]') || form.querySelector('[data-oi-target]');
    }

    // Applies an edit, undoably where the browser lets it, and tells Blazor.
    function apply(el, edit) {
        el.focus();
        el.setSelectionRange(edit.from, edit.to);
        var done = false;
        try { done = doc.execCommand && doc.execCommand('insertText', false, edit.insert); } catch (_) { done = false; }
        if (!done || el.value.slice(edit.from, edit.from + edit.insert.length) !== edit.insert) {
            el.setRangeText(edit.insert, edit.from, edit.to, 'end');
            el.dispatchEvent(new Event('input', { bubbles: true }));
        }
        el.setSelectionRange(edit.caret, edit.caret);
        el.dispatchEvent(new Event('change', { bubbles: true }));
        remember(el);
    }

    function remember(el) {
        el.__oiSel = { start: el.selectionStart, end: el.selectionEnd };
        var form = scope(el);
        if (form) form.__oiLast = el;
    }

    function stateOf(el, saved) {
        var sel = saved || { start: el.selectionStart, end: el.selectionEnd };
        var max = el.value.length;
        return { text: el.value, start: Math.min(sel.start, max), end: Math.min(sel.end, max) };
    }

    // The menu's buttons. Capture, so the insert happens before Blazor closes the menu.
    doc.addEventListener('click', function (e) {
        var btn = e.target.closest && e.target.closest('[data-oi-insert], [data-oi-section]');
        if (!btn || btn.disabled) return;
        var form = scope(btn);
        var el = form && targetFor(form);
        if (!el) return;
        var state = stateOf(el, el.__oiSel);
        var edit = btn.hasAttribute('data-oi-section')
            ? section(state, btn.getAttribute('data-oi-section'), inline(el))
            : insert(state, btn.getAttribute('data-oi-insert'));
        apply(el, edit);
    }, true);

    // The check's "Show where": the trouble selected in its box. The page heard the text when typing last paused, so if
    // it has moved since, the nearest place it's written is taken.
    doc.addEventListener('click', function (e) {
        var btn = e.target.closest && e.target.closest('[data-oi-select]');
        if (!btn) return;
        var el = doc.getElementById(btn.getAttribute('data-oi-select'));
        if (!el) return;
        var text = btn.getAttribute('data-oi-text') || '';
        var from = parseInt(btn.getAttribute('data-oi-from'), 10) || 0;
        if (text && el.value.substr(from, text.length) !== text) {
            var before = el.value.lastIndexOf(text, from), after = el.value.indexOf(text, from);
            from = before < 0 ? after : after < 0 ? before : (from - before <= after - from ? before : after);
            if (from < 0) from = 0;
        }
        el.focus();
        el.setSelectionRange(from, Math.min(el.value.length, from + text.length));
        el.scrollIntoView({ block: 'nearest' });
        remember(el);
    });

    ['focusin', 'keyup', 'mouseup', 'select'].forEach(function (type) {
        doc.addEventListener(type, function (e) { if (isTarget(e.target)) remember(e.target); }, true);
    });

    // --- the list under the box -------------------------------------------------------------------------------------

    function listFor(el) { return el.id ? doc.querySelector('[data-oi-list-for="' + el.id + '"]') : null; }
    function statusFor(el) { var form = scope(el); return form ? form.querySelector('[data-oi-status]') : null; }

    function close(el) {
        if (!el.__oiFound) return;
        var list = listFor(el);
        if (list && !list.hidden) {
            list.hidden = true;
            list.textContent = '';
        }
        el.__oiFound = null;
        el.removeAttribute('aria-activedescendant');
        var status = statusFor(el);
        if (status) status.textContent = '';
    }

    function highlight(el, i) {
        var list = listFor(el);
        if (!list) return;
        var opts = list.querySelectorAll('[role="option"]');
        if (!opts.length) return;
        i = (i + opts.length) % opts.length;
        opts.forEach(function (o, k) { o.setAttribute('aria-selected', k === i ? 'true' : 'false'); });
        el.__oiActive = i;
        el.setAttribute('aria-activedescendant', opts[i].id);
        if (root.tideCombo) root.tideCombo.reveal(opts[i].id);
    }

    function open(el) {
        var form = scope(el), list = listFor(el);
        if (!form || !list) return;
        var found = suggest(stateOf(el));
        // A section's tag ("{{#", "{{/") takes only a name a section can be about.
        var names = found && found.sign ? namesOf(form).filter(function (p) { return p.s; }) : namesOf(form);
        var shown = found ? matches(names, found.query) : [];
        if (!found || !shown.length) { close(el); return; }
        el.__oiFound = found;
        list.textContent = '';
        shown.forEach(function (p, i) {
            var li = doc.createElement('li');
            li.id = el.id + '-opt-' + i;
            li.setAttribute('role', 'option');
            li.setAttribute('aria-selected', 'false');
            li.setAttribute('data-oi-name', p.n);
            var name = doc.createElement('span');
            name.className = 'tide-data';
            name.textContent = '{{' + found.sign + p.n + '}}';
            var what = doc.createElement('span');
            what.className = 'tide-oi-what';
            what.textContent = p.d;
            li.appendChild(name);
            li.appendChild(what);
            list.appendChild(li);
        });
        list.hidden = false;
        highlight(el, 0);
        var status = statusFor(el);
        if (status) status.textContent = shown.length === 1 ? '1 placeholder matches. Enter to insert it.'
            : shown.length + ' placeholders match. Up and Down to choose, Enter to insert.';
    }

    function accept(el, name) {
        var found = el.__oiFound;
        if (!found) return;
        close(el);
        apply(el, complete(stateOf(el), found, name));
    }

    // What's typed is offered as it's typed, and heard by the page when typing pauses.
    var PAUSE_MS = 500;
    doc.addEventListener('input', function (e) {
        var el = e.target;
        if (!isTarget(el)) return;
        open(el);
        clearTimeout(el.__oiPause);
        el.__oiPause = setTimeout(function () { el.dispatchEvent(new Event('change', { bubbles: true })); }, PAUSE_MS);
    });

    doc.addEventListener('keydown', function (e) {
        var el = e.target;
        if (!isTarget(el) || !el.__oiFound) return;
        var list = listFor(el);
        if (!list || list.hidden) return;
        var opts = list.querySelectorAll('[role="option"]');
        var key = e.key;
        if (key === 'ArrowDown' || key === 'ArrowUp') highlight(el, (el.__oiActive || 0) + (key === 'ArrowDown' ? 1 : -1));
        else if ((key === 'Enter' || key === 'Tab') && !e.ctrlKey && !e.metaKey && !e.shiftKey && !e.altKey)
            accept(el, opts[el.__oiActive || 0].getAttribute('data-oi-name'));
        else if (key === 'Escape') close(el);
        else return;
        // Handled here: not a new line, a move of focus, a save or the form's Escape.
        e.preventDefault();
        e.stopImmediatePropagation();
    }, true);

    // A click on an option writes it; the press doesn't take focus from the box.
    doc.addEventListener('mousedown', function (e) {
        if (e.target.closest && e.target.closest('[data-oi-list-for] [role="option"]')) e.preventDefault();
    }, true);
    doc.addEventListener('click', function (e) {
        var opt = e.target.closest && e.target.closest('[data-oi-list-for] [role="option"]');
        if (!opt) return;
        var el = doc.getElementById(opt.closest('[data-oi-list-for]').getAttribute('data-oi-list-for'));
        if (el) accept(el, opt.getAttribute('data-oi-name'));
    });

    doc.addEventListener('focusout', function (e) { if (isTarget(e.target)) close(e.target); });

})(typeof window !== 'undefined' ? window : this);
