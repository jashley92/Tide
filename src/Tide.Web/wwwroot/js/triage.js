// Triage inbox: keyboard actions on the selected item. CSP-safe (an external file, no inline handlers). Keys go to .NET,
// which owns the selection; typing in a field (search, a dismiss reason) is left alone. "o", or Enter on a row, opens
// the item's source (the link its icon by the title has) in a new tab here, without a referrer or opener. "d" marks it a
// duplicate of the similar article. A click on the
// title itself is .NET's: it opens the promote form.
// The rows are one tab stop (a roving tabindex: the selected row is 0, the rest -1, as .NET renders them). Moving the
// selection (j/k, the arrows) moves focus with it, so the highlight and focus are always the same row.
// While a row's promote form is open the keys are the form's: only Esc, which closes it and focuses the row again.
// A suggested group ("Looks like one event") is a line of its own: Enter on it opens or closes it, the right arrow opens
// it, and the left arrow (on it or on a report under it) closes it. J/K and the up and down arrows move over it.
// "v" reads the selected item in the reading pane (EP-76); Esc closes the pane.
window.tideTriage = (function () {
    let ref = null;
    const typing = window.tideShortcuts.typing, inMenu = window.tideShortcuts.inMenu;

    const keys = {
        j: 'next', ArrowDown: 'next', k: 'prev', ArrowUp: 'prev',
        s: 'shortlist', x: 'dismiss', d: 'duplicate', p: 'promote', r: 'restore', o: 'open', v: 'read'
    };

    // Letters match in either case, so Caps Lock or Shift doesn't stop them.
    function keyOf(e) { return e.key && e.key.length === 1 ? e.key.toLowerCase() : e.key; }

    function reveal() {
        const selected = document.querySelector('.tide-triage-row.selected');
        if (selected) selected.scrollIntoView({ block: 'nearest' });
    }

    // After a move: focus goes to the newly selected row (focusing it scrolls it into view).
    function follow() {
        const selected = document.querySelector('.tide-triage-row.selected');
        if (selected && document.activeElement !== selected) selected.focus();
        reveal();
    }

    // After a decision (dismiss, shortlist, duplicate, restore): the decided row has gone, so focus went with it to the
    // page. It goes to the row now selected, so the focus ring and a screen reader's place carry on down the list. A
    // control that took focus itself (the promote form's title) keeps it.
    function settle() {
        const lost = !document.activeElement || document.activeElement === document.body || !document.activeElement.isConnected;
        if (lost) follow(); else reveal();
    }

    function onKey(e) {
        if (!ref || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
        if (document.querySelector('.tide-modal')) return;   // a confirmation is open: its keys are its own
        if (document.querySelector('.tide-promote-form')) {
            if (e.key !== 'Escape') return;
            e.preventDefault();
            e.stopPropagation();
            ref.invokeMethodAsync('OnTriageKey', 'cancel').then(follow).catch(function () { });
            return;
        }
        if (typing(e.target) || inMenu(e.target)) return;
        // Esc closes the reading pane (when no form is open).
        if (e.key === 'Escape' && document.querySelector('.tide-reading')) {
            e.preventDefault();
            ref.invokeMethodAsync('OnTriageKey', 'close-reading').then(follow).catch(function () { });
            return;
        }
        if (!window.tideShortcuts.on() && window.tideShortcuts.isLetter(e)) return;
        const onRow = e.target.classList && e.target.classList.contains('tide-triage-row');
        const onGroup = onRow && e.target.classList.contains('tide-triage-group');
        let action = keys[keyOf(e)];
        if (e.key === 'Enter' && onRow) action = onGroup ? 'toggle' : 'open';
        else if (e.key === 'ArrowRight' && onGroup) action = 'expand';
        else if (e.key === 'ArrowLeft' && onRow) action = 'collapse';
        if (!action) return;
        e.preventDefault();
        if (action === 'open') {
            const link = document.querySelector('.tide-triage-row.selected a.tide-triage-link');
            if (link) window.open(link.href, '_blank', 'noopener,noreferrer');
            return;
        }
        const moves = action === 'next' || action === 'prev' || action === 'toggle' || action === 'expand' || action === 'collapse' || action === 'read';
        ref.invokeMethodAsync('OnTriageKey', action).then(moves ? follow : settle).catch(function () { });
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
            if (!active || active === document.body || (active.closest && active.closest('.tide-triage'))) follow();
        },
        // Arriving at a row from the address (Back after a promote): it takes focus and scrolls into view, past the
        // heading the router focused.
        focusSelected: function () {
            const selected = document.querySelector('.tide-triage-row.selected');
            if (!selected) return;
            selected.focus({ preventScroll: true });
            selected.scrollIntoView({ block: 'center' });
        },
        // Before "Promote and write" opens the editor: this history entry's address becomes the tab, filters and row
        // (the page's own state is kept), so Back returns to them.
        remember: function (url) {
            history.replaceState(history.state, '', new URL(url, document.baseURI).href);
        },
        // A choice kept in this browser (EP-81: the order, the missed-stories notice hidden for a week). With no value it
        // reads; null forgets it.
        pref: function (key, value) {
            try {
                if (value === undefined) return localStorage.getItem('tide-triage-' + key);
                if (value === null) localStorage.removeItem('tide-triage-' + key);
                else localStorage.setItem('tide-triage-' + key, value);
            } catch (e) { /* private mode: the defaults stand */ }
            return null;
        },
        start: function (dotnetRef) {
            ref = dotnetRef;
            document.addEventListener('keydown', onKey);
        },
        stop: function () {
            document.removeEventListener('keydown', onKey);
            ref = null;
        }
    };
})();
