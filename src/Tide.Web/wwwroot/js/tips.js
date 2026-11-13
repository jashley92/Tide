// A page's tip, or a step of the tour or a guide (GuideStepDialog): set beside the control it's about, which is marked,
// or in the bottom corner when it has none, that control isn't shown or there's no room beside it. Never over the
// context rail or the presenter's rail, docked or open over the page: it moves left of it. A tip that needs its control
// (needsTarget) isn't shown without it. A tip isn't modal: this never takes a key from it, and moves focus only to give
// it back to the page's heading when Got it took it away. A step is a dialog (options.dialog): focus that lands outside
// it (a new page focuses its heading as it opens) goes back to its main button. A step's control may be drawn after the
// step (a page still loading, a guide started from Help): it's looked for again as the page changes, and scrolled into
// view once found. A selector can name fallbacks, "a || b": the first that's shown (the presenter's rail, else its Rail
// button). CSP-safe (an external file).
window.tideTips = (function () {
    var card = null, target = null, looking = null, opener = null, size = null, frame = 0, needsTarget = false, dialog = false, inside = false;
    var GAP = 10, EDGE = 8;
    var root = document.documentElement;

    function shown(el) { return !!el && el.isConnected && el.getClientRects().length > 0 && !el.closest('[inert], [hidden]'); }

    // The first of the selector's choices that's shown, else the first that's there at all.
    function pick(selector) {
        var found = null;
        selector.split('||').some(function (one) {
            var all = document.querySelectorAll(one.trim());
            for (var i = 0; i < all.length; i++) if (shown(all[i])) { found = all[i]; return true; }
            if (!found && all.length) found = all[0];
            return false;
        });
        return found;
    }

    // A step's control, again, when it isn't shown: drawn since, or drawn anew. Ringed and scrolled into view once found.
    function find() {
        if (!looking || shown(target)) return;
        var now = pick(looking);
        if (now === target) return;
        if (target) target.classList.remove('tide-tip-target');
        target = now;
        if (!shown(target)) return;
        // Its scroll margin keeps it clear of the top bar; one taller than most of the window shows from its top. (Read
        // before the ring is written, so this costs no extra layout.)
        var tall = target.getBoundingClientRect().height > innerHeight * 0.6;
        target.classList.add('tide-tip-target');
        target.scrollIntoView({ block: tall ? 'start' : 'nearest', inline: 'nearest' });
    }

    // Where the window is free of the sticky top bar (the app's, or present mode's): a card never goes above it.
    function clear() {
        var bar = document.querySelector('.top-row, .tide-present-top');
        return (shown(bar) ? Math.max(0, bar.getBoundingClientRect().bottom) : 0) + EDGE;
    }

    // The rail it keeps off: the context rail, or present mode's presenter's rail.
    function railBox() {
        var rail = document.getElementById('tide-rail') || document.getElementById('present-rail');
        return shown(rail) ? rail.getBoundingClientRect() : null;
    }

    // The page's width left of any scrollbar (the body scrolls, not <html>), so a card at the right keeps its whole
    // border in view.
    function width() { return Math.min(root.clientWidth, document.body.clientWidth); }

    // Below the control, else above it, never over the sticky top bar; across, centred on it but inside the page. In the
    // bottom corner when it has no control or neither fits. Then left of the rail, if it would be over it. Everything is read before anything is
    // written, so a move costs one layout.
    function place() {
        frame = 0;
        if (!card) return;
        find();
        var hasTarget = shown(target);
        if (needsTarget && !hasTarget) { card.hidden = true; return; }
        var r = hasTarget ? target.getBoundingClientRect() : null, w = width(), rail = railBox(), high = clear();
        var below = r ? Math.max(high, r.bottom + GAP) : 0;
        var top = !r ? null : below + size.height <= innerHeight - EDGE ? below
            : r.top - GAP - size.height >= high ? r.top - GAP - size.height : null;
        var left = top === null ? w - size.width - EDGE
            : Math.min(Math.max(EDGE, r.left + r.width / 2 - size.width / 2), w - size.width - EDGE);
        if (top === null) top = Math.max(high, innerHeight - size.height - EDGE);
        if (rail && left + size.width > rail.left && left < rail.right && top < rail.bottom && top + size.height > rail.top)
            left = Math.max(EDGE, rail.left - GAP - size.width);
        card.hidden = false;
        card.style.top = top + 'px';
        card.style.left = left + 'px';
        card.classList.add('is-placed');
    }

    // At most once a frame, however many scrolls, resizes or rail changes come in it.
    function soon() { if (!frame) frame = requestAnimationFrame(place); }

    // The rail opening or closing (theme.js marks it on <html>) moves the card off it.
    var railWatch = new MutationObserver(soon);

    // A step's control not found yet, or gone: the page changing may draw it. Read without a layout (find, in the next
    // frame, checks it's shown).
    var pageWatch = new MutationObserver(function () {
        if (!target || !target.isConnected || !target.classList.contains('tide-tip-target')) soon();
    });

    // Focus on the page's heading (present mode's is outside <main>).
    function toPage() {
        var to = document.querySelector('main h1') || document.querySelector('h1');
        if (!to) return;
        if (!to.hasAttribute('tabindex')) to.setAttribute('tabindex', '-1');
        to.focus();
    }

    // Whether focus is in the card: a tip's Got it taking it away gives it to the page's heading, not the page; a step
    // keeps it.
    document.addEventListener('focusin', function (e) {
        inside = !!card && card.contains(e.target);
        if (!dialog || !card || !card.isConnected || inside) return;
        var main = card.querySelector('.btn-primary');
        if (main) setTimeout(function () { if (dialog && card && card.isConnected && !card.contains(document.activeElement)) main.focus(); }, 0);
    });

    return {
        // options.needsTarget: show it only beside its control. options.dialog: a step, which keeps focus.
        show: function (el, selector, options) {
            this.hide();
            card = el;
            needsTarget = !!(options && options.needsTarget);
            dialog = !!(options && options.dialog);
            // A tour's or guide's first step: what had focus (its Show me around, Help's Start) gets it back at the end.
            if (dialog && options.first) opener = document.activeElement !== document.body ? document.activeElement : null;
            if (dialog && selector) {
                // A step looks for its control (find, from place) now and again as the page changes; a tip shows beside
                // what's there as it opens.
                looking = selector;
                pageWatch.observe(document.body, { childList: true, subtree: true });
            } else {
                target = selector ? pick(selector) : null;
                if (shown(target)) target.classList.add('tide-tip-target');
            }
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
            var lost = inside && !dialog && (!document.activeElement || document.activeElement === document.body);
            card = target = size = looking = null;
            frame = 0;
            inside = dialog = false;
            railWatch.disconnect();
            pageWatch.disconnect();
            removeEventListener('resize', soon, { passive: true });
            removeEventListener('scroll', soon, { capture: true, passive: true });
            if (lost) toPage();
        },
        // A tour or guide has ended: focus back on what started it when that's still on the page, else the page's
        // heading. A tip the new page shows stays up.
        end: function () {
            var to = opener;
            opener = null;
            if (shown(to)) to.focus();
            else toPage();
        }
    };
})();
