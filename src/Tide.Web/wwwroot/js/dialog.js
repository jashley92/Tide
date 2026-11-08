// Dialogs drawn by Blazor (the screenshot lightbox, confirmations, the ATT&CK matrix picker): remembers what had focus when one opened, and gives it focus back
// when it closes. CSP-safe (an external file).
window.tideDialog = (function () {
    let opener = null;

    // The controls Tab can reach in a dialog, in order. One inside an inert part (a folded ATT&CK column) can't take
    // focus, so it's left out: the Tab cycle's first and last are always controls the browser will actually focus.
    function focusables(container) {
        return Array.prototype.filter.call(
            container.querySelectorAll('button:not([disabled]), a[href], input:not([disabled]), select, textarea, [tabindex="0"]'),
            function (el) { return !el.closest('[inert]'); });
    }

    // Inside the lightbox, only the keys it handles lose their default (Tab stays on its one button; Enter, Space and
    // Escape close it), so the browser's own shortcuts (zoom, find, copy) still work over a screenshot.
    document.addEventListener('keydown', function (e) {
        if (!e.target || !e.target.closest) return;
        // A confirmation (ConfirmDialog) or the ATT&CK matrix picker: Tab cycles through its controls and doesn't leave it.
        var modal = e.target.closest('.tide-modal, .tide-atkm-panel');
        // Focus outside an open dialog (it was on the page, or dropped to the body): Tab goes into the dialog, not behind it.
        var open = !modal && e.key === 'Tab' ? document.querySelector('.tide-modal') : null;
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
            opener = document.activeElement;
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
        close: function () {
            if (opener && opener.isConnected && opener.focus) opener.focus();
            opener = null;
        }
    };
})();
