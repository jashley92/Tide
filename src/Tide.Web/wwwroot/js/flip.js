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
// Where each item was is kept as the window sees it without any glide playing on it, after each change, after a change
// inside an item (a row that grew), when the list changes size, and when it or a lane in it scrolls.
window.tideFlip = (function () {
    const ROOTS = '[data-tide-flip]', STAGGERED = 8, COPY = '[data-tide-copy]';
    const at = new WeakMap(), rects = new WeakMap(), running = new WeakMap(), known = new WeakSet(), due = new Set();
    let frame = 0;
    const sized = window.ResizeObserver ? new ResizeObserver(function (es) {
        es.forEach(function (e) { if (e.target.isConnected) later(e.target); else sized.unobserve(e.target); });
    }) : null;

    function items(root) { return Array.prototype.slice.call(root.querySelectorAll(root.dataset.tideFlip)); }
    function key(el) { return el.dataset.id || el; }
    function positions(root) { let m = at.get(root); if (!m) { m = new Map(); at.set(root, m); } return m; }

    // Where the window shows it, less any glide playing on it (that's motion, not its place), and the list it's in;
    // null while it isn't shown.
    function measure(el) {
        const r = el.getBoundingClientRect();
        if (!r.width && !r.height) return null;
        let dx = 0, dy = 0;
        const a = running.get(el);
        if (a && a.playState === 'running') { const m = new DOMMatrixReadOnly(getComputedStyle(el).transform); dx = m.m41; dy = m.m42; }
        return { x: r.left - dx, y: r.top - dy, w: r.width, h: r.height, seen: { x: dx, y: dy }, el: el, parent: el.parentElement };
    }

    function remember(root) {
        const m = positions(root), list = items(root), now = list.map(measure);
        m.clear();
        list.forEach(function (el, i) { if (now[i]) m.set(key(el), now[i]); });
        if (root.hasAttribute('data-tide-flip-exit')) list.forEach(function (el) { rects.set(el, el.getBoundingClientRect()); });
    }

    // Measured again on the next frame (a change inside an item, a resize, a scroll): one measure for many changes.
    function later(root) {
        due.add(root);
        if (frame) return;
        frame = requestAnimationFrame(function () {
            frame = 0;
            due.forEach(function (r) { if (r.isConnected) remember(r); });
            due.clear();
        });
    }

    // A card .NET drew in another lane flies there on a copy laid over the page, the card itself shown as it lands (it's
    // only transparent meanwhile, so focus can go to it: the Move menu sends focus there at once).
    function fly(el, was, now, opts) {
        const ts = window.tideShortcuts;
        const g = ts.copy(el, { left: was.x, top: was.y, width: was.w, height: was.h });
        g.classList.add('tide-flying');
        el.style.opacity = '0';
        const done = function () { g.remove(); el.style.opacity = ''; };
        g.animate([{ transform: 'none' }, { transform: 'translate(' + (now.x - was.x) + 'px, ' + (now.y - was.y) + 'px)' }],
            { duration: opts.far, easing: opts.ease, fill: 'forwards' }).finished.then(done, done);
    }

    function play(root) {
        const ts = window.tideShortcuts, first = !known.has(root), quiet = ts.still() || document.hidden || !document.body.animate;
        const opts = { dur: ts.ms('--tide-dur-base'), far: ts.ms('--tide-dur-panel'), ease: ts.token('--tide-ease-out') };
        if (first) { known.add(root); if (sized) sized.observe(root); }
        // Read everything, then write: one layout for the whole list.
        const m = positions(root), list = items(root), now = list.map(measure);
        const was = new Map(m), fresh = [];
        m.clear();
        list.forEach(function (el, i) { if (now[i]) m.set(key(el), now[i]); });
        list.forEach(function (el, i) {
            const n = now[i];
            if (!n) return;
            const w = was.get(key(el));
            if (!w) { fresh.push(el); return; }
            if (quiet) return;
            const dx = w.x - n.x + n.seen.x, dy = w.y - n.y + n.seen.y;
            if (!dx && !dy) return;
            const a = running.get(el);
            if (a) a.cancel();
            // A new element for a known card, in another container: it flies, as a glide would be clipped by its lane.
            if (w.el !== el && w.parent !== n.parent) { fly(el, w, n, opts); return; }
            running.set(el, el.animate([{ transform: 'translate(' + dx + 'px, ' + dy + 'px)' }, { transform: 'none' }], { duration: opts.dur, easing: opts.ease }));
        });
        // A list arriving whole (or a page of it) comes in in quick order; one or two new items have their own entrance.
        if ((first || fresh.length > 2) && fresh.length && !document.hidden && document.body.animate) {
            const stagger = ts.ms('--tide-stagger'), still = ts.still();
            fresh.slice(0, STAGGERED).forEach(function (el, i) {
                el.animate([{ opacity: 0 }, { opacity: 1 }], { duration: opts.dur, delay: still ? 0 : i * stagger, easing: opts.ease, fill: 'backwards' });
            });
        }
        if (root.hasAttribute('data-tide-flip-exit')) list.forEach(function (el) { rects.set(el, el.getBoundingClientRect()); });
    }

    // An item that left fades out where it was, on a copy (the item itself is gone).
    function leave(el) {
        const r = rects.get(el), ts = window.tideShortcuts;
        if (!r || !r.width || !document.body.animate || document.hidden) return;
        const g = ts.copy(el, r);
        g.classList.add('tide-leaving');
        const drop = ts.still() ? 0 : 8;
        g.animate([{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'translateY(' + drop + 'px)' }],
            { duration: ts.ms('--tide-dur-exit'), easing: ts.token('--tide-ease-in'), fill: 'forwards' })
            .finished.then(function () { g.remove(); }, function () { g.remove(); });
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
            if (root.hasAttribute('data-tide-flip-exit')) removed.forEach(function (n) { if (n.matches(root.dataset.tideFlip)) leave(n); });
        });
        touched.forEach(function (root) { if (root.isConnected) { due.delete(root); play(root); } });
    }

    // The window, or a lane in a list, scrolled: its items are somewhere else in the window now.
    function scrolled(e) {
        const root = e.target === document ? null : e.target.closest && e.target.closest(ROOTS);
        if (root) later(root);
        else if (e.target === document) document.querySelectorAll(ROOTS).forEach(later);
    }

    function start() {
        if (!window.MutationObserver) return;
        new MutationObserver(changed).observe(document.body, { childList: true, subtree: true });
        document.addEventListener('scroll', scrolled, { capture: true, passive: true });
        document.querySelectorAll(ROOTS).forEach(function (root) { known.add(root); remember(root); if (sized) sized.observe(root); });
    }
    if (document.body) start(); else document.addEventListener('DOMContentLoaded', start);
})();
