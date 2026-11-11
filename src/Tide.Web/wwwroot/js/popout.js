// A popout that closes when it's done with (Popout in .NET: a row's Attach to…, the account menu, Export indicators,
// every menu button and the filter bar's + Filter).
// CSP-safe (an external file). While one is open, .NET watches it (watch: the element holding it, and its button) and
// it closes on:
//   Escape anywhere on the page, with focus in it or not. That Escape is the popout's alone: it's heard first (on the
//     window, as it's captured) and goes no further, so the page under it (Triage's reading pane, the context rail, a
//     list's keys) doesn't hear it as well. Focus goes back to its button. The next Escape is the page's again.
//   a press (mouse, pen or touch) outside it, or focus moving outside it (Tab past its end): focus stays where it went.
//     A press inside it, on a part that takes no focus, leaves it open.
// Each closes it once: the watch ends here, then .NET's Dismiss closes it. .NET ends a watch itself (unwatch) when the
// popout closes another way, or the component goes.
// As it opens, its panel is kept inside the page: one aligned to its button that would run past the window's edge, or
// over the sidebar beside the page, is moved along until it doesn't (a button near the left of a narrow window).
window.tidePopout = (function () {
    let watched = [], last = 0;
    const margin = 8;

    // The holder's panel: its child placed against it (the backdrop, where there is one, covers the window instead).
    function fit(el) {
        const panel = Array.prototype.find.call(el.children, function (c) { return getComputedStyle(c).position === 'absolute'; });
        if (!panel) return;
        const r = panel.getBoundingClientRect();
        const main = document.querySelector('main');
        const from = Math.max(0, main ? main.getBoundingClientRect().left : 0) + margin;
        const to = document.documentElement.clientWidth - margin;
        // Past the right edge it moves left, but never past the left one: a panel wider than the room starts at its left.
        const by = r.right > to ? Math.max(to - r.right, from - r.left) : Math.max(0, from - r.left);
        if (by) panel.style.translate = by + 'px 0';
    }

    function listen(on) {
        const method = on ? 'addEventListener' : 'removeEventListener';
        window[method]('keydown', onKey, true);
        document[method]('pointerdown', onOutside, true);
        document[method]('focusin', onOutside, true);
    }

    function end(w) {
        watched = watched.filter(function (x) { return x !== w; });
        if (watched.length === 0) listen(false);
        if (w.focusable) w.el.removeAttribute('tabindex');
    }

    function dismiss(w) {
        end(w);
        w.ref.invokeMethodAsync('Dismiss').catch(function () { /* the component or the tab has gone */ });
    }

    // The last one opened takes the Escape.
    function onKey(e) {
        if (e.key !== 'Escape' || e.isComposing || watched.length === 0) return;
        e.preventDefault();
        e.stopImmediatePropagation();
        const w = watched[watched.length - 1];
        dismiss(w);
        if (w.opener.isConnected) w.opener.focus();
    }

    function onOutside(e) {
        watched.filter(function (w) { return !w.el.contains(e.target); }).forEach(dismiss);
    }

    return {
        watch: function (el, opener, ref) {
            if (watched.length === 0) listen(true);
            // A press on a part of it that takes no focus (its background, a label) moves focus to the nearest focusable
            // element holding it: the holder itself takes it (out of the Tab order), so that press isn't leaving it.
            const focusable = !el.hasAttribute('tabindex');
            if (focusable) el.setAttribute('tabindex', '-1');
            fit(el);
            watched.push({ id: ++last, el: el, opener: opener, ref: ref, focusable: focusable });
            return last;
        },
        unwatch: function (id) {
            const w = watched.find(function (x) { return x.id === id; });
            if (w) end(w);
        }
    };
})();
