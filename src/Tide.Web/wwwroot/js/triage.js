// Triage inbox: keyboard actions on the selected item. CSP-safe (an external file, no inline handlers). The selection,
// its keys (j/k, the arrows, Enter on a row) and focus following it are the shared row selection's (rows.js); this adds
// what is Triage's own. Keys go to .NET, which owns the selection; typing in a field (search, a dismiss reason) is left
// alone. "o", or Enter on a row, opens the item's source (the link its icon by the title has) in a new tab here, without
// a referrer or opener. "d" marks it a duplicate of the similar article. A click on the title itself is .NET's: it opens
// the promote form.
// While a row's promote form is open the keys are the form's: only Esc, which closes it and focuses the row again.
// A suggested group ("Looks like one event") is a line of its own: Enter on it opens or closes it, the right arrow opens
// it, and the left arrow (on it or on a report under it) closes it. J/K and the up and down arrows move over it.
// "v" reads the selected item in the reading pane (EP-76), and pressed again closes it; Esc closes it too. Open or closed
// is remembered for the person. While the pane is shown, the selected item's buttons are in the decision bar at its
// foot, one Tab from the selected row (hop, below).
window.tideTriage = (function () {
    const rows = window.tideRows;
    let started = 0, room = null, tellRoom = null;

    // While the promote form is open, only Esc is the page's.
    function held(e) {
        if (!document.querySelector('.tide-promote-form')) return false;
        if (e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            rows.send('cancel');
        }
        return true;
    }

    // Keys that switch something one way and back (V the pane, Enter a group) or open something (Enter or O the source,
    // in a new tab): held down, the keyboard's repeats are left alone, so one press is one switch or one tab.
    const once = ['read', 'toggle-reading', 'toggle', 'open'];

    // What a key means here, given what the shared keys made of it.
    function actionFor(e, action, onRow) {
        const meant = meaning(e, action, onRow);
        return e.repeat && once.indexOf(meant) >= 0 ? null : meant;
    }

    function meaning(e, action, onRow) {
        // Esc closes the reading pane (when no form is open), and V reads or closes it, only when it's shown: one open by
        // itself is hidden on a narrow window, where Esc closes nothing and V opens it.
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
                keys: { s: 'shortlist', x: 'dismiss', d: 'duplicate', p: 'promote', r: 'restore', o: 'open', v: 'read' },
                follow: ['toggle', 'expand', 'collapse', 'read', 'toggle-reading', 'close-reading', 'cancel'],
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
