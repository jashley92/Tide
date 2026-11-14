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
// While it's open its panel is placed in the window (fixed), against its button, so no box around it that cuts off
// what overflows it (a list's card) can hide any of it, and it never makes the page wider. It's placed as it opens and
// as it's drawn again (fit), before its first control takes focus (which .NET gives without scrolling), and again as
// the page scrolls or the window changes size; it closes when its button scrolls out of the window. In the room the
// page leaves it (what shows of the window, without its scrollbars, clear of the sidebar):
//   side to side, aligned as the stylesheet aligns it (its left or right edge with its button's), moved along to stay
//     inside, and no wider than the room;
//   top to bottom, below its button if it fits, else above it if it fits, else on the side with more room, as tall as
//     that room and scrolling inside. It never covers its button.
window.tidePopout = (function () {
    let watched = [], last = 0, frame = 0, resized = null;
    const margin = 8;

    // The holder's panel: its child placed against it (the backdrop, where there is one, covers the window instead).
    function panelOf(w) {
        if (!w.panel || !w.panel.isConnected) {
            w.panel = Array.prototype.find.call(w.el.children, function (c) { return getComputedStyle(c).position === 'absolute'; }) || null;
        }
        return w.panel;
    }

    // The foot of the top bar where it stays over the page as it scrolls (a popout opening upward stops under it), or 0;
    // a popout in the top bar itself (the account menu) is its own.
    function topBar(w) {
        const bar = document.querySelector('.top-row');
        if (!bar || bar.contains(w.el)) return 0;
        const at = getComputedStyle(bar).position;
        return at === 'sticky' || at === 'fixed' ? bar.getBoundingClientRect().bottom : 0;
    }

    function fit(w) {
        const panel = panelOf(w);
        if (!panel) return;
        const s = panel.style, scrolled = panel.scrollTop;
        // Measured where the stylesheet puts it, against its button.
        s.position = s.left = s.top = s.right = s.bottom = s.width = s.maxHeight = s.overflowY = '';
        const r = panel.getBoundingClientRect(), b = w.opener.getBoundingClientRect();
        // Side to side, the page's own width: clear of the sidebar, and of the scrollbar's gutter, which the window keeps
        // whether or not there's a scrollbar in it (scrollbar-gutter: stable), so it's not counted in clientWidth.
        const de = document.documentElement, main = document.querySelector('main'), page = main ? main.getBoundingClientRect() : null;
        const minX = Math.max(0, page ? page.left : 0) + margin, maxX = Math.min(de.clientWidth, page ? page.right : Infinity) - margin;
        const minY = Math.max(0, topBar(w)) + margin, maxY = de.clientHeight - margin;
        const width = Math.min(r.width, maxX - minX);
        const endAligned = Math.abs(r.right - b.right) < Math.abs(r.left - b.left);
        const x = Math.max(minX, Math.min(endAligned ? b.right - width : b.left, maxX - width));
        const gap = r.top >= b.bottom ? r.top - b.bottom : 4;
        const below = maxY - (b.bottom + gap), above = b.top - gap - minY;
        const up = r.height > below && (r.height <= above || above > below);
        const height = Math.max(0, Math.min(r.height, up ? above : below));
        const y = up ? b.top - gap - height : b.bottom + gap;
        s.position = 'fixed';
        s.right = s.bottom = 'auto';
        s.width = width + 'px';
        s.left = x + 'px';
        s.top = y + 'px';
        if (height < r.height) {
            s.maxHeight = height + 'px';
            s.overflowY = 'auto';
            panel.scrollTop = scrolled;
        }
        // A transformed box around it holds a fixed panel in place of the window: corrected by where it landed.
        const now = panel.getBoundingClientRect();
        if (Math.abs(now.left - x) > 0.5 || Math.abs(now.top - y) > 0.5) {
            s.left = (2 * x - now.left) + 'px';
            s.top = (2 * y - now.top) + 'px';
        }
    }

    // The page scrolled (anywhere but in a panel) or the window changed size: each follows its button, or closes once
    // its button is out of the window. Once a frame.
    function onMove(e) {
        if (e.type === 'scroll' && watched.some(function (w) { return w.panel && w.panel.contains(e.target); })) return;
        if (frame) return;
        frame = requestAnimationFrame(function () {
            frame = 0;
            const height = document.documentElement.clientHeight;
            watched.slice().forEach(function (w) {
                const b = w.opener.getBoundingClientRect();
                if (b.bottom < 0 || b.top > height) dismiss(w);
                else fit(w);
            });
        });
    }

    function listen(on) {
        const method = on ? 'addEventListener' : 'removeEventListener';
        window[method]('keydown', onKey, true);
        document[method]('pointerdown', onOutside, true);
        document[method]('focusin', onOutside, true);
        document[method]('scroll', onMove, true);
        window[method]('resize', onMove);
        // What shows of the window narrows without a resize when the page grows a scrollbar.
        if (typeof ResizeObserver === 'undefined') return;
        if (on) (resized = resized || new ResizeObserver(onMove)).observe(document.documentElement);
        else if (resized) resized.disconnect();
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
    // A key or press in something that points at an open popout without being part of it (a guide's step,
    // data-keeps-popouts) leaves it open: the guide closes what it opened itself.
    function kept(e) { return !!(e.target && e.target.closest && e.target.closest('[data-keeps-popouts]')); }

    function onKey(e) {
        if (e.key !== 'Escape' || e.isComposing || watched.length === 0 || kept(e)) return;
        e.preventDefault();
        e.stopImmediatePropagation();
        const w = watched[watched.length - 1];
        dismiss(w);
        if (w.opener.isConnected) w.opener.focus();
    }

    function find(id) {
        return watched.find(function (x) { return x.id === id; });
    }

    function onOutside(e) {
        if (kept(e)) return;
        watched.filter(function (w) { return !w.el.contains(e.target); }).forEach(dismiss);
    }

    return {
        watch: function (el, opener, ref) {
            if (watched.length === 0) listen(true);
            // A press on a part of it that takes no focus (its background, a label) moves focus to the nearest focusable
            // element holding it: the holder itself takes it (out of the Tab order), so that press isn't leaving it.
            const focusable = !el.hasAttribute('tabindex');
            if (focusable) el.setAttribute('tabindex', '-1');
            const w = { id: ++last, el: el, opener: opener, ref: ref, focusable: focusable };
            watched.push(w);
            fit(w);
            return last;
        },
        // Drawn again while open (a filter's options, fewer matches): placed again.
        fit: function (id) {
            const w = find(id);
            if (w) fit(w);
        },
        unwatch: function (id) {
            const w = find(id);
            if (w) end(w);
        },
        // The button of the open popout `el` is in, if it's in one: a dialog opened from a popout (which closes as the
        // dialog takes focus) gives focus back to that button.
        opener: function (el) {
            const w = watched.find(function (x) { return x.el.contains(el); });
            return w ? w.opener : null;
        }
    };
})();
