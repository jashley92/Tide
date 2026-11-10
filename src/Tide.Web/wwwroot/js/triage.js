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
// is remembered for the person.
window.tideTriage = (function () {
    const rows = window.tideRows;
    let started = 0;

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

    // What a key means here, given what the shared keys made of it.
    function actionFor(e, action, onRow) {
        // Esc closes the reading pane (when no form is open), and V reads or closes it, only when it's shown: one open by
        // itself is hidden on a narrow window, where Esc closes nothing and V opens it.
        const pane = document.querySelector('.tide-reading');
        const shown = !!pane && pane.getClientRects().length > 0;
        if (e.key === 'Escape' && shown) return 'close-reading';
        if (action === 'read' && shown) return 'toggle-reading';
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

    return {
        // The promote form opened: its title takes focus, selected, ready to retype.
        focusPromote: function () {
            const title = document.getElementById('promote-title');
            if (title) { title.focus(); title.select(); }
        },
        // The promote form closed: the row gets focus back, unless focus already went somewhere else (a tab, a filter).
        focusRow: function () {
            const active = document.activeElement;
            if (!active || active === document.body || (active.closest && active.closest('.tide-triage'))) rows.follow();
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
            started = rows.start(dotnetRef, {
                row: 'tide-triage-row', method: 'OnTriageKey',
                keys: { s: 'shortlist', x: 'dismiss', d: 'duplicate', p: 'promote', r: 'restore', o: 'open', v: 'read' },
                follow: ['toggle', 'expand', 'collapse', 'read', 'toggle-reading', 'close-reading', 'cancel'],
                held: held, key: actionFor, open: open
            });
        },
        stop: function () {
            rows.stop(started);
        }
    };
})();
