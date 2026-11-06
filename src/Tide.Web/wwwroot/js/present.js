// Present mode: keyboard navigation, full screen, and the slide's fit and scroll. CSP-safe (an external file, no inline
// handlers). Keys go to .NET, which owns the current slide; typing in a field (meeting notes) is left alone.
//
// Fit, then scroll: a slide too tall for the window is shrunk (its type and spacing, all in em, through --tide-fit on its
// body) as far as it needs, but no further than 80% of its size; past that its body scrolls inside the slide, while the
// header and the footer stay put. CSS can't measure a slide's content against its room, so this does, again whenever
// the room (the body) or the content (its child) changes size: ResizeObservers on both.
//
// The scroll: soft fades at the body's top and bottom while there's more that way (is-more-above, is-more-below, which
// also show the More pill), Space and Page Down scrolling the body before they change the slide, and the presenter's
// scroll shared with the room (at most ten times a second, the last always; .NET passes it on and brings it back to
// those following). The action and product cards over a slide (.tide-scrollfade too) keep their own fades.
window.tidePresent = (function () {
    let ref = null;
    const typing = window.tideShortcuts.typing, inMenu = window.tideShortcuts.inMenu;
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)');

    const keys = {
        ArrowRight: 'next', Enter: 'next',
        ArrowLeft: 'prev', Backspace: 'prev',
        Home: 'first', End: 'last',
        f: 'fullscreen', F: 'fullscreen',
        d: 'discussed', D: 'discussed',
        Escape: 'close'
    };

    const FLOOR = 0.8;          // the smallest a slide is shrunk to fit, of its size
    const NEAR = 0.01;          // how far from its first guess the fit looks first
    const CLOSE = 0.005;        // and how close it gets to the largest size that fits
    const PAGE = 0.85;          // how much of the visible height Space, Page Down and the More pill scroll
    const EVERY = 100;          // at most one scroll sent to .NET each this many ms
    const INTENT = 900;         // a scroll this soon after the wheel, a touch, a key or a click is the person's own

    // Enter and Space on a focused button or link press it (End the meeting, Mark discussed, a slide dot); they turn
    // the slide only when nothing that takes them has focus.
    function pressable(target) {
        return !!(target && target.closest && target.closest('button, a[href], summary, [role="button"], [role="tab"]'));
    }

    function slideBody() { return document.querySelector('#present-root > .tide-slide > .tide-slide-body'); }

    // What Space and Page Down page: the body of a panel open over the slide (an action, a product's card, the tag
    // panel), which is what's in view, rather than the slide hidden behind it; else the slide's body.
    function pagedBody() {
        const open = document.querySelectorAll('#present-root .tide-present-action > .tide-present-action-body');
        return open.length ? open[open.length - 1] : slideBody();
    }

    function behavior(instant) { return instant || reduced.matches ? 'instant' : 'smooth'; }

    // How far the element scrolls.
    function range(el) { return el.scrollHeight - el.clientHeight; }

    // Whether there's more of the element above and below what shows: the one rule for the fades, the More pill and
    // whether Space scrolls or moves on.
    function more(el) {
        const max = range(el), top = el.scrollTop;
        return { above: max > 1 && top > 1, below: max > 1 && top < max - 2 };
    }

    // At the end of the body in that direction (or it doesn't scroll at all).
    function atEdge(el, forward) {
        if (!el) return true;
        const m = more(el);
        return !(forward ? m.below : m.above);
    }

    function fraction(el) {
        const max = range(el);
        return max > 0 ? el.scrollTop / max : 0;
    }

    // The fades and the More pill: classes on the scrolling element, while there's more that way.
    function edges(el) {
        const m = more(el);
        el.classList.toggle('is-more-above', m.above);
        el.classList.toggle('is-more-below', m.below);
    }

    // --- fit ----------------------------------------------------------------------------------------------------------

    function setFit(el, f) { el.style.setProperty('--tide-fit', f.toFixed(3)); }

    // The largest size, from 80% to 100%, at which the slide fits; 80% if none does (then it scrolls). Measured at full
    // size first; if that's too tall, at the guess that it all shrinks in step (the body's room over its height). From
    // there it steps up while it fits (or down while it doesn't), by NEAR and then twice as far each time, until it has
    // a size that fits and one that doesn't; a short bisection between the two finishes it.
    function largestFit(el) {
        const room = el.clientHeight + 1;
        const fitsAt = function (f) {
            setFit(el, f);
            return el.scrollHeight <= room;
        };
        if (fitsAt(1)) return 1;
        const guess = Math.min(1, Math.max(FLOOR, el.clientHeight / el.scrollHeight));
        let lo, hi, d = NEAR, f;
        if (fitsAt(guess)) {
            lo = guess;
            f = lo + d;
            while (f < 1 && fitsAt(f)) {
                lo = f;
                d *= 2;
                f = lo + d;
            }
            hi = Math.min(1, f);
        } else {
            if (guess <= FLOOR) return FLOOR;
            hi = guess;
            f = Math.max(FLOOR, hi - d);
            while (!fitsAt(f)) {
                if (f <= FLOOR) return FLOOR;
                hi = f;
                d *= 2;
                f = Math.max(FLOOR, hi - d);
            }
            lo = f;
        }
        while (hi - lo > CLOSE) {
            const mid = (lo + hi) / 2;
            if (fitsAt(mid)) lo = mid;
            else hi = mid;
        }
        return lo;
    }

    // Kept where it was scrolled to, as a fraction.
    function fit(el) {
        const at = fraction(el);
        setFit(el, largestFit(el));
        if (at > 0) el.scrollTop = at * range(el);
        edges(el);
    }

    function content(el) { return el.querySelector(':scope > .tide-slide-inner, :scope > .tide-slide-cover'); }

    // What the last fit was for: the room the body had and its content's height then. Neither changed: nothing to fit.
    let fittedFor = '';

    function sizeOf(el) {
        const c = content(el);
        return el.clientHeight + ':' + (c ? c.offsetHeight : el.scrollHeight);
    }

    // --- what's watched -----------------------------------------------------------------------------------------------
    //
    // The slide's body and its content are watched for their size (a new body for each slide, from .NET's @key); the
    // page's root, the slides' root and the slide itself only for children coming and going (a new slide's body, a card
    // opened over it), so a timer ticking or notes being typed don't wake this.

    let shown = null, shownContent = null, frame = 0, sizes = null, cardSizes = null, children = null;
    let watched = [], cards = [];

    function schedule() {
        if (!frame) frame = requestAnimationFrame(settle);
    }

    function same(a, b) { return a.length === b.length && a.every((x, i) => x === b[i]); }

    function watch() {
        const root = document.getElementById('present-root');
        const page = root || document.querySelector('.tide-present');
        const slide = root && root.querySelector(':scope > .tide-slide');
        const targets = [page && page.parentElement, root, slide].filter(Boolean);
        if (children && !same(targets, watched)) {
            children.disconnect();
            for (const t of targets) children.observe(t, { childList: true });
            watched = targets;
        }

        const el = slideBody(), c = el && content(el);
        if (el !== shown || c !== shownContent) {
            if (sizes) {
                sizes.disconnect();
                if (el) sizes.observe(el);
                if (c) sizes.observe(c);
            }
            if (el !== shown) fittedFor = '';
            shown = el;
            shownContent = c;
        }

        // The cards over the slide: each body and what's in it, so a card's fades follow its own size.
        const now = root ? Array.from(root.querySelectorAll('.tide-scrollfade')).filter(x => x !== el) : [];
        if (cardSizes && !same(now, cards)) {
            cardSizes.disconnect();
            for (const x of now) {
                cardSizes.observe(x);
                for (const k of x.children) cardSizes.observe(k);
            }
        }
        cards = now;
    }

    function settle() {
        frame = 0;
        watch();
        const el = shown;
        if (!el || sizeOf(el) === fittedFor) return;
        fit(el);
        fittedFor = sizeOf(el);
    }

    function onCardSizes(entries) {
        for (const e of entries) {
            const card = e.target.classList.contains('tide-scrollfade') ? e.target : e.target.parentElement;
            if (card && card.isConnected) edges(card);
        }
    }

    // --- scroll by hand, shared -----------------------------------------------------------------------------------------

    // When the person last scrolled, or pressed or touched, the slide: never, to begin with (not 0, which is when the
    // page opened, so a late joiner's landing at the presenter's place in its first moments isn't taken for their own).
    let intentAt = -Infinity, lastSent = 0, sendTimer = 0;

    function intended() { intentAt = performance.now(); }

    function insideBody(target) { return !!(target && target.closest && target.closest('.tide-slide-body')); }

    function onIntent(e) { if (insideBody(e.target)) intended(); }

    function onScroll(e) {
        const el = e.target;
        if (!(el instanceof Element) || !el.classList.contains('tide-scrollfade')) return;
        edges(el);
        if (el.classList.contains('tide-slide-body') && performance.now() - intentAt < INTENT) report(el);
    }

    // A scroll by hand: the presenter's goes to the room, ten times a second at most (the last always); a follower's
    // pauses their following of the presenter's scroll, until .NET, which says so on the body (data-scroll-paused), says
    // it's resumed.
    function report(el) {
        const mode = el.dataset.scrollShare;
        if (!ref || !mode) return;
        if (mode === 'follow' && el.dataset.scrollPaused === 'true') return;
        const wait = EVERY - (performance.now() - lastSent);
        if (wait <= 0) send();
        else if (!sendTimer) sendTimer = setTimeout(send, wait);
    }

    function send() {
        sendTimer = 0;
        lastSent = performance.now();
        const el = slideBody();
        if (!el || !ref) return;
        ref.invokeMethodAsync('OnSlideScrolled', fraction(el)).catch(function () { });
    }

    function pageBy(el, forward) {
        if (!el) return;
        intended();
        el.scrollBy({ top: (forward ? 1 : -1) * el.clientHeight * PAGE, behavior: behavior(false) });
    }

    function onClick(e) {
        const pill = e.target.closest && e.target.closest('[data-slide-more]');
        if (!pill) return;
        e.preventDefault();
        pageBy(slideBody(), true);
    }

    // .NET: the presenter scrolled (or this page just joined, or came to their slide): this page goes to the same place,
    // a fraction .NET has already kept between 0 and 1. Not a scroll by hand, so it isn't sent back.
    function follow(f, instant) {
        if (frame) { cancelAnimationFrame(frame); settle(); }
        const el = slideBody();
        if (!el) return;
        intentAt = -Infinity;
        el.scrollTo({ top: f * range(el), behavior: behavior(instant) });
    }

    // --- keys ------------------------------------------------------------------------------------------------------------

    // Space and Page Down page the slide's body down first, Shift+Space and Page Up up; only at its end do they change
    // the slide. The arrows and the other keys change it at once.
    function paging(e) {
        if (e.key === 'PageDown') return 'down';
        if (e.key === 'PageUp') return 'up';
        if (e.key === ' ') return e.shiftKey ? 'up' : 'down';
        return null;
    }

    function onKey(e) {
        if (!ref || e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey || typing(e.target) || inMenu(e.target)) return;
        // A screenshot shown full size has the keys; the slide behind it doesn't move.
        if (document.querySelector('.tide-lightbox')) return;
        if (document.querySelector('.tide-modal')) return;   // the palette or the shortcuts list is open: its keys are its own
        if (document.querySelector('.tide-atkm-panel')) return;   // the ATT&CK picker (the tag panel's): Esc closes it alone
        if (!window.tideShortcuts.on() && window.tideShortcuts.isLetter(e)) return;
        if ((e.key === 'Enter' || e.key === ' ') && pressable(e.target)) return;
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { intended(); return; }   // the browser scrolls a focused body
        const page = paging(e);
        if (page) {
            e.preventDefault();
            const el = pagedBody(), forward = page === 'down';
            if (!atEdge(el, forward)) { pageBy(el, forward); return; }
            ref.invokeMethodAsync('OnPresentKey', forward ? 'next' : 'prev').catch(function () { });
            return;
        }
        const action = keys[e.key];
        if (!action) return;
        // Escape leaves full screen by itself; only a second Escape (not in full screen) closes the zoomed image.
        if (action === 'close' && document.fullscreenElement) return;
        e.preventDefault();
        if (action === 'fullscreen') { toggleFullscreen(); return; }
        ref.invokeMethodAsync('OnPresentKey', action).catch(function () { });
    }

    // The whole page goes full screen, not just the slides: the palette, the shortcuts list and the toasts are drawn
    // beside them, and a full-screen element hides everything outside it (a dialog opened there froze the slides).
    function toggleFullscreen() {
        const el = document.documentElement;
        if (document.fullscreenElement) { document.exitFullscreen().catch(function () { }); }
        else if (el.requestFullscreen) { el.requestFullscreen().catch(function () { }); }
    }

    function onFullscreenChange() {
        if (ref) ref.invokeMethodAsync('OnFullscreenChanged', !!document.fullscreenElement).catch(function () { });
        schedule();
    }

    const intents = ['wheel', 'touchmove', 'pointerdown'];

    return {
        start: function (dotnetRef) {
            ref = dotnetRef;
            document.addEventListener('keydown', onKey);
            document.addEventListener('fullscreenchange', onFullscreenChange);
            document.addEventListener('scroll', onScroll, true);
            document.addEventListener('click', onClick);
            intents.forEach(function (n) { document.addEventListener(n, onIntent, { capture: true, passive: true }); });
            if (window.ResizeObserver) {
                sizes = new ResizeObserver(schedule);
                cardSizes = new ResizeObserver(onCardSizes);
            }
            children = new MutationObserver(schedule);
            if (document.fonts) document.fonts.ready.then(schedule, function () { /* fonts never settled: the next resize re-fits */ });
            schedule();
        },
        stop: function () {
            ref = null;
            document.removeEventListener('keydown', onKey);
            document.removeEventListener('fullscreenchange', onFullscreenChange);
            document.removeEventListener('scroll', onScroll, true);
            document.removeEventListener('click', onClick);
            intents.forEach(function (n) { document.removeEventListener(n, onIntent, { capture: true, passive: true }); });
            for (const o of [sizes, cardSizes, children]) if (o) o.disconnect();
            if (frame) cancelAnimationFrame(frame);
            if (sendTimer) clearTimeout(sendTimer);
            sizes = null;
            cardSizes = null;
            children = null;
            shown = null;
            shownContent = null;
            watched = [];
            cards = [];
            frame = 0;
            sendTimer = 0;
            if (document.fullscreenElement) document.exitFullscreen().catch(function () { });
        },
        toggleFullscreen: toggleFullscreen,
        follow: follow,
        // .NET: this page has just become the presenter: where it is on the slide is now the room's.
        share: function () { if (ref) send(); }
    };
})();
