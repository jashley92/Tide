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

    const keys = {
        ArrowRight: 'next', Enter: 'next',
        ArrowLeft: 'prev', Backspace: 'prev',
        Home: 'first', End: 'last',
        f: 'fullscreen', F: 'fullscreen',
        d: 'discussed', D: 'discussed',
        ']': 'rail',
        Escape: 'close'
    };
    // The slide's keys heard while a step of Learn to present (rehearsal) is up: the ones that move the slides (onKey).
    const stepKeys = { ArrowRight: 'next', ArrowLeft: 'prev', Home: 'first', End: 'last' };
    // The slide's keys heard while the presenter's rail is open over it (onKey): those, and D.
    const railKeys = Object.assign({ d: 'discussed', D: 'discussed' }, stepKeys);

    // Whether the window has room to dock the presenter's rail beside the slide (.NET draws it there, or over the slide).
    const railRoom = window.matchMedia('(min-width: 1500px)');
    function onRailRoom() { if (ref) ref.invokeMethodAsync('OnRailRoom', railRoom.matches).catch(function () { }); }

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
    // An action beside the slide (a column of the page, not over it) leaves them to the slide, whose scroll the room shares.
    function pagedBody() { return cardOver() || slideBody(); }

    // The body of the panel open over the slide (the last one, if more than one is), or null with none, or with one only
    // beside the slide.
    function cardOver() {
        const open = Array.from(document.querySelectorAll('#present-root .tide-present-action > .tide-present-action-body'))
            .filter(function (body) { return getComputedStyle(body.parentElement).position === 'fixed'; });
        return open.length ? open[open.length - 1] : null;
    }

    // The keys that move the slide. A panel open over the slide holds them: it's what's in view, and moving on would close
    // it under someone reading it (Escape closes it). One beside the slide leaves them to the slide.
    const moves = { next: true, prev: true, first: true, last: true };

    function behavior(instant) { return instant || window.tideShortcuts.still() ? 'instant' : 'smooth'; }

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
        if (slide && !turnedFrom) turnedFrom = slideNo(slide);
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

    // The slide changed (its body is new, keyed by .NET): the page turns. Going on, the old page lifts from its left edge
    // and turns away, fading as it goes, uncovering the new one. Going back, the new page turns in over the old the other
    // way. For the presenter and each follower alike. Only transform and opacity move, so the browser composites each
    // frame rather than painting the slide again, and the page stays solid as it turns (see below): an audience watches it. The
    // page moves the moment the key is pressed (an easing that starts at once, the lift early), and a shade turns with it,
    // deepest as the page lifts, so it reads as paper; the shade is its own layer, only its opacity changing. A
    // turn still playing when the next comes is finished at once, so pressing on quickly never stacks pages. A jump of
    // more than one slide (End, Home, the meeting ending) isn't one page turning: the old fades out over the new. With reduced
    // motion, the old page fades out over the new, quickly. The old page is the copy settle() last took of the slide as it
    // was drawn (scrolled where it was last seen), since by the time the new body arrives .NET has emptied and taken away
    // the old one.
    let turnedFrom = 0, lastPage = null, turning = null, turned = null;   // turned: the turn playing, from and to
    function slideNo(s) { const m = /Slide (\d+)/.exec(s.getAttribute('aria-label') || ''); return m ? +m[1] : 0; }
    function turn() {
        const slide = document.querySelector('#present-root > .tide-slide'), ts = window.tideShortcuts;
        if (!slide) return;
        const no = slideNo(slide), was = turnedFrom, last = lastPage, now = slideBody();
        if (!was || no === was || !last || !now || !now.animate) return;
        turnedFrom = no;
        lastPage = null;
        // Back to the page a turn still under way came from (the other key, pressed mid-turn): the page turns back from
        // where it has got to, rather than finishing (a frame of the next slide bare) and starting over from edge on.
        const live = turning && turning.playState === 'running' && turned && turned.from === no && turned.to === was;
        const tilt = live ? new DOMMatrixReadOnly(getComputedStyle(turned.el).transform).m11 : 1;   // cos of its angle now
        // On again the same way while a page is still turning (the key pressed quickly, or held): the page in the air
        // carries on and uncovers the latest slide, rather than each slide between showing bare for a few frames.
        const onward = !live && turning && turning.playState === 'running' && turned && Math.abs(no - was) === 1 && (no > was) === (turned.to > turned.from);
        if (onward && turned.el.isConnected) {
            turned.to = no;
            return;
        }
        // Back again while the page coming in is still turning (its body was drawn afresh, so that turn can't carry on):
        // the next page comes in from the same point of its turn, not from edge on again.
        const carry = onward && no < was ? turning.currentTime || 0 : 0;
        // Any other turn still in the air (keys mixed quickly) fades where it is, quickly, rather than vanishing in a frame.
        if (!live && !onward && turning && turning.playState === 'running' && turned && turned.el.isConnected && !ts.still()) {
            const el = turned.el, cs = getComputedStyle(el), ghost = el.cloneNode(true);
            ghost.setAttribute('aria-hidden', 'true');
            ghost.setAttribute('data-tide-copy', '');
            ghost.inert = true;
            ghost.classList.remove('tide-page-over', 'tide-scrollfade');
            ghost.classList.add('tide-page-turning');
            ghost.style.transform = cs.transform;
            el.parentElement.appendChild(ghost);
            const gone = function () { ghost.remove(); };
            ghost.animate([{ opacity: +cs.opacity }, { opacity: 0 }], { duration: ts.ms('--tide-dur-fast'), easing: ts.token('--tide-ease-out'), fill: 'forwards' })
                .finished.then(gone, gone);
        }
        if (turning) turning.finish();
        if (document.hidden) return;
        const back = no < was, still = ts.still() || Math.abs(no - was) > 1, dur = ts.ms(ts.still() ? '--tide-dur-fast' : still ? '--tide-dur-base' : '--tide-dur-turn');
        const page = ts.copy(last.page);
        page.classList.remove('tide-scrollfade', 'tide-page-over');   // a copy taken mid-turn would sit over the page turning in
        page.classList.add('tide-page-turning');
        slide.appendChild(page);
        if (last.top) page.scrollTop = last.top;   // (setting it lays the page out at once: only when it's needed)
        const at = function (deg) { return 'perspective(4000px) rotateY(' + (still ? 0 : deg) + 'deg)'; };
        // The page is solid paper while it turns, uncovering the next one by turning over until it's nearly edge on, and
        // only fades in its last tenth: any earlier, the two slides show through each other, which reads as a dissolve.
        // Seen from far enough off that the edge lifting towards the room grows by about half at most.
        // The new page turning in over the old (back) plays these the other way round, so its offsets are mirrored; a
        // back jump that fades (reduced motion, or more than one slide) plays them forwards like any fade.
        const inward = back && !still, lift = inward ? .75 : .25, gone = inward ? .1 : .9;
        const flat = { transform: at(0), opacity: 1 }, lifting = { transform: at(-20), opacity: 1, offset: lift };
        const turnedOver = { transform: at(-78), opacity: 1, offset: gone }, away = { transform: at(-90), opacity: 0 };
        const shade = still ? null : document.createElement('div');
        const done = function () { page.remove(); if (shade) shade.remove(); now.classList.remove('tide-page-over'); };
        const play = function (el, frames, ease) {
            const opts = { duration: dur, easing: ts.token(ease), fill: 'backwards' };
            turning = el.animate(frames, opts);
            turned = { from: was, to: no, el: el };
            // Turning back, it starts at the angle the page had got to (found by halving: the two ways have different
            // easings, so the same share of the time isn't the same angle).
            let from = 0;
            if (carry && inward) turning.currentTime = from = carry;
            else if (live && !still) {
                let lo = 0, hi = dur;
                for (let i = 0; i < 12; i++) {
                    const mid = (lo + hi) / 2;
                    turning.currentTime = mid;
                    const cos = new DOMMatrixReadOnly(getComputedStyle(el).transform).m11;
                    if ((cos < tilt) === inward) lo = mid; else hi = mid;
                }
                turning.currentTime = from = (lo + hi) / 2;
            }
            turning.finished.then(done, done);
            if (!shade) return;
            shade.className = 'tide-page-shade';
            slide.appendChild(shade);
            const s = shade.animate(frames.map(function (f) { return Object.assign({}, f, { opacity: f === lifting ? .45 : f === turnedOver ? .6 : 0 }); }), opts);
            if (from) s.currentTime = from;
        };
        if (inward) {
            now.classList.add('tide-page-over');
            play(now, [away, turnedOver, lifting, flat], '--tide-ease-out');
            return;
        }
        play(page, [flat, lifting, turnedOver, away], '--tide-ease-grab');
    }

    function settle() {
        frame = 0;
        watch();
        const el = shown;
        if (!el || sizeOf(el) === fittedFor) return;
        fit(el);
        fittedFor = sizeOf(el);
        lastPage = { page: el.cloneNode(true), top: el.scrollTop };
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
        if (el === shown && lastPage) lastPage.top = el.scrollTop;   // where the page turns from, when it turns
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
        // With the slide it's of: one sent on the timer after the presenter moved on is the last slide's, which the
        // meeting drops rather than taking it for the new one's.
        ref.invokeMethodAsync('OnSlideScrolled', fraction(el), el.dataset.slideKey || null).catch(function () { });
    }

    function pageBy(el, forward) {
        if (!el) return;
        intended();
        el.scrollBy({ top: (forward ? 1 : -1) * el.clientHeight * PAGE, behavior: behavior(false) });
    }

    // The button that last opened a card over the slide (data-card-opener: the indicators' chips and "+N more", the CVEs'
    // "+N more"), so focus goes back to it when this page closes the card, rather than to the page. Found again by its
    // value if it was drawn afresh meanwhile.
    let opener = null, openerKey = null;

    function backToOpener() {
        const el = opener && opener.isConnected ? opener
            : openerKey ? document.querySelector('[data-card-opener="' + CSS.escape(openerKey) + '"]') : null;
        opener = openerKey = null;
        if (el) el.focus({ preventScroll: true });
    }

    // .NET: the rail has stopped being drawn (closed with its own button, handed off, or taken over). Focus that was in
    // it dropped to the page's body: it goes to Rail, or with no Rail any more (not presenting), the top bar's first button.
    function refocus() {
        const at = document.activeElement;
        if (at && at !== document.body) return;
        const to = document.getElementById('present-rail-toggle') || document.querySelector('.tide-present-top button:not([disabled])');
        if (to) to.focus({ preventScroll: true });
    }

    function onClick(e) {
        const from = e.target.closest && e.target.closest('[data-card-opener]');
        if (from) { opener = from; openerKey = from.getAttribute('data-card-opener'); }
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
    // the slide. The arrows and the other keys change it at once. With a panel open over the slide, Space and the page
    // keys page it and stop at its top and end, and no key changes the slide (cardOver), with the presenter's rail or a
    // step of Learn to present open too.
    function paging(e) {
        if (e.key === 'PageDown') return 'down';
        if (e.key === 'PageUp') return 'up';
        if (e.key === ' ') return e.shiftKey ? 'up' : 'down';
        return null;
    }

    function onKey(e) {
        // A step of Learn to present (rehearsal) is a dialog over the slides, and over the presenter's rail when a step
        // opened it: the keys that move the slides still do, as its first step says, and focus stays in the step. The
        // rest (Enter, Space, Escape, Tab) are the step's; a field's are its own; a dialog over the step has them all.
        if (ref && document.querySelector('.tide-modal[data-guide-card]')) {
            const move = stepKeys[e.key];
            if (!move || document.querySelector('.tide-modal:not([data-guide-card])') || e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey || typing(e.target)) return;
            e.preventDefault();
            if (moves[move] && cardOver()) return;   // a card over the slide holds them, as below
            ref.invokeMethodAsync('OnPresentKey', move).catch(function () { });
            return;
        }
        // The presenter's rail open over the slide is a modal dialog: wherever focus is (in it, or dropped to the page), ]
        // and Escape close it (not while typing). The slide's keys that press nothing in the rail still work, as its D
        // hint says: D ticks the article, the arrows, Home and End move the slide under it (not with a card open over the
        // slide: cardOver), and focus stays in the rail.
        // Enter, Space and Backspace are the rail's own; Tab is dialog.js's. A dialog open over it (a confirmation from
        // Raise or Add) has the keys: its Escape cancels it alone.
        const rail = ref && document.querySelector('#present-rail[aria-modal="true"]');
        if (rail) {
            if (document.querySelector('.tide-modal')) return;
            if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey || typing(e.target)) return;
            const action = e.key === ']' || e.key === 'Escape' ? 'rail' : railKeys[e.key];
            if (!action || action === 'discussed' && !window.tideShortcuts.on()) return;
            e.preventDefault();
            if (moves[action] && cardOver()) return;   // a card over the slide holds them, as below
            ref.invokeMethodAsync('OnPresentKey', action).then(function () {
                if (action !== 'rail' && rail.isConnected && !rail.contains(document.activeElement)) rail.focus();
            }).catch(function () { });
            return;
        }
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
            if (cardOver()) return;   // at the panel's end: it stays open
            ref.invokeMethodAsync('OnPresentKey', forward ? 'next' : 'prev').catch(function () { });
            return;
        }
        const action = keys[e.key];
        if (!action) return;
        // Escape leaves full screen by itself; only a second Escape (not in full screen) closes the zoomed image.
        if (action === 'close' && document.fullscreenElement) return;
        if (moves[action] && cardOver()) { e.preventDefault(); return; }
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
            railRoom.addEventListener('change', onRailRoom);
            onRailRoom();
            document.addEventListener('keydown', onKey);
            document.addEventListener('fullscreenchange', onFullscreenChange);
            document.addEventListener('scroll', onScroll, true);
            document.addEventListener('click', onClick);
            intents.forEach(function (n) { document.addEventListener(n, onIntent, { capture: true, passive: true }); });
            if (window.ResizeObserver) {
                sizes = new ResizeObserver(schedule);
                cardSizes = new ResizeObserver(onCardSizes);
            }
            children = new MutationObserver(function () { turn(); schedule(); });
            if (document.fonts) document.fonts.ready.then(schedule, function () { /* fonts never settled: the next resize re-fits */ });
            schedule();
        },
        stop: function () {
            ref = null;
            railRoom.removeEventListener('change', onRailRoom);
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
            opener = openerKey = null;
            frame = 0;
            sendTimer = 0;
            if (document.fullscreenElement) document.exitFullscreen().catch(function () { });
        },
        toggleFullscreen: toggleFullscreen,
        follow: follow,
        // .NET: this page closed the card over the slide: focus back on the button that opened it.
        backToOpener: backToOpener,
        refocus: refocus,
        // .NET: this page has just become the presenter: where it is on the slide is now the room's.
        share: function () { if (ref) send(); }
    };
})();
