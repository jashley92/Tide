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
window.tidePopout = (function () {
    let watched = [], last = 0;

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
            watched.push({ id: ++last, el: el, opener: opener, ref: ref, focusable: focusable });
            return last;
        },
        unwatch: function (id) {
            const w = watched.find(function (x) { return x.id === id; });
            if (w) end(w);
        }
    };
})();
