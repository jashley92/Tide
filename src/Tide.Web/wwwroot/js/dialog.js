// Dialogs drawn by Blazor (the screenshot lightbox, confirmations, the ATT&CK matrix picker): remembers what had focus when one opened, and gives it focus back
// when it closes. CSP-safe (an external file).
window.tideDialog = (function () {
    // Each open dialog and what had focus as it opened, the innermost last: a dialog opened over another (a confirmation
    // over the ATT&CK picker) gives focus back to its own opener inside the outer one, and the outer one later to its own.
    // Blazor takes a closed dialog out of the page, so an entry whose dialog has gone is one that closed. Some close
    // without telling us (the palette running a command, the guide ending, one thrown away while open): their entries
    // are dropped as they're found gone, so a later close never sends focus to what opened them.
    const openers = [];

    function gone(entry) { return !!entry.dialog && !entry.dialog.isConnected; }

    // Drops the entries of dialogs that have closed.
    function forget() {
        for (var i = openers.length - 1; i >= 0; i--) if (gone(openers[i])) openers.splice(i, 1);
    }

    // The controls Tab can reach in a dialog, in order. One inside an inert or hidden part (a folded ATT&CK column, a
    // closed file's details) or not drawn can't take focus, so it's left out: the Tab cycle's first and last are always
    // controls the browser will actually focus.
    function focusables(container) {
        return Array.prototype.filter.call(
            container.querySelectorAll('button:not([disabled]), a[href], input:not([disabled]):not([type="hidden"]), select:not([disabled]), '
                + 'textarea:not([disabled]), summary, [tabindex="0"]'),
            function (el) { return !el.closest('[inert], [hidden]') && el.getClientRects().length > 0; });
    }

    // A modal dialog: a confirmation (ConfirmDialog) or the palette, the ATT&CK matrix picker, or the context rail or
    // present mode's presenter's rail while it's open over the page (each is marked aria-modal then). The lightbox handles its own keys, below.
    var DIALOGS = '.tide-modal, .tide-atkm-panel', RAILS = '.tide-rail[aria-modal="true"], #present-rail[aria-modal="true"]';
    var MODAL = DIALOGS + ', ' + RAILS;

    // The last one drawn that matches: of several open, the one on top.
    function lastOf(sel) {
        var all = document.querySelectorAll(sel);
        return all.length ? all[all.length - 1] : null;
    }

    // Inside the lightbox, only the keys it handles lose their default (Tab stays on its one button; Enter, Space and
    // Escape close it), so the browser's own shortcuts (zoom, find, copy) still work over a screenshot.
    document.addEventListener('keydown', function (e) {
        if (!e.target || !e.target.closest) return;
        // In a modal dialog (the innermost, for one opened inside another), Tab cycles through its controls and doesn't
        // leave it.
        var modal = e.target.closest(MODAL);
        // Focus outside an open dialog (it was on the page, or dropped to the body): Tab goes into the dialog, not behind it.
        // With several open, the one on top: a dialog over a rail open over the page, else the last drawn.
        var open = !modal && e.key === 'Tab' ? lastOf(DIALOGS) || lastOf(RAILS) : null;
        if (open) {
            var into = focusables(open)[0];
            if (into) { e.preventDefault(); into.focus(); }
            return;
        }
        if (modal && e.key === 'Tab') {
            var f = focusables(modal);
            if (f.length === 0) return;
            var first = f[0], last = f[f.length - 1];
            // The dialog itself has focus (its text was clicked: it takes focus then, so Escape still reaches it): Tab
            // and Shift+Tab go to its first and last control, not out behind it.
            if (document.activeElement === modal) { e.preventDefault(); (e.shiftKey ? last : first).focus(); return; }
            if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
            else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
            return;
        }
        if (!e.target.closest('.tide-lightbox')) return;
        if (e.key === 'Tab' || e.key === 'Enter' || e.key === ' ' || e.key === 'Escape') e.preventDefault();
    });
    return {
        open: function (focusTarget) {
            // Opened from inside a popout (the palette over + Filter), which closes as the dialog takes focus: focus goes
            // back to the popout's button, as what had focus in it is gone by then.
            forget();
            openers.push({
                opener: (window.tidePopout && window.tidePopout.opener(document.activeElement)) || document.activeElement,
                dialog: (focusTarget && focusTarget.closest && focusTarget.closest('[role="dialog"], [role="alertdialog"]')) || focusTarget || null,
            });
            if (focusTarget && focusTarget.focus) focusTarget.focus();
        },
        // A dialog with fields to fill (ConfirmDialog's Form): focus goes to its first field, or to the fallback.
        openFirst: function (container, fallback) {
            var field = container && container.querySelector && container.querySelector('input:not([disabled]):not([type=hidden]), select:not([disabled]), textarea:not([disabled])');
            this.open(field || fallback);
        },
        // A dialog whose body acts (the send dialog): focus goes to its first control, so it opens scrolled to the top.
        openFirstControl: function (container, fallback) {
            var control = container && container.querySelector && container.querySelector('button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled])');
            this.open(control || fallback);
        },
        // The ATT&CK picker's columns moved (its filter changed): back to the left edge, smoothly where the CSS allows
        // (scroll-behavior), and focus back to the filter box if it was in a column that just collapsed (made inert).
        toStart: function (grid, filterBox) {
            if (!grid) return;
            if (grid.scrollLeft > 0) grid.scrollTo({ left: 0 });
            var active = document.activeElement;
            if (filterBox && filterBox.focus && (!active || active === document.body || (active.closest && active.closest('[inert]')))) filterBox.focus();
        },
        // A dialog closed: focus goes back to what opened it. The entries on top whose dialogs have gone all closed by
        // now, the one closing among them; the lowest is the one opened first, and its opener is still on the page. With
        // none gone (a dialog left on the page), the top one closed.
        close: function () {
            var entry = null;
            while (openers.length && gone(openers[openers.length - 1])) entry = openers.pop();
            if (!entry) entry = openers.pop();
            var opener = entry && entry.opener;
            if (opener && opener.isConnected && opener.focus) opener.focus();
        },
        // A dialog closed and something else takes focus (the palette ran a command): forget it, leaving focus be.
        forget: forget
    };
})();
