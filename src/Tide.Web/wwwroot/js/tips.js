// A page's tip (OnboardingHost): set beside the control it's about, which is marked, or left in its corner when that
// control isn't shown or there's no room beside it. A tip isn't modal: this never moves focus or takes a key. CSP-safe
// (an external file).
window.tideTips = (function () {
    var card = null, target = null, size = null, frame = 0;
    var GAP = 10, EDGE = 8;

    function shown(el) { return !!el && el.isConnected && el.getClientRects().length > 0 && !el.closest('[inert], [hidden]'); }

    // Below the control, else above it; across, centred on it but inside the window. In its corner when neither fits.
    // Everything is read before anything is written, so a move costs one layout.
    function place() {
        frame = 0;
        if (!card) return;
        var r = shown(target) ? target.getBoundingClientRect() : null;
        var top = !r ? null : r.bottom + GAP + size.height <= innerHeight - EDGE ? r.bottom + GAP
            : r.top - GAP - size.height >= EDGE ? r.top - GAP - size.height : null;
        if (top === null) {
            card.classList.remove('is-placed');
            card.style.removeProperty('top');
            card.style.removeProperty('left');
            return;
        }
        var left = Math.min(Math.max(EDGE, r.left + r.width / 2 - size.width / 2), innerWidth - size.width - EDGE);
        card.style.top = top + 'px';
        card.style.left = left + 'px';
        card.classList.add('is-placed');
    }

    // At most once a frame, however many scrolls or resizes come in it.
    function soon() { if (!frame) frame = requestAnimationFrame(place); }

    // A tour step is a modal dialog: focus that lands outside it (a new page focuses its heading as it opens) goes back
    // to its main button.
    var modal = false;
    function keep(e) {
        if (!modal || !card || !card.isConnected || card.contains(e.target)) return;
        var main = card.querySelector('.btn-primary');
        if (main) setTimeout(function () { if (card && card.isConnected && !card.contains(document.activeElement)) main.focus(); }, 0);
    }
    document.addEventListener('focusin', keep);

    return {
        // `asDialog`: a tour step, which keeps focus; a tip never takes it.
        show: function (el, selector, asDialog) {
            this.hide();
            card = el;
            modal = !!asDialog;
            target = selector ? document.querySelector(selector) : null;
            if (shown(target)) target.classList.add('tide-tip-target');
            // The card's size doesn't change while it's up: measured once, where it starts.
            var box = card.getBoundingClientRect();
            size = { width: box.width, height: box.height };
            place();
            addEventListener('resize', soon, { passive: true });
            addEventListener('scroll', soon, { capture: true, passive: true });
        },
        hide: function () {
            if (target) target.classList.remove('tide-tip-target');
            if (frame) cancelAnimationFrame(frame);
            card = target = size = null;
            modal = false;
            frame = 0;
            removeEventListener('resize', soon, { passive: true });
            removeEventListener('scroll', soon, { capture: true, passive: true });
        }
    };
})();
