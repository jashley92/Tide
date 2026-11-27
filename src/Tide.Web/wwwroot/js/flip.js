// Things in a list move rather than jump (FLIP). A list marked data-tide-flip="<its items' selector>" is watched: when
// its items change (one added, removed or moved, by .NET or by a script), each item still there glides from where it
// was to where it is now, so the rows below a new one make room, the gap a promoted row left closes, and the cards in a
// lane slide aside for the slot a held card would drop into. An item is known by its data-id where it has one, so a
// board card that .NET draws afresh in another lane (its Move menu, someone else's move, a drop) is the same card: it
// flies there from where it was, over the page, as lanes clip what's outside them. A list that arrives with several
// items at once (a page or a filter loaded) shows them in quick order. With data-tide-flip-exit, an item that leaves
// fades out where it was (toasts). With reduced motion, or in a tab nobody is looking at, nothing moves: new items still
// fade in, and leaving ones fade out.
//
// Each change is measured first and then played, so the page is laid out once for all the items, not once per item.
// Where each item was is kept relative to its list (so scrolling the window changes nothing) and without any glide
// playing on it: after each change, after a change inside an item (a row that grew), when the list changes size, and
// when a lane inside it scrolls.
window.tideFlip = (function () {
    const ROOTS = '[data-tide-flip]', STAGGERED = 8, FLIGHTS = 8, COPY = '[data-tide-copy]';
    const at = new WeakMap(), running = new WeakMap(), due = new Set();
    let frame = 0;
    const sized = window.ResizeObserver ? new ResizeObserver(function (es) {
        es.forEach(function (e) { if (e.target.isConnected) later(e.target); else sized.unobserve(e.target); });
    }) : null;

    function items(root) { return Array.prototype.slice.call(root.querySelectorAll(root.dataset.tideFlip)); }
    function key(el) { return el.dataset.id || el; }

    // Where it is in its list, less any glide playing on it (that's motion, not its place), the container it's in, and
    // the glide's offset now; null while it isn't shown.
    function measure(el, origin) {
        const r = el.getBoundingClientRect();
        if (!r.width && !r.height) return null;
        let dx = 0, dy = 0;
        const a = running.get(el);
        if (a && a.playState === 'running') { const m = new DOMMatrixReadOnly(getComputedStyle(el).transform); dx = m.m41; dy = m.m42; }
        return { x: r.left - dx - origin.left, y: r.top - dy - origin.top, w: r.width, h: r.height, dx: dx, dy: dy, parent: el.parentElement };
    }

    // Measures the list's items and keeps where each is, giving back where they were before (null the first time).
    function snapshot(root) {
        const was = at.get(root) || null, origin = root.getBoundingClientRect(), list = items(root), now = new Map();
        list.forEach(function (el) { const p = measure(el, origin); if (p) now.set(key(el), p); });
        at.set(root, now);
        return { was: was, list: list, now: now, origin: origin };
    }

    // Measured again on the next frame (a change inside an item, a resize, a scroll): one measure for many changes.
    function later(root) {
        due.add(root);
        if (frame) return;
        frame = requestAnimationFrame(function () {
            frame = 0;
            due.forEach(function (r) { if (r.isConnected) snapshot(r); });
            due.clear();
        });
    }

    function play(root) {
        const ts = window.tideShortcuts, s = snapshot(root), quiet = ts.quiet(), o = s.origin;
        if (!s.was && sized) sized.observe(root);
        const dur = ts.ms('--tide-dur-base'), ease = ts.token('--tide-ease-out'), fresh = [];
        let flights = 0;
        s.list.forEach(function (el) {
            const n = s.now.get(key(el)), w = s.was && s.was.get(key(el));
            if (!n) return;
            if (!w) { fresh.push(el); return; }
            if (quiet) return;
            const dx = w.x - n.x + n.dx, dy = w.y - n.y + n.dy;
            if (!dx && !dy) return;
            const a = running.get(el);
            if (a) a.cancel();
            // Drawn in another container (another lane): it flies, as a glide would be clipped by its lane. A few at most;
            // the rest of a whole board redrawn are just there.
            if (w.parent !== n.parent) {
                if (flights++ < FLIGHTS) ts.fly(el, { left: o.left + w.x, top: o.top + w.y, width: w.w, height: w.h }, ts.ms('--tide-dur-panel'), { left: o.left + n.x, top: o.top + n.y });
                return;
            }
            running.set(el, el.animate([{ transform: 'translate(' + dx + 'px, ' + dy + 'px)' }, { transform: 'none' }], { duration: dur, easing: ease }));
        });
        // A list arriving whole (or a page of it) comes in in quick order; one or two new items have their own entrance.
        if ((!s.was || fresh.length > 2) && fresh.length && !document.hidden && document.body.animate) {
            const stagger = ts.ms('--tide-stagger'), still = ts.still();
            fresh.slice(0, STAGGERED).forEach(function (el, i) {
                el.animate([{ opacity: 0 }, { opacity: 1 }], { duration: dur, delay: still ? 0 : i * stagger, easing: ease, fill: 'backwards' });
            });
        }
    }

    // An item that left fades out where it was last measured, on a copy (the item itself is gone).
    function leave(root, el) {
        const ts = window.tideShortcuts, kept = at.get(root), p = kept && kept.get(key(el));
        if (!p || !document.body.animate || document.hidden) return;
        const o = root.getBoundingClientRect(), g = ts.copy(el, { left: o.left + p.x, top: o.top + p.y, width: p.w, height: p.h });
        g.classList.add('tide-leaving');
        ts.drift(g, [{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'translateY(' + (ts.still() ? 0 : 8) + 'px)' }],
            { duration: ts.ms('--tide-dur-exit'), easing: ts.token('--tide-ease-in') });
    }

    function elements(list) { return Array.prototype.filter.call(list, function (n) { return n.nodeType === 1 && !n.matches(COPY); }); }

    // A tab's count that rolls (tide-roll) is told the number it replaced, which rolls up and out as the new one comes in.
    // (Blazor swaps the count element, keyed by its number, inside the same tab.)
    function odometer(tab, added, gone) {
        const was = gone.get(tab);
        if (!was) return;
        added.forEach(function (n) { const roll = n.querySelector('.tide-roll'); if (roll) roll.dataset.was = was; });
    }

    function changed(records) {
        const touched = new Set(), gone = new Map();
        records.forEach(function (m) {
            elements(m.removedNodes).forEach(function (n) { if (n.classList.contains('tide-tabbar-count')) gone.set(m.target, n.textContent.trim()); });
        });
        records.forEach(function (m) {
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
            // Inside one of its items (a menu opening, a row's text): nothing moves now, but where the items are may have.
            if (m.target.closest(root.dataset.tideFlip)) { later(root); return; }
            touched.add(root);
            if (root.hasAttribute('data-tide-flip-exit')) removed.forEach(function (n) { if (n.matches(root.dataset.tideFlip)) leave(root, n); });
        });
        touched.forEach(function (root) { if (root.isConnected) { due.delete(root); play(root); } });
    }

    // A lane inside a list scrolled: its items are somewhere else in the list now. (The window scrolling moves a list
    // and its items together, which changes nothing.)
    function scrolled(e) {
        const root = e.target !== document && e.target.closest ? e.target.closest(ROOTS) : null;
        if (root && root !== e.target) later(root);
    }

    function start() {
        if (!window.MutationObserver) return;
        new MutationObserver(changed).observe(document.body, { childList: true, subtree: true });
        document.addEventListener('scroll', scrolled, { capture: true, passive: true });
        document.querySelectorAll(ROOTS).forEach(function (root) { snapshot(root); if (sized) sized.observe(root); });
    }
    if (document.body) start(); else document.addEventListener('DOMContentLoaded', start);
})();
