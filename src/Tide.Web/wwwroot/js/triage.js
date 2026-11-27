// Triage inbox: keyboard actions on the selected item. CSP-safe (an external file, no inline handlers). The selection,
// its keys (j/k, the arrows, Enter on a row) and focus following it are the shared row selection's (rows.js); this adds
// what is Triage's own. Keys go to .NET, which owns the selection; typing in a field (search, a dismiss reason) is left
// alone. "o", or Enter on a row, opens the item's source (the link its icon by the title has) in a new tab here, without
// a referrer or opener. "d" marks it a duplicate of the similar article. A click on the title itself is .NET's: it opens
// the promote form.
// While a row's promote form is open the keys are the form's: Enter promotes and writes (the form's submit), Shift+Enter
// promotes and keeps triaging, and Esc closes it and focuses the row again.
// A suggested group ("Looks like one event") is a line of its own: Enter on it opens or closes it, the right arrow opens
// it, and the left arrow (on it or on a report under it) closes it. J/K and the up and down arrows move over it.
// "v" reads the selected item in the reading pane (EP-76), and pressed again closes it; Esc closes it too. Open or closed
// is remembered for the person. While the pane is shown, the selected item's buttons are in the decision bar at its
// foot, one Tab from the selected row (hop, below).
// Several items can be chosen to decide together (P4): Space on a row chooses it or leaves it out, Shift+J and Shift+K
// (or Shift with the arrows) choose it and the next or previous one, and Esc clears the choice. Space is the page's
// scroll key everywhere else: it's taken only with focus on a row, as rows.js takes it. While items are chosen, the
// selection bar stands where the decision bar was, and S, X and P act on all of them (.NET decides).
window.tideTriage = (function () {
    const rows = window.tideRows;
    let started = 0, room = null, tellRoom = null;

    // While the promote form is open, only Esc is the page's, and Shift+Enter is the form's second way out: Promote and
    // keep triaging, its button pressed (plain Enter submits the form, Promote and write, as it always has). Not on a
    // button, where Shift+Enter presses that button.
    function held(e) {
        const form = document.querySelector('.tide-promote-form');
        if (!form) return false;
        if (e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            rows.send('cancel');
        } else if (e.key === 'Enter' && e.shiftKey && !e.repeat && !e.isComposing && form.contains(e.target) && e.target.tagName !== 'BUTTON') {
            e.preventDefault();
            const stay = form.querySelector('[data-promote-stay]');
            if (stay && !stay.disabled) stay.click();
        }
        return true;
    }

    // Keys that switch something one way and back (V the pane, Enter a group) or open something (Enter or O the source,
    // in a new tab): held down, the keyboard's repeats are left alone, so one press is one switch or one tab.
    const once = ['read', 'toggle-reading', 'toggle', 'open', 'select-toggle'];

    // What a key means here, given what the shared keys made of it.
    function actionFor(e, action, onRow) {
        const meant = meaning(e, action, onRow);
        return e.repeat && once.indexOf(meant) >= 0 ? null : meant;
    }

    function meaning(e, action, onRow) {
        // Esc closes the reading pane (when no form is open), and V reads or closes it, only when it's shown: one open by
        // itself is hidden on a narrow window, where Esc closes nothing and V opens it.
        // Esc clears the items chosen first (the selection bar is drawn while there are any), then closes the pane.
        if (e.key === 'Escape' && document.querySelector('.tide-triage-selbar')) return 'select-clear';
        if (e.key === 'Escape' || action === 'read') {
            const shown = !!shownPane();
            if (e.key === 'Escape' && shown) return 'close-reading';
            if (action === 'read' && shown) return 'toggle-reading';
        }
        const onGroup = onRow && e.target.classList.contains('tide-triage-group');
        if (e.key === 'Enter' && onRow) return onGroup ? 'toggle' : 'open';
        if (e.key === 'ArrowRight' && onGroup) return 'expand';
        if (e.key === 'ArrowLeft' && onRow) return 'collapse';
        return action;
    }

    function open(row) {
        const link = row && row.querySelector('a.tide-triage-link');
        if (link) window.open(link.href, '_blank', 'noopener,noreferrer');
    }

    // The reading pane, when it's shown (one open by itself is hidden on a narrow window); null otherwise.
    function shownPane() {
        const pane = document.querySelector('.tide-reading');
        return pane && pane.getClientRects().length > 0 ? pane : null;
    }

    // Where the pane sits over the list (a narrow window), the row kept in view after V can leave the pane's head above
    // the window, under the top bar: once V has opened or read into it, its head is brought into view below the bar
    // (focus stays on the row, for J and K). At once, not smoothly: a smooth scroll still keeping the row in view would
    // otherwise carry on past it. Beside the list, it's in view already and nothing moves.
    function showPane(actions) {
        if (!actions.some(function (a) { return a === 'read' || a === 'toggle-reading'; })) return;
        const pane = shownPane(), list = pane && pane.parentElement.querySelector(':scope > .tide-card:first-child');
        if (list && pane.getBoundingClientRect().bottom <= list.getBoundingClientRect().top + 1)
            pane.scrollIntoView({ block: 'start', behavior: 'instant' });
    }

    // While the reading pane is shown, the selected item's buttons are in the decision bar at its foot, after every other
    // row's links and the pane in the page's order. Tab goes as it did when the buttons were in the row: through the
    // selected row, then the pane's head tools (Read it here or again, Open the site), then the bar, main button first,
    // and from the bar's last control on to the next row; Shift+Tab goes back the same way. The pane's close button and
    // its text stay where the page's order puts them (after the list), and Esc closes the pane.
    const tabbable = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex="0"]';

    function stops(root) {
        return root ? Array.prototype.filter.call(root.querySelectorAll(tabbable), function (el) { return el.getClientRects().length > 0; }) : [];
    }

    function hop(e) {
        if (e.key !== 'Tab' || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
        const bar = document.querySelector('.tide-reading .tide-triage-decide');
        const row = document.querySelector('.tide-triage-row.selected');
        if (!bar || !row || !bar.getClientRects().length) return;
        const chain = [row].concat(stops(row), stops(document.querySelector('.tide-reading .tide-reading-tools')), stops(bar));
        // The list's first control past the selected row: the next row's, as the page's order had it.
        const after = stops(row.closest('.tide-card')).filter(function (el) {
            return !row.contains(el) && (row.compareDocumentPosition(el) & Node.DOCUMENT_POSITION_FOLLOWING);
        })[0];
        const at = chain.indexOf(e.target);
        let to = null;
        if (at >= 0) to = e.shiftKey ? (at > 0 ? chain[at - 1] : null) : (at < chain.length - 1 ? chain[at + 1] : after);
        else if (e.shiftKey && e.target === after) to = chain[chain.length - 1];
        if (!to) return;
        e.preventDefault();
        to.focus();
    }

    return {
        // Promote and keep triaging: the rows promoted (their item ids, from .NET) reshape themselves, in size and
        // place, into the Promoted tab's count just above the list, which locks on. Each is a copy, moved and scaled by
        // transform (the browser composites it, no layout each frame), its text fading first so the stretch doesn't show;
        // the row itself fades at once. Each takes as long as its way to the tab needs (tideShortcuts.travel). .NET waits
        // for the first to land (the promise; never longer than --tide-dur-travel) before it reloads the list, so the
        // count rolls to its new number because a row hit it, and the rows below close the gap (flip.js); any others fly
        // on by themselves. Without motion, or in a tab nobody is looking at, the rows fade and the count changes at once.
        // The rows' faded state is a data attribute .NET never draws, taken off once the copies are done (or at once
        // without them), so a row that stays on the page can't stay hidden. tabLabel: the Promoted tab's label, from .NET
        // (Triage.Tabs).
        promoted: function (ids, tabLabel) {
            const MORPHS = 8, STAGGER = 40;   // at most so many rows reshape, each this many ms after the one before
            const want = new Set(ids || []), ts = window.tideShortcuts;
            const rows = Array.prototype.filter.call(document.querySelectorAll('.tide-triage-row[data-items]'), function (r) {
                return r.dataset.items.split(' ').some(function (id) { return want.has(id); });
            }).slice(0, MORPHS);
            const tab = Array.prototype.find.call(document.querySelectorAll('.tide-tabbar-item'), function (b) {
                const label = b.querySelector('span:not(.bi)');
                return label && label.textContent.trim() === tabLabel;
            });
            const dest = tab && (tab.querySelector('.tide-tabbar-count') || tab);
            if (!dest || !rows.length) return Promise.resolve();
            let landed = false, first = null;
            const hit = new Promise(function (ok) { first = ok; });
            const land = function () { if (!landed) { landed = true; ts.pulse(tab, 'data-tide-landed'); } first(); };
            const t = dest.getBoundingClientRect();
            const from = rows.map(function (r) { return r.getBoundingClientRect(); });   // every read before any change
            const shown = function () { rows.forEach(function (r) { r.removeAttribute('data-tide-promoting'); }); };
            rows.forEach(function (r) { r.setAttribute('data-tide-promoting', ''); });
            if (!t.width || ts.quiet()) { land(); shown(); return Promise.resolve(); }
            const ease = ts.token('--tide-ease-out');
            const waited = Promise.race([hit, new Promise(function (ok) { setTimeout(ok, ts.ms('--tide-dur-travel')); })]);
            Promise.all(rows.map(function (row, i) {
                const r = from[i];
                if (!r.width || !r.height) return null;
                const g = ts.copy(row, r);   // the row itself, lifted, as it was before it faded
                g.removeAttribute('data-tide-promoting');
                g.classList.add('tide-morph');
                Array.prototype.forEach.call(g.children, function (c) { c.animate([{ opacity: 1 }, { opacity: 0 }], { duration: ts.ms('--tide-dur-fast'), fill: 'forwards' }); });
                const to = 'translate(' + (t.left - r.left) + 'px, ' + (t.top - r.top) + 'px) scale(' + t.width / r.width + ', ' + t.height / r.height + ')';
                return ts.drift(g, [{ transform: 'none', opacity: 1 }, { transform: to, opacity: .15 }],
                    { duration: ts.travel(Math.hypot(t.left - r.left, t.top - r.top)), delay: i * STAGGER, easing: ease }).finished.then(land, function () { });
            })).then(shown, shown);
            return waited;
        },
        // The promote form opened: its title takes focus, selected, ready to retype.
        focusPromote: function () {
            const title = document.getElementById('promote-title');
            if (title) { title.focus(); title.select(); }
        },
        // The promote form closed, or an attach is done: the row gets focus back, unless focus already went somewhere else
        // (a tab, a filter). The decision bar under the reading pane counts as the list's, as the row's buttons did.
        focusRow: function () {
            const active = document.activeElement;
            if (!active || active === document.body || (active.closest && active.closest('.tide-triage, .tide-triage-decide'))) rows.follow();
        },
        // A choice kept in this browser (the inbox order). With no value it reads; null forgets it.
        pref: function (key, value) {
            try {
                if (value === undefined) return localStorage.getItem('tide-triage-' + key);
                if (value === null) localStorage.removeItem('tide-triage-' + key);
                else localStorage.setItem('tide-triage-' + key, value);
            } catch (e) { /* private mode: the defaults stand */ }
            return null;
        },
        start: function (dotnetRef) {
            // Whether the window has room for the pane that opens by itself (app.css shows it from 1400px): .NET reads
            // nothing for a hidden pane, and catches up when it shows.
            room = window.matchMedia('(min-width: 1400px)');
            tellRoom = function () { dotnetRef.invokeMethodAsync('OnReadingRoom', room.matches).catch(function () { }); };
            room.addEventListener('change', tellRoom);
            tellRoom();
            document.addEventListener('keydown', hop);
            started = rows.start(dotnetRef, {
                row: 'tide-triage-row', method: 'OnTriageKey',
                keys: { s: 'shortlist', x: 'dismiss', d: 'duplicate', p: 'promote', r: 'restore', o: 'open', v: 'read', ' ': 'select-toggle' },
                shift: { j: 'select-next', ArrowDown: 'select-next', k: 'select-prev', ArrowUp: 'select-prev' },
                follow: ['toggle', 'expand', 'collapse', 'read', 'toggle-reading', 'close-reading', 'cancel', 'select-next', 'select-prev', 'select-clear'],
                held: held, key: actionFor, open: open, after: showPane
            });
        },
        stop: function () {
            if (room) room.removeEventListener('change', tellRoom);
            room = tellRoom = null;
            document.removeEventListener('keydown', hop);
            rows.stop(started);
        }
    };
})();
