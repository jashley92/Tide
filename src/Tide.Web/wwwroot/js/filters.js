// The filter bar's "+ Filter" menu from the keyboard (FilterBar), and every menu button's menu (MenuButton, the action page's
// More) the same way. CSP-safe (an external file, no inline handlers).
// .NET opens and closes it; this moves focus. Opening it, or one filter's options, focuses the first choice (the one in
// force, or a text filter's box): focusIn. The arrows and Home/End move between the choices (tideRoving, in theme.js).
// Tab leaves it closed (its backdrop is clicked, which .NET hears), except in a typed filter's form, which Tab moves
// through until focus is past either end. Esc is .NET's: it closes it and focus goes back to + Filter. The down arrow on
// + Filter opens it, as on a menu button.
// One listener on the page for every bar, so there's nothing to start or stop.
window.tideFilters = (function () {
    const choices = '[role="menuitem"]:not([disabled]), [role="menuitemradio"]:not([disabled]), [role="menuitemcheckbox"]:not([disabled])';

    function focusIn(pop) {
        if (!pop || !pop.querySelector) return;
        // Past the way back to the list of filters, which heads each filter's options.
        const all = Array.prototype.slice.call(pop.querySelectorAll(choices));
        const first = all.find(function (c) { return !c.classList.contains('tide-filter-back'); }) || all[0];
        const target = pop.querySelector('input') || pop.querySelector('[aria-checked="true"]') || first;
        if (!target) return;
        // The popout is placed in view already (tidePopout): only a long list inside it scrolls to the choice.
        target.focus({ preventScroll: true });
        target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }

    function onKey(e) {
        if (e.ctrlKey || e.metaKey || e.altKey || !e.target || !e.target.closest) return;
        const trigger = e.target.closest('.tide-filter-add, .tide-menubutton-btn');
        if (trigger) {
            if (e.key === 'ArrowDown' && trigger.getAttribute('aria-expanded') !== 'true') {
                e.preventDefault();
                trigger.click();
            }
            return;
        }
        const pop = e.target.closest('.tide-filter-pop, .tide-menubutton-pop');
        if (!pop) return;
        if (e.key === 'Tab') {
            const backdrop = pop.parentElement.querySelector('.tide-popover-backdrop');
            // A typed filter's form (its box, Remove, Apply) is moved through with Tab, as any form is, and left closed
            // only once focus is past its ends. That's judged after the browser has moved focus, not from which stop
            // had it: a date box's Tab and Shift+Tab step between its month, day and year inside the one input, so
            // Shift+Tab from its day went back to its month but, the box being the form's first stop, closed the
            // popover. A menu's choices aren't tab stops, so there Tab leaves at once.
            const form = e.target.closest('form');
            if (form) {
                setTimeout(function () {
                    if (!form.contains(document.activeElement) && backdrop && backdrop.isConnected !== false) backdrop.click();
                }, 0);
                return;
            }
            if (backdrop) backdrop.click();
            return;
        }
        // A long list's find box: Down goes to the first choice it leaves; otherwise the keys move the caret.
        if (e.target.tagName === 'INPUT') {
            if (e.key === 'ArrowDown' && e.target.classList.contains('tide-filter-find')) {
                const first = Array.prototype.slice.call(pop.querySelectorAll(choices)).find(function (c) { return !c.classList.contains('tide-filter-back'); });
                if (first) { e.preventDefault(); first.focus(); }
            }
            return;
        }
        const to = window.tideRoving(Array.prototype.slice.call(pop.querySelectorAll(choices)), document.activeElement, e, 'y');
        if (!to) return;
        e.preventDefault();
        to.focus();
    }

    document.addEventListener('keydown', onKey);

    return { focusIn: focusIn };
})();
