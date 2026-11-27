// Things in a list move rather than jump (FLIP). A list marked data-tide-flip="<its items' selector>" is watched: when
// its items change (one added, removed or moved, by .NET or by a script), each item still there glides from where it
// was to where it is now, so the rows below a new one make room, the gap a promoted row left closes, and the cards in a
// lane slide aside for the slot a held card would drop into. An item is known by its data-id where it has one, so a
// board card that .NET draws afresh in another lane (its Move menu, someone else's move, a drop) is the same card: it
// flies there from where it was, over the page, as lanes clip what's outside them. A list that arrives with several
// items at once (a page loaded) shows them in quick order, the first time it's seen only: a tab or filter changed on a
// list already there just shows what's in it, as someone working all day doesn't want it played again each time; a few
// new items among the rest (a group opened) fade in. With data-tide-flip-exit, an item that leaves fades out where it
// was (toasts). With reduced motion, or in a tab nobody is looking at, nothing moves: new items still
// fade in, and leaving ones fade out.
//
// Each change is measured first and then played, so the page is laid out once for all the items, not once per item.
// Where each item was is kept relative to its list (so scrolling the window changes nothing) and without any motion
// playing on it (a glide, or an entrance like a toast's): after each change, after a change inside an item (a row that
// grew), when the list changes size, when a lane inside it scrolls, and when an item's own motion ends. While a ghost
// line closes up (a row that left, live) the list's size changes each frame: it's measured once that's done. A glide
// takes as long as its distance needs (tideShortcuts.travel).
window.tideFlip = (function () {
    const ROOTS = '[data-tide-flip]', MOVED = .5, STAGGERED = 8, FLIGHTS = 8, COPY = '[data-tide-copy]';
    const goneFrom = new WeakMap(), flipKeys = new WeakMap(), places = new WeakMap(), at = new WeakMap(), running = new WeakMap(), due = new Map(), heights = new WeakMap(), pinned = new WeakMap();
    // The part of the app a page is in (/library, /briefs): lists come in afresh on going to another part, not on a tab
    // or a day within one (/library/outreach to /library/playbooks).
    function part() { return '/' + location.pathname.split('/')[1]; }
    let frame = 0, page = part();
    const entered = new Set();   // the lists that came in on this page
    const sized = window.ResizeObserver ? new ResizeObserver(function (es) {
        es.forEach(function (e) { if (!e.target.isConnected) sized.unobserve(e.target); else if (!closing(e.target)) later(e.target); });
    }) : null;
    function closing(root) { const g = root.querySelector('.tide-live-ghost'); return !!g && g.getAnimations().length > 0; }

    function items(root) { return Array.prototype.slice.call(root.querySelectorAll(root.dataset.tideFlip)); }
    function key(el) { return el.dataset.id || el; }

    // Where it is in its list, less any motion playing on it (that's motion, not its place), its laid-out size (not one a
    // tilt or scale enlarges), the container it's in, and the motion's offset now; null while it isn't shown.
    function measure(el, origin) {
        const r = el.getBoundingClientRect();
        if (!r.width && !r.height) return null;
        let dx = 0, dy = 0;
        const a = running.get(el);
        if ((a && a.playState === 'running') || el.getAnimations().length) {
            const t = getComputedStyle(el).transform;
            if (t && t !== 'none') { const m = new DOMMatrixReadOnly(t); dx = m.m41; dy = m.m42; }
        }
        const w = el.offsetWidth || r.width, h = el.offsetHeight || r.height;
        return { x: r.left + (r.width - w) / 2 - dx - origin.left, y: r.top + (r.height - h) / 2 - dy - origin.top, w: w, h: h, dx: dx, dy: dy, parent: el.parentElement };
    }

    // Measures the list's items and keeps where each is, giving back where they were before (null the first time).
    function snapshot(root) {
        const was = at.get(root) || null, box = root.getBoundingClientRect(), list = items(root), now = new Map();
        // Kept relative to the list, as the window scrolling moves both together; but a list fixed to the window (the
        // toasts, pinned at its foot) grows from its foot, so its items are kept as the window sees them.
        if (!pinned.has(root)) pinned.set(root, getComputedStyle(root).position === 'fixed');
        const origin = pinned.get(root) ? { left: 0, top: 0, height: box.height } : box;
        heights.set(root, box.height);
        boxOf(root);
        list.forEach(function (el) { const p = measure(el, origin); if (p) now.set(key(el), p); });
        at.set(root, now);
        places.set(root, { left: box.left + scrollX, top: box.top + scrollY, w: box.width, h: box.height });   // where the list was, on the page
        return { was: was, list: list, now: now, origin: origin };
    }

    // Measured again on the next frame (a resize, a scroll), or played then (a change inside an item, which may have
    // grown): one measure for many changes, still before the change is painted.
    function later(root, moves) {
        due.set(root, due.get(root) || !!moves);
        if (frame) return;
        frame = requestAnimationFrame(function () {
            frame = 0;
            due.forEach(function (moving, r) { if (r.isConnected) { if (moving) play(r); else snapshot(r); } });
            due.clear();
        });
    }

    // What follows a list that grew or shrank (the rest of its card, the cards after it) glides from where it was by
    // the same amount, rather than jumping; out as far as an item of an enclosing list, as what follows that glides
    // with its list (which plays too).
    function inList(el) {
        for (let r = el.parentElement && el.parentElement.closest(ROOTS); r; r = r.parentElement && r.parentElement.closest(ROOTS)) {
            if (items(r).indexOf(el) >= 0) return true;
        }
        return false;
    }
    // Only what is laid out below it (not a pane beside it, in a row or a grid), drawn, and seen at some point of the
    // glide: one off the screen throughout has nothing to show, and a transform would only unstick a bar inside it.
    function below(s, box, by) {
        if (s.matches(COPY) || /fixed|absolute|sticky/.test(getComputedStyle(s).position)) return false;
        const r = s.getBoundingClientRect();
        if (Math.abs(by) > innerHeight || !r.width || !r.height || r.top < box.bottom - 1 || r.right <= box.left || r.left >= box.right) return false;
        return Math.min(r.top, r.top + by) < innerHeight && Math.max(r.bottom, r.bottom + by) > 0;
    }
    function slide(root, by, ms, wait) {
        const ease = window.tideShortcuts.token('--tide-ease-out');
        for (let n = root; n && n !== document.body && !inList(n); n = n.parentElement) {
            const box = n.getBoundingClientRect();
            for (let s = n.nextElementSibling; s; s = s.nextElementSibling) {
                if (!below(s, box, by)) continue;
                s.animate([{ transform: 'translateY(' + by + 'px)' }, { transform: 'none' }], { duration: ms, delay: wait, easing: ease, fill: 'backwards' });
            }
        }
    }

    // What follows a list that changed height glides with it.
    function resized(was, now, root, ms, wait) {
        const by = was - now;
        if (Math.abs(by) > 1) slide(root, by, ms, wait);
    }

    // The page's foot. Something getting shorter while the window is scrolled to the foot (a decided row, a group folded,
    // a filter, the reading pane settling a frame later) would cut the page short, and the browser would pull the window
    // back in a frame and push the sticky sidebar up. So on every change, before anything measures it, the page keeps
    // the height the window needs; a frame later, if the page did end higher, the window scrolls back smoothly to where
    // it now ends (followed frame by frame, as it may settle shorter still) and the page lets go as it arrives. The
    // reader scrolling meanwhile takes over from where the window is, the hold following them up. Not on going to
    // another page (it opens at its top), nor with reduced motion.
    let lastY = 0, pin = 0, rewind = null, footFrame = 0, footPath = location.pathname;
    let footMs = 0, footWait = 0;   // the glide this change started (play), which the window keeps time with
    let idle = 0;   // a look once the changes have gone quiet
    const HANDS = ['wheel', 'touchstart', 'mousedown', 'keydown'];   // the reader taking the scroll
    // Keys that scroll the page take it (Space only outside a field or a row, where it types or picks); the list's own
    // keys (J, K, the arrows on a row) only move a selection, which the ease then follows.
    const PAGE_KEYS = /^(PageUp|PageDown|Home|End)$/, SPACE = /^( |Spacebar)$/;
    function handed(done) {
        return function (e) {
            // A wheel the window can't go with (down at the foot, up at the top) moves nothing: the ease carries on.
            if (e.type === 'wheel' && ((e.deltaY > 0 && scrollY >= document.documentElement.scrollHeight - innerHeight - 1) || (e.deltaY < 0 && scrollY <= 0))) return;
            if (e.type === 'keydown' && !PAGE_KEYS.test(e.key) && !(SPACE.test(e.key) && !(e.target && e.target.closest &&
                e.target.closest('input, textarea, select, button, [contenteditable], [role="option"], [role="row"], li[tabindex]')))) return;
            done();
        };
    }
    function frameOf() { return document.querySelector('.page') || document.body; }
    function hold(h) { pin = h; frameOf().style.minHeight = h ? h + 'px' : ''; }
    // Holds just enough for the window to sit at top (the page's height may be decided by another column, so it is
    // topped up until there is room), keeps the window there, and returns where the page ends without the hold.
    // How far the page reaches: its own laid-out box (the page's frame), not the document's scroll height, which counts
    // rows still gliding up from below (their transforms) and would put the foot too low, letting the frame shrink under
    // the sticky sidebar.
    function reach() {
        const f = frameOf();
        return f === document.body ? document.documentElement.scrollHeight : f.getBoundingClientRect().bottom + scrollY;
    }
    function room(top) {
        const need = top + innerHeight;
        hold(0);
        const natural = reach();
        let h = need > natural + .5 ? need : 0;
        hold(h);
        for (let i = 0; h && i < 2; i++) {
            const short = need - reach();
            if (short <= .5) break;
            hold(h += short);
        }
        if (Math.abs(scrollY - top) > .5) window.scrollTo({ top: top, behavior: 'instant' });
        return Math.max(0, natural - innerHeight);
    }
    // A change is coming in: keep the room before anything is measured, and look a frame later.
    function guard() {
        const went = footPath !== location.pathname;
        footPath = location.pathname;
        if (idle) { clearTimeout(idle); idle = 0; }
        if (went) { if (rewind) rewind(); if (pin) hold(0); return; }
        if (!window.KeyframeEffect) return;
        if (!(lastY > 0)) return;
        if (pin < lastY + innerHeight) hold(lastY + innerHeight);
        if (rewind) return;   // the ease under way looks again each frame
        if (!footFrame) { footMs = footWait = 0; footFrame = requestAnimationFrame(settle); }
    }
    // A change can come in parts (the next row drawn taller a render before the decided one goes): while the page hasn't
    // ended higher, the hold stays on until the changes have been quiet a moment, then one more look lets it go.
    function settle(final) {
        footFrame = 0;
        const glide = footMs, wait = footWait;
        footMs = footWait = 0;
        if (rewind) return;
        // The reader scrolled meanwhile (wheel, touch, a page key): where they are now is where the window is, not where
        // it was before, so a later look never takes them back.
        if (performance.now() - handAt < 1500) lastY = scrollY;
        const ts = window.tideShortcuts, from = lastY, foot = room(from);
        if (!(from > foot + 1)) {
            if (final !== true && !document.hidden) {
                hold(from + innerHeight);
                idle = setTimeout(function () { idle = 0; settle(true); }, 300);
            }
            return;
        }
        if (!ts || ts.still() || document.hidden) { hold(0); lastY = scrollY; return; }
        glideWindow(from, foot, glide || ts.travel(from - foot), wait);
    }
    // The window easing to where it should be (the page's foot, or a row brought into view: aim), frame by frame, in
    // one ease (in step with the lists' glide when given its time and wait). Each frame covers its share of what is left
    // from wherever the window is. Restarted from there if the page settles a good deal shorter meanwhile (the reading
    // pane a moment after a group folds), so the rest isn't rushed at the ease's tail; stopped where it is if the page
    // grows back (a line added a render later), rather than chasing it back down.
    let aim = null;
    function glideWindow(from, to0, ms, delay) {
        const ts = window.tideShortcuts;
        const ease = function (t, d) {
            return new Animation(new KeyframeEffect(null, [], { duration: t, delay: d, easing: ts.token('--tide-ease-out'), fill: 'both' }), document.timeline);
        };
        const target = function (at) { const foot = room(at); return aim === null ? foot : Math.min(aim, foot); };
        let timing = ease(ms, delay), goal = to0, prev = 0, frame = 0;
        const done = function () {
            cancelAnimationFrame(frame); timing.cancel(); rewind = null; aim = null;
            HANDS.forEach(function (t) { removeEventListener(t, hand, true); });
            room(lastY = scrollY);
        }, hand = handed(done);
        const step = function () {
            const at = scrollY, to = target(at);
            if (to - goal > 8 && aim === null) { done(); return; }
            if (goal - to > 8) { timing.cancel(); timing = ease(ts.travel(Math.abs(at - to)), 0); timing.play(); prev = 0; }
            goal = to;
            const p = timing.effect.getComputedTiming().progress || 0, k = p >= 1 ? 1 : (p - prev) / (1 - prev);
            prev = p;
            room(lastY = Math.round(at + (to - at) * k));   // the window's own place, meanwhile
            if (timing.playState === 'finished') done(); else frame = requestAnimationFrame(step);
        };
        HANDS.forEach(function (t) { addEventListener(t, hand, { capture: true, passive: true }); });
        rewind = done;
        timing.play();
        frame = requestAnimationFrame(step);
    }
    // A row brought into view (rows.js) eases the window there rather than stepping it, so the rows gliding to their
    // places aren't jerked by the scroll (and an ease already under way takes it as its aim). With reduced motion it steps.
    function bring(dy) {
        const ts = window.tideShortcuts;
        if (!dy) return;
        if (!ts || ts.still() || !window.KeyframeEffect || document.hidden) { window.scrollBy({ top: dy, behavior: 'instant' }); lastY = scrollY; return; }
        aim = Math.max(0, scrollY + dy);   // measured from where the window is now (asked twice, it's the same place)
        if (rewind) return;
        glideWindow(scrollY, aim, ts.travel(Math.abs(dy)), 0);
    }
    // Where the window is, as the reader left it. A pull back because the page ended higher is not the reader's: the
    // window's place before it is kept. A hold still on follows the reader up (it is only a least height, so it never
    // stops them going down, and it isn't measured here: that would scroll the window again).
    // A hold left on after the reader took the window (wheel, touch, a page key mid-rewind) goes once they stop: the
    // window eases on from where they left it, a moment after their own scroll, as one settle rather than a second move
    // much later.
    let resume = 0;
    function scrolledWindow() {
        if (rewind) return;
        const y = scrollY;
        if (y < lastY && y >= document.documentElement.scrollHeight - innerHeight - 1) return;
        lastY = y;
        if (pin && y + innerHeight < pin) hold(y + innerHeight);
        if (pin) { clearTimeout(resume); resume = setTimeout(function () { resume = 0; if (pin && !rewind) { if (idle) { clearTimeout(idle); idle = 0; } settle(true); } }, 150); }
    }

    // What was last pressed (a Move button), for a list where people move items themselves: a button that went disabled
    // as its item reached the end has lost focus, so the item is still known to be the one moved, and focus goes on to
    // the item's other enabled button rather than the page.
    let pressed = null, pressedAt = 0, pointed = false;
    function noted(e) { pressed = e.target && e.target.closest ? e.target.closest('button, a, [tabindex]') : null; pressedAt = performance.now(); pointed = e.type === 'pointerdown'; }
    function keep(el) {
        const a = document.activeElement;
        if (!pressed || !pressed.isConnected || !el.contains(pressed) || (a && a !== document.body)) return;
        if (!pressed.disabled) { pressed.focus({ preventScroll: true }); return; }
        const other = pressed.parentElement && pressed.parentElement.querySelector('button:not([disabled])');
        if (other) other.focus({ preventScroll: true });
    }

    // Focus on a button that a change has just disabled (a Move button whose item reached the end, by this person or a
    // colleague) would drop to the page: it goes to the item's other enabled button.
    let focusedLast = null, handAt = 0;   // handAt: when the reader last scrolled or pressed a key   // what last had focus (the browser takes it off a button as it's disabled)
    function rescue() {
        const a = document.activeElement, f = a && a.disabled ? a : (!a || a === document.body) ? focusedLast : null;
        if (!f || !f.isConnected || !f.disabled || !f.closest || !f.closest('[data-tide-flip-carry]')) return;
        const other = f.parentElement && f.parentElement.querySelector('button:not([disabled])');
        if (other) other.focus({ preventScroll: true });
    }

    // A list that scrolls within a box of its own (triage's list beside its pane): rows going while the box is scrolled
    // down would cut it short and the browser would pull it back in a frame. As for the page's foot: the list keeps its
    // height as the change comes in (before anything measures it), and once it's played, the box scrolls back in step
    // with the glide before the list lets go. The box and how far it's scrolled are known from before (no reading).
    const boxes = new WeakMap(), tops = new WeakMap(), heldIn = new Map();
    function boxOf(root) {
        if (boxes.has(root)) return boxes.get(root);
        let b = null;
        for (let n = root.parentElement; n && n !== document.body && n !== document.documentElement; n = n.parentElement) {
            const y = getComputedStyle(n).overflowY;
            if (y === 'auto' || y === 'scroll') { b = n; tops.set(n, n.scrollTop); break; }
        }
        boxes.set(root, b);
        return b;
    }
    function holdIn(root) {
        const b = boxes.get(root), top = b ? tops.get(b) || 0 : 0, h = heights.get(root);
        if (!b || top <= 0 || !h || heldIn.has(root)) return;
        root.style.minHeight = h + 'px';
        const held = { box: b, top: top };
        heldIn.set(root, held);
        // A change that doesn't play the list (only measured again) lets go a frame later, without moving the box.
        requestAnimationFrame(function () { requestAnimationFrame(function () { if (heldIn.get(root) === held) unhold(root, true, 0, 0); }); });
    }
    // Where the box's end is once the list lets go: read before the glides go on (their transforms add to its scroll height).
    function heldEnd(root) {
        const held = heldIn.get(root);
        if (!held) return;
        const h = root.style.minHeight;
        root.style.minHeight = '';
        held.max = held.box.scrollHeight - held.box.clientHeight;
        root.style.minHeight = h;
        held.box.scrollTop = held.top;
    }
    function unhold(root, still, ms, wait) {
        const held = heldIn.get(root);
        if (!held) return;
        heldIn.delete(root);
        const b = held.box, h = root.style.minHeight, ts = window.tideShortcuts;
        root.style.minHeight = '';
        const max = held.max !== undefined ? held.max : b.scrollHeight - b.clientHeight;
        // In step with the glide, or, with nothing gliding, as long as its own way back needs.
        ms = Math.max(ms || 0, ts ? ts.travel(held.top - max) : 0);
        if (!(held.top > max + 1) || still || !window.KeyframeEffect) { b.scrollTop = Math.min(held.top, max); tops.set(b, b.scrollTop); return; }
        root.style.minHeight = h;
        b.scrollTop = held.top;
        const timing = new Animation(new KeyframeEffect(null, [], { duration: ms, delay: wait, easing: ts.token('--tide-ease-out'), fill: 'both' }), document.timeline);
        timing.play();
        (function step() {
            const p = timing.effect.getComputedTiming().progress || 0;
            b.scrollTop = Math.round(held.top + (max - held.top) * p);
            if (timing.playState === 'finished') { root.style.minHeight = ''; b.scrollTop = max; tops.set(b, max); return; }
            requestAnimationFrame(step);
        })();
    }

    // A box scrolled by so much over a time, eased as a flight is (so a card flying into a lane that scrolls lands as it
    // stops); at once without motion.
    function scrollIn(box, by, ms) {
        const ts = window.tideShortcuts, from = box.scrollTop;
        if (ts.still() || !window.KeyframeEffect) { box.scrollTop = from + by; return; }
        const timing = new Animation(new KeyframeEffect(null, [], { duration: ms, easing: ts.token('--tide-ease-out'), fill: 'both' }), document.timeline);
        timing.play();
        box.setAttribute('data-tide-scrolling', '');   // board.js leaves it be meanwhile
        (function step() {
            box.scrollTop = Math.round(from + by * (timing.effect.getComputedTiming().progress || 0));
            if (timing.playState !== 'finished') requestAnimationFrame(step); else box.removeAttribute('data-tide-scrolling');
        })();
    }

    function play(root, afterExit) {
        const tall = heights.get(root);
        const before = places.get(root);
        const ts = window.tideShortcuts, s = snapshot(root), quiet = ts.quiet(), o = s.origin;
        // The list itself moved on the page as the change came (a bar above it re-wrapping): its items glide from where
        // they were painted, not from where they were in the list, unless the list is being carried itself (a follower
        // slide on it or around it).
        let sx = 0, sy = 0;
        const after = places.get(root);
        if (before && after && !pinned.get(root) && (Math.abs(before.top - after.top) > MOVED || Math.abs(before.left - after.left) > MOVED)) {
            let carried = false;
            for (let a = root; a && a !== document.body && !carried; a = a.parentElement)
                carried = a.getAnimations().some(function (x) { return x.playState === 'running' && x.effect && x.effect.getKeyframes().some(function (k) { return k.transform && k.transform !== 'none'; }); });
            if (!carried) { sx = before.left - after.left; sy = before.top - after.top; }
        }
        heldEnd(root);
        if (!s.was && sized) sized.observe(root);
        const dur = ts.ms('--tide-dur-base'), ease = ts.token('--tide-ease-out'), fresh = [];   // dur: an entrance's
        let flights = 0, longest = 0;   // longest: the longest glide this change starts
        // A list mostly replaced (another tab, a filter, a page: most of what was there has gone, or most of what is there
        // is new) just shows: nothing glides, arrives or pushes what follows. A group's members opening out (they fold
        // back with data-tide-exit) are an unfolding, however many.
        let kept = 0, born = 0;
        if (s.was) s.list.forEach(function (el) {
            if (!s.now.get(key(el))) return;
            if (s.was.get(key(el))) kept++;
            else if (!el.hasAttribute('data-tide-exit')) born++;
        });
        // A list may say what it shows (data-tide-flip-key: the set of entries, as a filter or a page decides it): when that
        // changed, it's a different list, however many of the same items it has, and it just shows; the same set in a new
        // order (an entry moved) glides.
        const fk = root.getAttribute('data-tide-flip-key'), rekeyed = fk !== null && flipKeys.has(root) && flipKeys.get(root) !== fk;
        if (fk !== null) flipKeys.set(root, fk);
        // Items that already left on their own (data-tide-gone: rows this person decided, lifting out) don't make the list a
        // different one: the rest still glide to close the gap.
        const gone = goneFrom.get(root);
        goneFrom.delete(root);
        let had = s.was ? s.was.size : 0;
        if (gone && s.was) s.was.forEach(function (v, k) { if (gone.has(k)) had--; });
        const renewed = rekeyed || (!!s.was && ((had > 1 && kept * 2 < had) || born * 2 > s.now.size && (born > 1 || had > 0))), still = quiet || renewed;
        // One duration for every glide and unfold in this change, as long as its longest distance needs: items that move
        // together stay in step (each timed by its own distance, a row gliding up could cross one still unfolding). Less
        // than half a pixel is no move.
        let far = 0;
        if (s.was && !still) s.list.forEach(function (el) {
            const n = s.now.get(key(el)), w = s.was.get(key(el));
            if (!n || !w || w.parent !== n.parent) return;
            const d = Math.hypot(w.x - n.x + n.dx + sx, w.y - n.y + n.dy + sy);
            far = Math.max(far, d < MOVED ? 0 : d, n.h - w.h > 1 ? n.h - w.h : 0);
        });
        const together = ts.travel(far);
        // After an exit (a toast closed, a group folded), the rest wait until what left has faded, so none glides under it.
        // A card flying out to another lane (board): the ones closing up behind it wait a beat, so the one rising into its
        // place doesn't pass under it as it sets off.
        const left = new Set();   // the lanes a card flew out of
        if (s.was && !still) s.list.forEach(function (el) { const n = s.now.get(key(el)), w = s.was.get(key(el)); if (n && w && w.parent !== n.parent) left.add(w.parent); });
        // Rows this person decided lift out over the first half of the exit: the rest close up once they've faded (the
        // handover: one arriving where one leaves waits this long).
        const wait = afterExit && !quiet ? ts.ms('--tide-dur-exit') : gone && gone.size && !quiet ? ts.ms('--tide-dur-handover') : 0, behind = ts.ms('--tide-dur-fast');
        // A list pinned to the window's foot grows from its foot: held taller, it would push its items up.
        if (!still && s.was && !pinned.get(root)) {
            resized(tall, o.height, root, together, wait);
            // The window keeps time with a list that changed height (what shortens the page), not with one that only moved.
            if (far && Math.abs(tall - o.height) > 1) { footMs = Math.max(footMs, together); footWait = Math.max(footWait, wait); }
        }
        s.list.forEach(function (el) {
            const n = s.now.get(key(el)), w = s.was && s.was.get(key(el));
            if (!n) return;
            // A ghost is bringing it (board.js): it doesn't move or fade in by itself. .NET may draw it afresh in its new
            // lane before the ghost marks it, so the ghost's card is known by its id too (tideLanding).
            if (el.hasAttribute('data-tide-landing') || (window.tideLanding && el.dataset.id && window.tideLanding.has(el.dataset.id))) return;
            if (!w) { fresh.push(el); return; }
            if (still) return;
            // It grew (a row selected opens its decision, a card's Move menu opens): what's new unfolds from its old foot
            // as the items after it glide down, so they never slide across it. A clip, inset from the bottom by what it
            // grew, opened over the same time to its own foot (not past it, into the gap the next is gliding over), with
            // room at the other sides for its brackets.
            const grew = n.h - w.h;
            if (grew > 1) el.animate([{ clipPath: 'inset(-16px -16px ' + grew + 'px -16px)' }, { clipPath: 'inset(-16px -16px 0px -16px)' }], { duration: together, delay: wait, easing: ease, fill: 'backwards' });
            const dx = w.x - n.x + n.dx + sx, dy = w.y - n.y + n.dy + sy;
            if (Math.abs(dx) < MOVED && Math.abs(dy) < MOVED) {
                // Already where it's painted (a glide under way brought it there as the change came): the glide's offset
                // no longer applies (it would hop it away and back), so it stops, and the item stays where it's seen.
                const r = running.get(el);
                if (r && (Math.abs(n.dx) >= MOVED || Math.abs(n.dy) >= MOVED)) { r.cancel(); running.delete(el); }
                return;
            }
            const a = running.get(el);
            if (a) a.cancel();
            // Drawn in another container (another lane): it flies, as a glide would be clipped by its lane. A few at most;
            // the rest of a whole board redrawn are just there.
            if (w.parent !== n.parent) {
                if (flights++ < FLIGHTS) {
                    // A lane that scrolls (a short window) is brought to where the card lands first, so it's seen to land.
                    const lane = el.parentElement, lr = lane.getBoundingClientRect(), cr = { top: o.top + n.y, bottom: o.top + n.y + n.h };
                    const inView = cr.top >= lr.top && cr.bottom <= lr.bottom;
                    // As tall as the card is now (its Move menu, open as it was measured, is gone).
                    const from = { left: o.left + w.x, top: o.top + w.y, width: w.w, height: n.h };
                    let by = 0;
                    if (!inView) {
                        // As board.js shows a card (inside the lane's padding; from its top when it's taller than the lane),
                        // so the copy lands where the card stays; the lane scrolls there in step with the flight, not at once.
                        const pad = parseFloat(getComputedStyle(lane).paddingTop) || 0, top = cr.top - lr.top - pad, foot = cr.bottom - lr.bottom + pad;
                        by = Math.round(top < 0 || n.h > lr.height - 2 * pad ? top : foot);
                    }
                    const to = { left: o.left + n.x, top: o.top + n.y - by }, ms = ts.travel(Math.hypot(to.left - from.left, to.top - from.top));
                    if (by) scrollIn(lane, by, ms);
                    // Where it set off: the part of it its lane had scrolled out of view starts hidden; and the lane it left,
                    // scrolled down, brings the card now at its top in from its own top.
                    const old = w.parent && w.parent.isConnected && w.parent !== lane ? w.parent : null, ol = old && old.getBoundingClientRect();
                    const clip = ol ? { top: Math.max(0, ol.top - from.top), bottom: Math.max(0, from.top + from.height - ol.bottom) } : null;
                    if (old && old.scrollTop > 0) {
                        const pad = parseFloat(getComputedStyle(old).paddingTop) || 0;
                        const first = Array.prototype.find.call(old.querySelectorAll(root.dataset.tideFlip), function (c) { const r = c.getBoundingClientRect(); return r.height && r.bottom > ol.top + pad; });
                        if (first) { const ft = first.getBoundingClientRect().top - ol.top - pad; if (ft < -1) scrollIn(old, ft, ms); }
                    }
                    // And where it lands, the part its lane won't show (a card taller than a short lane) is hidden as it arrives.
                    const clipTo = { top: Math.max(0, lr.top - to.top), bottom: Math.max(0, to.top + n.h - lr.bottom) };
                    ts.fly(el, from, to, clip, clipTo.top || clipTo.bottom ? clipTo : null);
                }
                return;
            }
            const t = together;
            longest = t;
            const glide = el.animate([{ transform: 'translate(' + dx + 'px, ' + dy + 'px)' }, { transform: 'none' }], { duration: t, delay: left.has(n.parent) ? Math.max(wait, behind) : wait, easing: ease, fill: 'backwards' });
            running.set(el, glide);
            // In a list where people move items themselves (data-tide-flip-carry: the running order, bullets), the one they
            // moved (it has focus, on its Move button) is carried over the one it swaps with, not through it.
            if (root.hasAttribute('data-tide-flip-carry') && (el.contains(document.activeElement) || (pressed && performance.now() - pressedAt < 2000 && el.contains(pressed)))) {
                keep(el);
                el.setAttribute('data-tide-carried', '');
                // Carried out of the window (moved past its edge), the window eases after it rather than losing it.
                if (!pinned.get(root)) {
                    const head = parseFloat(getComputedStyle(document.documentElement).scrollPaddingTop) || 0, top = o.top + n.y, bottom = top + n.h;
                    let by = top < head ? top - head : bottom > innerHeight ? Math.min(bottom - innerHeight, top - head) : 0;
                    // Moved with the mouse, the window goes by as far as the item went, so the button pressed stays under
                    // the pointer for the next press; by keyboard, it shows the item whole.
                    if (by && pointed && pressed && el.contains(pressed) && w) by = n.y - w.y;
                    if (Math.abs(by) > 1) bring(by);
                }
                const down = function () { if (running.get(el) === glide) el.removeAttribute('data-tide-carried'); };
                glide.finished.then(down, down);
            }
        });
        // A list arriving whole comes in in quick order. In a list already there, new items (a group opened, a first
        // saved query above the preview) fade in once the items gliding past them have mostly gone by, so none slides
        // across them; a list mostly new with nothing gliding (another tab, a filter) just shows.
        unhold(root, still, together, wait);
        if (!fresh.length || document.hidden || !document.body.animate) return;
        if (afterExit && !quiet) {
            // One arriving where one is leaving (a toast after its Undo): it comes in as the other has mostly gone, so
            // the two never share a box; or, where the rest glide over after the exit, once they have mostly gone by.
            const late = longest ? wait + longest * .6 : ts.ms('--tide-dur-handover');
            fresh.forEach(function (el) { el.animate([{ opacity: 0 }, { opacity: 1 }], { duration: dur, delay: late, easing: ease, fill: 'backwards' }); });
        } else if (!s.was) {
            // Once per list per page: one drawn again on the same page (a table back from "no results") just shows.
            const id = root.dataset.tideFlip;
            if (entered.has(id)) return;
            entered.add(id);
            const stagger = ts.ms('--tide-stagger'), still = ts.still();
            fresh.slice(0, STAGGERED).forEach(function (el, i) {
                el.animate([{ opacity: 0 }, { opacity: 1 }], { duration: dur, delay: still ? 0 : i * stagger, easing: ease, fill: 'backwards' });
            });
        } else if (!renewed) {
            fresh.forEach(function (el) { el.animate([{ opacity: 0 }, { opacity: 1 }], { duration: dur, delay: longest * .6, easing: ease, fill: 'backwards' }); });
        }
    }

    // An item that left fades out where it was last measured, on a copy (the item itself is gone).
    function leave(root, el) {
        const ts = window.tideShortcuts, kept = at.get(root), p = kept && kept.get(key(el));
        if (!p || !document.body.animate || document.hidden) return;
        // Placed from where the list is; but a list that changed size as the item went (centred in its row, it moves too)
        // is placed from where it was when its items were measured (on the page, so a scroll since doesn't matter).
        const now = root.getBoundingClientRect(), was = places.get(root), moved = was && (Math.abs(was.w - now.width) > .5 || Math.abs(was.h - now.height) > .5);
        const o = pinned.get(root) ? { left: 0, top: 0 } : moved ? { left: was.left - scrollX, top: was.top - scrollY } : now;
        const g = ts.copy(el, { left: o.left + p.x, top: o.top + p.y, width: p.w, height: p.h });
        g.classList.add('tide-leaving');
        Object.assign(g.style, { position: 'fixed', margin: '0', zIndex: 'var(--tide-z-drop)', pointerEvents: 'none', listStyle: 'none' });
        // The copy keeps the look its list gave it (`.tide-checklist li`): it goes in a stand-in for the list, of no size
        // and drawing nothing itself, and not as the last item, so a rule for the last one doesn't take its border.
        const holder = document.createElement(root.tagName), after = document.createElement(el.tagName);
        holder.className = root.className;
        holder.setAttribute('data-tide-copy', '');
        holder.setAttribute('aria-hidden', 'true');
        Object.assign(holder.style, { position: 'fixed', left: '0', top: '0', width: '0', height: '0', margin: '0', padding: '0', border: '0',
            background: 'none', boxShadow: 'none', overflow: 'visible', display: 'block', pointerEvents: 'none', animation: 'none', transform: 'none' });
        after.style.display = 'none';
        holder.append(g, after);
        document.body.appendChild(holder);
        // Clear by two thirds of the way, so one coming in where it was (a toast after its undo) doesn't share its box: the
        // easing is the keyframes' own, so the fade does end at two thirds of the time (on the whole effect, it would
        // stretch the first stretch to nine tenths).
        const gone = function () { holder.remove(); };
        ts.drift(g, [{ opacity: 1, transform: 'none', easing: ts.token('--tide-ease-in') }, { opacity: 0, offset: .66 }, { opacity: 0, transform: 'translateY(' + (ts.still() ? 0 : 8) + 'px)' }],
            { duration: ts.ms('--tide-dur-exit') }).finished.then(gone, gone);
    }

    function elements(list) { return Array.prototype.filter.call(list, function (n) { return n.nodeType === 1 && !n.matches(COPY); }); }

    // A floating surface (a menu, a popover, the palette, a dialog's scrim) that .NET takes away as it closes fades out
    // where it was, quicker than it came, rather than vanishing in a frame. Where each open one is is noted once it has
    // opened, and again if the page scrolls under it.
    const SURFACES = '.tide-popover, .tide-modal-scrim, .tide-selbar, .tide-triage-selbar, .tide-hands-pop, .tide-reading', surfaceAt = new WeakMap(), open = new Set();
    // An in-flow surface (Triage's chosen-items bar on a narrow window, in the row) leaves no copy: what takes its place is
    // there at once, and a copy would be drawn over it.
    function note(el) { if (el.isConnected) surfaceAt.set(el, el.matches('.tide-triage-selbar') && getComputedStyle(el).position === 'static' ? null : el.getBoundingClientRect()); else open.delete(el); }
    function opened(n) {
        const found = n.matches(SURFACES) ? [n] : Array.prototype.slice.call(n.querySelectorAll(SURFACES));
        found.forEach(function (el) {
            open.add(el);
            requestAnimationFrame(function () { requestAnimationFrame(function () {
                note(el);
                // And again once it has come in (its slide or unfold done), so its copy goes from where it rests.
                if (el.getAnimations) Promise.all(el.getAnimations().map(function (a) { return a.finished; })).then(function () { note(el); }, function () { });
            }); });
        });
    }
    function closed(n) {
        const ts = window.tideShortcuts, found = n.matches(SURFACES) ? [n] : Array.prototype.slice.call(n.querySelectorAll(SURFACES));
        found.forEach(function (el) {
            open.delete(el);
            const r = el.hasAttribute('data-tide-gone') ? null : surfaceAt.get(el);   // gone already (faded as its items left)
            if (!r || !r.width || document.hidden || !document.body.animate) return;
            const g = ts.copy(el, r);
            g.classList.add('tide-surface-leaving');
            ts.drift(g, [{ opacity: 1 }, { opacity: 0 }], { duration: ts.ms('--tide-dur-exit'), easing: ts.token('--tide-ease-in') });
        });
    }

    // A count that changed is told what it replaced (data-was), so it rolls like an odometer (tide-roll: the old number
    // goes out as the new one comes in, up when it grew, down when it fell, data-dir) or ticks (a short text). Blazor
    // swaps the element, keyed by its value, in the same place: a tab's count (TabBar), or anything marked data-tide-tick
    // (the Odometer component; Today's step details).
    const TICKS = '.tide-tabbar-count, [data-tide-tick]';
    function odometer(at, added, gone) {
        const was = gone.get(at);
        if (was === undefined) return;
        added.forEach(function (n) {
            const el = n.matches('[data-tide-tick]') ? n : n.querySelector('.tide-roll');
            if (!el || el.textContent.trim() === was) return;
            el.dataset.was = was;
            if (parseFloat(el.textContent) < parseFloat(was)) el.dataset.dir = 'down';
        });
    }

    function changed(records) {
        guard();
        records.forEach(function (m) { const r = m.target.closest && m.target.closest(ROOTS); if (r) holdIn(r); });
        // Blazor puts focus back on a button it moved or redrew (a Move button, the promote form's button as the form goes)
        // without preventScroll, and the window jumps to it mid-change: it's put back where the reader had it (what
        // follows brings anything into view smoothly). A button only: a field typed in scrolls the window itself.
        const a = document.activeElement, f = a && a !== document.body ? a : focusedLast;   // (focused, then taken away with its form)
        if (!rewind && f && f.tagName === 'BUTTON' && Math.abs(scrollY - lastY) > 1 && performance.now() - handAt > 300)   // not the reader's own scroll
            window.scrollTo({ top: lastY, behavior: 'instant' });
        // Another page (whether or not the one between had lists): its lists come in afresh.
        if (part() !== page) { entered.clear(); page = part(); }
        const touched = new Set(), gone = new Map(), exited = new Set();
        records.forEach(function (m) {
            elements(m.removedNodes).forEach(function (n) { if (n.matches(TICKS)) gone.set(m.target, n.textContent.trim()); closed(n); });
            elements(m.addedNodes).forEach(opened);
        });
        records.forEach(function (m) {
            // Text changed in place (a range's label re-wrapping): what it pushed glides, as for a change inside an item.
            if (m.type === 'characterData') {
                const at = m.target.parentElement, r = at && !at.closest(COPY) && at.closest(ROOTS);
                if (r) later(r, true);
                return;
            }
            if (m.target.closest && m.target.closest(COPY)) return;   // a copy flying or leaving is no list's
            const added = elements(m.addedNodes), removed = elements(m.removedNodes);
            if (!added.length && !removed.length) return;
            if (added.length && gone.size) odometer(m.target, added, gone);
            // A list that just arrived, or one inside what arrived.
            added.forEach(function (n) {
                if (n.matches(ROOTS)) touched.add(n);
                n.querySelectorAll(ROOTS).forEach(function (r) { touched.add(r); });
            });
            const root = m.target.closest(ROOTS);
            if (!root) return;
            // Inside one of its items (a card's Move menu opening, a row's text): the item may have grown, so the ones
            // after it glide to where they are now rather than jump, played on the next frame with any others.
            if (m.target.closest(root.dataset.tideFlip)) { later(root, true); return; }
            touched.add(root);
            // Items that leave fade out where they were: every one in a list marked data-tide-flip-exit, or one that asks
            // itself (data-tide-exit: a group's reports folding away), unless something else already took it out
            // (data-tide-gone: a row this person decided lifts out on its own).
            const exits = root.hasAttribute('data-tide-flip-exit');
            removed.forEach(function (n) {
                const its = n.matches(root.dataset.tideFlip) ? [n] : exits ? Array.prototype.slice.call(n.querySelectorAll(root.dataset.tideFlip)) : [];   // (in a keyed slot)
                its.forEach(function (i) {
                    if (i.hasAttribute('data-tide-gone')) { (goneFrom.get(root) || goneFrom.set(root, new Set()).get(root)).add(key(i)); return; }
                    if (!exits && !i.hasAttribute('data-tide-exit')) return;
                    if (i.isConnected) return;   // taken out and put back in the same change (moved up or down): it glides, at once
                    exited.add(root);
                    leave(root, i);
                });
            });
        });
        // A list inside another's item (the bullets, inside the editor's sections): the outer list plays too, so what's
        // after the item that grew or shrank glides rather than jumps.
        // It waits for an exit inside it, as the inner one does.
        touched.forEach(function (root) {
            const out = exited.has(root);
            for (let up = root.parentElement && root.parentElement.closest(ROOTS); up; up = up.parentElement && up.parentElement.closest(ROOTS)) {
                touched.add(up);
                if (out) exited.add(up);
            }
        });
        touched.forEach(function (root) { if (root.isConnected) { due.delete(root); play(root, exited.has(root)); } });
        rescue();
    }

    // A lane inside a list scrolled: its items are somewhere else in the list now. (The window scrolling moves a list
    // and its items together, which changes nothing.)
    function scrolled(e) {
        if (e.target && e.target.nodeType === 1) tops.set(e.target, e.target.scrollTop);
        open.forEach(note);
        const root = e.target !== document && e.target.closest ? e.target.closest(ROOTS) : null;
        if (root && root !== e.target) later(root);
        // Lists inside a box that scrolled (Triage's list in its own scroller) are somewhere else on the page now.
        if (e.target !== document && e.target.querySelectorAll) e.target.querySelectorAll(ROOTS).forEach(function (r) { later(r); });
    }
    // An item's own motion ended (a toast's entrance), or a ghost line closed up: measured where it rests.
    function ended(e) {
        if (e.type === 'transitionend' && !/^(transform|translate|rotate|scale)$/.test(e.propertyName)) return;   // a hover's color
        if (open.has(e.target)) note(e.target);   // a surface that rose in is where it rests now (its copy goes from there)
        const t = e.target, root = t.closest ? t.closest(ROOTS) : null;
        if (root && root !== t && (t.matches(root.dataset.tideFlip) || t.classList.contains('tide-live-ghost'))) later(root);
    }

    function start() {
        if (!window.MutationObserver) return;
        new MutationObserver(changed).observe(document.body, { childList: true, characterData: true, subtree: true });
        document.addEventListener('scroll', scrolled, { capture: true, passive: true });
        document.addEventListener('pointerdown', noted, true);
        ['wheel', 'touchmove'].forEach(function (t) { addEventListener(t, function () { handAt = performance.now(); }, { capture: true, passive: true }); });
        addEventListener('keydown', function (e) {   // a key that scrolls the page itself (not one a list or a form takes)
            if (/^(PageUp|PageDown|Home|End| |ArrowUp|ArrowDown)$/.test(e.key) && (e.target === document.body || e.target === document.documentElement)) handAt = performance.now();
        }, { capture: true, passive: true });
        document.addEventListener('focusin', function (e) { focusedLast = e.target; }, true);
        document.addEventListener('keydown', noted, true);
        lastY = scrollY;
        if (window.KeyframeEffect) addEventListener('scroll', scrolledWindow, { passive: true });
        document.addEventListener('transitionend', ended, true);
        document.addEventListener('animationend', ended, true);
        document.querySelectorAll(ROOTS).forEach(function (root) { snapshot(root); if (sized) sized.observe(root); });
        opened(document.body);   // surfaces already open as the page loads (the reading pane, remembered open)
    }
    if (document.body) start(); else document.addEventListener('DOMContentLoaded', start);
    // Something about to put the window somewhere itself (the reading pane brought into view) ends an ease under way.
    function stop() { if (rewind) rewind(); }
    return { bring: bring, stop: stop };
})();
