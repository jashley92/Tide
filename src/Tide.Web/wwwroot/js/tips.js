// A page's tip, or a step of the tour or a guide (GuideStepDialog): set next to the control it's about, with a small
// arrow on the side facing it, and the control ringed (a pulsing ring that settles; steady under reduced motion). Below
// the control, else above it, else beside it; in the bottom corner only when its control isn't on the page. Never over
// the sticky top bar, nor over the context rail or the presenter's rail, docked or open over the page: it moves left of
// it, and points at a control in it from there. A tip that needs its control (needsTarget) isn't shown without it. A tip
// isn't modal: this never takes a key from it, and moves focus only to give it back to the page's heading when Got it
// took it away. A step is a dialog (options.dialog): focus that lands outside it (a new page focuses its heading as it
// opens) goes back to its main button. A step's control may be drawn after the step (a page still loading, a guide
// started from Help): it's looked for again as the page changes, and scrolled into view once found. A selector can name
// fallbacks, "a || b": the first that's shown (the presenter's rail, else its Rail button). CSP-safe (an external file).
window.tideTips = (function () {
    var card = null, ring = null, arrow = null, target = null, looking = null, opener = null, size = null, frame = 0;
    var needsTarget = false, dialog = false, inside = false;
    // ARROW_HALF is half the arrow's 14px square (app.css); ARROW keeps it off the card's rounded corners.
    var GAP = 12, EDGE = 8, ARROW = 16, ARROW_HALF = 7, RING = 4;
    var root = document.documentElement;

    function shown(el) { return !!el && el.isConnected && el.getClientRects().length > 0 && !el.closest('[inert], [hidden]'); }

    function clamp(n, lo, hi) { return Math.max(lo, Math.min(n, hi)); }

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

    // A step's control, again, when it isn't shown: drawn since, or drawn anew. Marked and scrolled into view once found.
    function find() {
        if (!looking || shown(target)) return;
        var now = pick(looking);
        if (now === target) return;
        if (target) { target.classList.remove('tide-tip-target'); sizeWatch.unobserve(target); }
        target = now;
        if (!shown(target)) return;
        // Its scroll margin keeps it clear of the top bar; one taller than most of the window shows from its top. (Read
        // before the mark is written, so this costs no extra layout.)
        var tall = target.getBoundingClientRect().height > innerHeight * 0.6;
        target.classList.add('tide-tip-target');
        sizeWatch.observe(target);
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

    // How far down a card beside a control at `r` starts: level with what shows of it, inside the window.
    function sideTop(r, high) { return clamp(r.top, high, innerHeight - size.height - EDGE); }

    // Where the card goes for a control at `r`: below it, else above it (across, centred on it but inside the page), else
    // beside it (right, then left), from the top of what shows of it. Null when none fits.
    function beside(r, w, high) {
        var h = size.height, cw = size.width, below = Math.max(high, r.bottom + GAP);
        var across = clamp(r.left + r.width / 2 - cw / 2, EDGE, w - cw - EDGE);
        if (below + h <= innerHeight - EDGE) return { side: 'below', top: below, left: across };
        if (r.top - GAP - h >= high) return { side: 'above', top: r.top - GAP - h, left: across };
        var top = sideTop(r, high);
        if (r.right + GAP + cw <= w - EDGE) return { side: 'right', top: top, left: r.right + GAP };
        if (r.left - GAP - cw >= EDGE) return { side: 'left', top: top, left: r.left - GAP - cw };
        return null;
    }

    // The card next to its control, its arrow facing it, and the ring round the control; in the bottom corner, with no
    // arrow or ring, when its control isn't shown. Then left of the rail if it would be over it: a control in the rail is
    // pointed at from there. Everything is read before anything is written, so a move costs one layout.
    function place() {
        frame = 0;
        if (!card) return;
        find();
        var hasTarget = shown(target);
        if (needsTarget && !hasTarget) { card.hidden = true; ringAt(null); arrowAt(null); return; }
        var r = hasTarget ? target.getBoundingClientRect() : null, w = width(), rail = railBox(), high = clear();
        var at = r && beside(r, w, high)
            || { side: 'none', top: Math.max(high, innerHeight - size.height - EDGE), left: w - size.width - EDGE };
        if (rail && at.left + size.width > rail.left && at.left < rail.right && at.top < rail.bottom && at.top + size.height > rail.top) {
            at.left = Math.max(EDGE, rail.left - GAP - size.width);
            // A control in the rail: the card is now left of it.
            if (r && r.left >= rail.left - 1 && r.top >= rail.top - 1) {
                at.side = 'left';
                at.top = sideTop(r, high);
            }
        }
        card.hidden = false;
        card.style.top = at.top + 'px';
        card.style.left = at.left + 'px';
        card.dataset.side = at.side;
        card.classList.add('is-placed');
        // Marked again if a redraw took the mark off (a NavLink rewriting its class as the page changes).
        if (r) target.classList.add('tide-tip-target');
        ringAt(r);
        arrowAt(r && at, r, high);
    }

    // An element of tips.js's own drawn over the page (the ring, the arrow): at the end of the page, or of what's full
    // screen (present mode), outside anything Blazor draws; made again when that changes.
    function overlay(el, cls) {
        var home = document.fullscreenElement || document.body;
        if (el && el.parentNode === home) return el;
        if (el) el.remove();
        el = document.createElement('div');
        el.className = cls;
        el.setAttribute('aria-hidden', 'true');
        home.appendChild(el);
        pageWatch.takeRecords();   // its own insert isn't the page changing
        return el;
    }

    // The card's arrow, on the side facing its control (at `r`) and opposite its middle, inside the card's corners: its
    // own element over the card's edge (so it's no part of the card's box), turned to face the control.
    function arrowAt(at, r, high) {
        if (!at || at.side === 'none') { if (arrow) arrow.hidden = true; return; }
        arrow = overlay(arrow, 'tide-tip-arrow');
        var along = at.side === 'below' || at.side === 'above'
            ? clamp(r.left + r.width / 2 - at.left, ARROW, size.width - ARROW)
            : clamp((Math.max(r.top, high) + Math.min(r.bottom, innerHeight)) / 2 - at.top, ARROW, size.height - ARROW);
        var x, y;
        if (at.side === 'below') { x = at.left + along; y = at.top; }
        else if (at.side === 'above') { x = at.left + along; y = at.top + size.height; }
        else if (at.side === 'left') { x = at.left + size.width; y = at.top + along; }
        else { x = at.left; y = at.top + along; }
        arrow.hidden = false;
        arrow.dataset.side = at.side;
        arrow.classList.toggle('is-step', dialog);
        arrow.style.left = (x - ARROW_HALF) + 'px';
        arrow.style.top = (y - ARROW_HALF) + 'px';
    }

    // The ring round the control: drawn over the page (and a step's scrim), never over the control itself; following it
    // as the page scrolls or resizes, and gone with the card.
    function ringAt(r) {
        if (!r) { if (ring) ring.hidden = true; return; }
        ring = overlay(ring, 'tide-tip-ring');
        ring.hidden = false;
        ring.style.top = (r.top - RING) + 'px';
        ring.style.left = (r.left - RING) + 'px';
        ring.style.width = (r.width + 2 * RING) + 'px';
        ring.style.height = (r.height + 2 * RING) + 'px';
    }

    // At most once a frame, however many scrolls, resizes or rail changes come in it.
    function soon() { if (!frame) frame = requestAnimationFrame(place); }

    // The rail opening or closing (theme.js marks it on <html>) moves the card off it.
    var railWatch = new MutationObserver(soon);

    // The sticky top bar growing or shrinking: the card is placed again, clear of it.
    var barWatch = new ResizeObserver(soon);

    // A step's control not found yet, or gone, or the page moving under it: the page changing (a stylesheet added
    // included) may draw it or move it. Read without a layout (place, in the next frame, measures).
    var pageWatch = new MutationObserver(soon);

    // The control or the page changing size (letters widening, a list growing) moves the card, its arrow and the ring.
    var sizeWatch = new ResizeObserver(soon);

    // The card growing or shrinking (a longer step, wider letters) is measured again and placed again, so it stays in
    // view.
    var cardWatch = new ResizeObserver(function () {
        if (!card || card.hidden) return;
        var box = card.getBoundingClientRect();
        if (size && box.width === size.width && box.height === size.height) return;
        size = { width: box.width, height: box.height };
        soon();
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
            } else {
                target = selector ? pick(selector) : null;
                if (shown(target)) sizeWatch.observe(target);
            }
            // The page changing (a step's control drawn, a list growing above a tip's control) places it again.
            pageWatch.observe(root, { childList: true, subtree: true });
            sizeWatch.observe(document.body);
            // Measured where it starts, and again if its size changes (cardWatch).
            var box = card.getBoundingClientRect();
            size = { width: box.width, height: box.height };
            place();
            addEventListener('resize', soon, { passive: true });
            addEventListener('scroll', soon, { capture: true, passive: true });
            railWatch.observe(root, { attributes: true, attributeFilter: ['data-rail', 'data-rail-over'] });
            cardWatch.observe(card);
            // The top bar settling (its tools wrapping to a second line as the page draws) moves the card clear of it.
            var bar = document.querySelector('.top-row, .tide-present-top');
            if (bar) barWatch.observe(bar);
            // Whether it's shown, for the page to announce it.
            return !card.hidden;
        },
        hide: function () {
            if (target) target.classList.remove('tide-tip-target');
            if (frame) cancelAnimationFrame(frame);
            if (ring) { ring.remove(); ring = null; }
            if (arrow) { arrow.remove(); arrow = null; }
            var lost = inside && !dialog && (!document.activeElement || document.activeElement === document.body);
            card = target = size = looking = null;
            frame = 0;
            inside = dialog = false;
            railWatch.disconnect();
            barWatch.disconnect();
            pageWatch.disconnect();
            sizeWatch.disconnect();
            cardWatch.disconnect();
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
