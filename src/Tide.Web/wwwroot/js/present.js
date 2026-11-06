// Present mode: keyboard navigation, full screen, and the slide's fit and scroll. CSP-safe (an external file, no inline
// handlers). Keys go to .NET, which owns the current slide; typing in a field (meeting notes) is left alone.
//
// Fit, then scroll: a slide too tall for the window is shrunk (its type and spacing, all in em, through --tide-fit on its
// body) as far as it needs, but no further than 80% of its size; past that its body scrolls inside the slide, while the
// header and the footer stay put. CSS can't measure a slide's content against its room, so this does, again whenever
// the room or the content changes.
//
// The scroll: soft fades at the body's top and bottom while there's more that way (is-more-above, is-more-below, which
// also show the More pill), Space and Page Down scrolling the body before they change the slide, and the presenter's
// scroll shared with the room (at most ten times a second; .NET passes it on and brings it back to those following).
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
    const PAGE = 0.85;          // how much of the visible height Space, Page Down and the More pill scroll
    const EVERY = 100;          // at most one scroll sent to .NET each this many ms
    const INTENT = 900;         // a scroll this soon after the wheel, a touch, a key or a click is the person's own

    // Enter and Space on a focused button or link press it (End the meeting, Mark discussed, a slide dot); they turn
    // the slide only when nothing that takes them has focus.
    function pressable(target) {
        return !!(target && target.closest && target.closest('button, a[href], summary, [role="button"], [role="tab"]'));
    }

    function slideBody() { return document.querySelector('#present-root .tide-slide-body'); }

    function behavior(instant) { return instant || reduced.matches ? 'instant' : 'smooth'; }

    function room(el) { return el.scrollHeight - el.clientHeight; }

    // At the end of the body in that direction (or it doesn't scroll at all).
    function atEdge(el, forward) {
        if (!el) return true;
        const max = room(el);
        if (max <= 1) return true;
        return forward ? el.scrollTop >= max - 2 : el.scrollTop <= 1;
    }

    function fraction(el) {
        const max = room(el);
        return max > 0 ? Math.min(1, Math.max(0, el.scrollTop / max)) : 0;
    }

    // The fades and the More pill: classes on the scrolling element, while there's more that way.
    function edges(el) {
        const max = room(el), top = el.scrollTop;
        el.classList.toggle('is-more-above', max > 1 && top > 1);
        el.classList.toggle('is-more-below', max > 1 && top < max - 2);
    }

    // --- fit ----------------------------------------------------------------------------------------------------------

    function fits(el) { return el.scrollHeight <= el.clientHeight + 1; }

    function setFit(el, f) { el.style.setProperty('--tide-fit', f.toFixed(3)); }

    // The largest size, from 80% to 100%, at which the slide fits; 80% if none does (then it scrolls). Kept where it was
    // scrolled to, as a fraction.
    function fit(el) {
        const at = fraction(el);
        setFit(el, 1);
        if (!fits(el)) {
            setFit(el, FLOOR);
            if (fits(el)) {
                let lo = FLOOR, hi = 1;
                for (let i = 0; i < 6; i++) {
                    const mid = (lo + hi) / 2;
                    setFit(el, mid);
                    if (fits(el)) lo = mid; else hi = mid;
                }
                setFit(el, lo);
            }
        }
        if (at > 0) el.scrollTop = at * room(el);
        edges(el);
    }

    let shown = null, dirty = false, frame = 0, resizes = null, mutations = null;

    // Once a frame at most: a new slide's body is watched for its size; one changed in place is fitted again.
    function schedule() {
        dirty = true;
        if (!frame) frame = requestAnimationFrame(settle);
    }

    function settle() {
        frame = 0;
        const el = slideBody();
        if (el !== shown) {
            if (resizes) resizes.disconnect();
            shown = el;
            sentOnce = false;
            if (el && resizes) resizes.observe(el);
            dirty = true;
        }
        if (dirty && el) fit(el);
        dirty = false;
        document.querySelectorAll('#present-root .tide-scrollfade').forEach(function (x) { if (x !== el) edges(x); });
    }

    function onMutations(records) {
        for (const r of records) {
            if (!shown || !shown.isConnected || shown.contains(r.target) || r.target.contains(shown)) { schedule(); return; }
            if (r.target.closest && r.target.closest('.tide-scrollfade')) { schedule(); return; }
        }
    }

    // --- scroll by hand, shared -----------------------------------------------------------------------------------------

    let intentAt = 0, lastSent = 0, sendTimer = 0, sentOnce = false;

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
    // once, which pauses their following of the presenter's scroll (until .NET says it's resumed).
    function report(el) {
        const mode = el.dataset.scrollShare;
        if (!ref || !mode) return;
        if (mode === 'follow' && (el.dataset.scrollPaused === 'true' || sentOnce)) return;
        const wait = EVERY - (performance.now() - lastSent);
        if (wait <= 0) send();
        else if (!sendTimer) sendTimer = setTimeout(send, wait);
    }

    function send() {
        sendTimer = 0;
        lastSent = performance.now();
        const el = slideBody();
        if (!el || !ref) return;
        if (el.dataset.scrollShare === 'follow') sentOnce = true;
        ref.invokeMethodAsync('OnSlideScrolled', fraction(el)).catch(function () { });
    }

    function pageBy(el, forward) {
        if (!el) return;
        intended();
        el.scrollBy({ top: (forward ? 1 : -1) * el.clientHeight * PAGE, behavior: behavior(false) });
    }

    function onClick(e) {
        const more = e.target.closest && e.target.closest('[data-slide-more]');
        if (!more) return;
        e.preventDefault();
        pageBy(slideBody(), true);
    }

    // .NET: the presenter scrolled (or this page just joined, or came to their slide): this page goes to the same place.
    // Not a scroll by hand, so it isn't sent back.
    function follow(f, instant) {
        if (frame) { cancelAnimationFrame(frame); settle(); }
        const el = slideBody();
        if (!el) return;
        intentAt = 0;
        sentOnce = false;
        el.scrollTo({ top: Math.min(1, Math.max(0, f)) * room(el), behavior: behavior(instant) });
    }

    // --- keys ------------------------------------------------------------------------------------------------------------

    // Space and Page Down page the slide's body down first, Shift+Space and Page Up up; only at its end do they change
    // the slide (.NET decides: SlidePaging). The arrows and the other keys change it at once.
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
            const el = slideBody(), forward = page === 'down', edge = atEdge(el, forward);
            intended();
            ref.invokeMethodAsync('OnPageKey', forward, edge)
                .then(function (r) { if (r === 'scroll') pageBy(el, forward); })
                .catch(function () { if (!edge) pageBy(el, forward); });
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

    function onLoad(e) { if (e.target && e.target.tagName === 'IMG' && insideBody(e.target)) schedule(); }

    const intents = ['wheel', 'touchmove', 'pointerdown'];

    return {
        start: function (dotnetRef) {
            ref = dotnetRef;
            document.addEventListener('keydown', onKey);
            document.addEventListener('fullscreenchange', onFullscreenChange);
            document.addEventListener('scroll', onScroll, true);
            document.addEventListener('click', onClick);
            document.addEventListener('load', onLoad, true);
            intents.forEach(function (n) { document.addEventListener(n, onIntent, { capture: true, passive: true }); });
            window.addEventListener('resize', schedule);
            if (window.ResizeObserver) resizes = new ResizeObserver(schedule);
            mutations = new MutationObserver(onMutations);
            mutations.observe(document.body, { childList: true, subtree: true, characterData: true });
            if (document.fonts) document.fonts.ready.then(schedule, function () { /* fonts never settled: the next resize re-fits */ });
            schedule();
        },
        stop: function () {
            ref = null;
            document.removeEventListener('keydown', onKey);
            document.removeEventListener('fullscreenchange', onFullscreenChange);
            document.removeEventListener('scroll', onScroll, true);
            document.removeEventListener('click', onClick);
            document.removeEventListener('load', onLoad, true);
            intents.forEach(function (n) { document.removeEventListener(n, onIntent, { capture: true, passive: true }); });
            window.removeEventListener('resize', schedule);
            if (resizes) resizes.disconnect();
            if (mutations) mutations.disconnect();
            if (frame) cancelAnimationFrame(frame);
            if (sendTimer) clearTimeout(sendTimer);
            resizes = null;
            mutations = null;
            shown = null;
            frame = 0;
            sendTimer = 0;
            if (document.fullscreenElement) document.exitFullscreen().catch(function () { });
        },
        toggleFullscreen: toggleFullscreen,
        follow: follow,
        refit: schedule
    };
})();
