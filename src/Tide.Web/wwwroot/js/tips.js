// A page's tip (OnboardingHost): set beside the control it's about, which is marked, or in the bottom corner when it has
// none, that control isn't shown or there's no room beside it. Never over the context rail, docked or open over the
// page: it moves left of it. A tip that needs its control (needsTarget) isn't shown without it. A tip isn't modal: this
// never takes a key, and moves focus only to give it back to the page's heading when Got it took it away. CSP-safe (an
// external file).
window.tideTips = (function () {
    var card = null, target = null, size = null, frame = 0, needsTarget = false, inside = false;
    var GAP = 10, EDGE = 8;
    var root = document.documentElement;

    function shown(el) { return !!el && el.isConnected && el.getClientRects().length > 0 && !el.closest('[inert], [hidden]'); }

    // The page's width left of any scrollbar (the body scrolls, not <html>), so a card at the right keeps its whole
    // border in view.
    function width() { return Math.min(root.clientWidth, document.body.clientWidth); }

    // Below the control, else above it; across, centred on it but inside the page. In the bottom corner when it has no
    // control or neither fits. Then left of the rail, if it would be over it. Everything is read before anything is
    // written, so a move costs one layout.
    function place() {
        frame = 0;
        if (!card) return;
        var hasTarget = shown(target);
        if (needsTarget && !hasTarget) { card.hidden = true; return; }
        var r = hasTarget ? target.getBoundingClientRect() : null, w = width();
        var rail = document.getElementById('tide-rail'), railBox = shown(rail) ? rail.getBoundingClientRect() : null;
        var top = !r ? null : r.bottom + GAP + size.height <= innerHeight - EDGE ? r.bottom + GAP
            : r.top - GAP - size.height >= EDGE ? r.top - GAP - size.height : null;
        var left = top === null ? w - size.width - EDGE
            : Math.min(Math.max(EDGE, r.left + r.width / 2 - size.width / 2), w - size.width - EDGE);
        if (top === null) top = innerHeight - size.height - EDGE;
        if (railBox && left + size.width > railBox.left && left < railBox.right && top < railBox.bottom && top + size.height > railBox.top)
            left = Math.max(EDGE, railBox.left - GAP - size.width);
        card.hidden = false;
        card.style.top = top + 'px';
        card.style.left = left + 'px';
        card.classList.add('is-placed');
    }

    // At most once a frame, however many scrolls, resizes or rail changes come in it.
    function soon() { if (!frame) frame = requestAnimationFrame(place); }

    // The rail opening or closing (theme.js marks it on <html>) moves the tip off it.
    var railWatch = new MutationObserver(soon);

    // Whether focus is in the tip, so that when Got it takes the tip away the page's heading gets it, not the page.
    document.addEventListener('focusin', function (e) { inside = !!card && card.contains(e.target); });

    function toHeading() {
        var heading = document.querySelector('main h1');
        if (!heading) return;
        if (!heading.hasAttribute('tabindex')) heading.setAttribute('tabindex', '-1');
        heading.focus();
    }

    return {
        // options.needsTarget: show the tip only beside its control.
        show: function (el, selector, options) {
            this.hide();
            card = el;
            needsTarget = !!(options && options.needsTarget);
            target = selector ? document.querySelector(selector) : null;
            if (shown(target)) target.classList.add('tide-tip-target');
            // The card's size doesn't change while it's up: measured once, where it starts.
            var box = card.getBoundingClientRect();
            size = { width: box.width, height: box.height };
            place();
            addEventListener('resize', soon, { passive: true });
            addEventListener('scroll', soon, { capture: true, passive: true });
            railWatch.observe(root, { attributes: true, attributeFilter: ['data-rail', 'data-rail-over'] });
            // Whether it's shown, for the page to announce it.
            return !card.hidden;
        },
        hide: function () {
            if (target) target.classList.remove('tide-tip-target');
            if (frame) cancelAnimationFrame(frame);
            var lost = inside && (!document.activeElement || document.activeElement === document.body);
            card = target = size = null;
            frame = 0;
            inside = false;
            railWatch.disconnect();
            removeEventListener('resize', soon, { passive: true });
            removeEventListener('scroll', soon, { capture: true, passive: true });
            if (lost) toHeading();
        }
    };
})();
