// Things in a list move rather than jump (FLIP). A list marked data-tide-flip="<its items' selector>" is watched: when
// its items change (one added, removed or moved, by .NET or by a script), each item still there glides from where it
// was to where it is now, so the rows below a new one make room, the gap a promoted row left closes, a board card moved
// with its Move menu travels to its new lane, and the cards in a lane slide aside for the slot a held card would drop
// into. A list that arrives with several items at once (a page or a filter loaded) shows them in quick order. With
// data-tide-flip-exit, an item that leaves fades out where it was (toasts). With reduced motion, nothing moves: new
// items still fade in, and leaving ones fade out.
//
// Where each item was is where layout put it (offsets, which a transform playing on it doesn't change), taken after each
// change and whenever the list changes size (a row that opens, the window resized), so it is current for the next one.
window.tideFlip = (function () {
    const ROOTS = '[data-tide-flip]', STAGGERED = 8;
    const at = new WeakMap(), rects = new WeakMap(), running = new WeakMap(), known = new WeakSet();
    const sized = window.ResizeObserver ? new ResizeObserver(function (es) { es.forEach(function (e) { remember(e.target); }); }) : null;

    // Where layout put it on the page, whatever transform is playing on it; null while it isn't shown.
    function place(el) {
        if (!el.offsetParent) return null;
        let x = 0, y = 0;
        for (let n = el; n; n = n.offsetParent) { x += n.offsetLeft; y += n.offsetTop; }
        return { x: x, y: y };
    }

    function items(root) { return root.querySelectorAll(root.dataset.tideFlip); }

    function remember(root) {
        const exits = root.hasAttribute('data-tide-flip-exit');
        items(root).forEach(function (el) {
            const p = place(el);
            if (!p) return;
            at.set(el, p);
            if (exits) rects.set(el, el.getBoundingClientRect());
        });
    }

    // Its transform now, from a glide still playing: the next glide starts from where it is seen, not from its end.
    function stop(el) {
        const a = running.get(el);
        if (!a || a.playState !== 'running') return { x: 0, y: 0 };
        const m = new DOMMatrixReadOnly(getComputedStyle(el).transform);
        a.cancel();
        return { x: m.m41, y: m.m42 };
    }

    function play(root) {
        const ts = window.tideShortcuts, still = ts.still(), first = !known.has(root);
        const dur = ts.ms('--tide-dur-base'), ease = ts.token('--tide-ease-out'), fresh = [];
        known.add(root);
        if (sized) sized.observe(root);
        items(root).forEach(function (el) {
            const now = place(el);
            if (!now) return;
            const was = at.get(el);
            if (!was) { fresh.push(el); return; }
            if (still || !el.animate) return;
            const seen = stop(el), dx = was.x - now.x + seen.x, dy = was.y - now.y + seen.y;
            if (!dx && !dy) return;
            running.set(el, el.animate([{ transform: 'translate(' + dx + 'px, ' + dy + 'px)' }, { transform: 'none' }], { duration: dur, easing: ease }));
        });
        // A list arriving whole (or a page of it) comes in in quick order; one or two new items have their own entrance.
        if ((first || fresh.length > 2) && fresh.length && fresh[0].animate) {
            const stagger = ts.ms('--tide-stagger');
            fresh.slice(0, STAGGERED).forEach(function (el, i) {
                el.animate([{ opacity: 0 }, { opacity: 1 }], { duration: ts.ms('--tide-dur-base'), delay: still ? 0 : i * stagger, easing: ease, fill: 'backwards' });
            });
        }
        remember(root);
    }

    // An item that left fades out where it was, on a copy (the item itself is gone).
    function leave(el) {
        const r = rects.get(el), ts = window.tideShortcuts;
        if (!r || !r.width || !document.body.animate) return;
        const g = ts.copy(el);
        g.classList.add('tide-leaving');
        g.style.left = r.left + 'px';
        g.style.top = r.top + 'px';
        g.style.width = r.width + 'px';
        g.style.height = r.height + 'px';
        document.body.appendChild(g);
        const drop = ts.still() ? 0 : 8;
        g.animate([{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'translateY(' + drop + 'px)' }],
            { duration: ts.ms('--tide-dur-base') * .7, easing: ts.token('--tide-ease-in'), fill: 'forwards' })
            .finished.then(function () { g.remove(); }, function () { g.remove(); });
    }

    function changed(records) {
        const roots = Array.prototype.slice.call(document.querySelectorAll(ROOTS));
        if (!roots.length) return;
        roots.forEach(function (root) {
            if (known.has(root) && !records.some(function (m) { return root.contains(m.target); })) return;
            if (root.hasAttribute('data-tide-flip-exit')) {
                const sel = root.dataset.tideFlip;
                records.forEach(function (m) {
                    if (!root.contains(m.target)) return;
                    m.removedNodes.forEach(function (n) { if (n.nodeType === 1 && n.matches(sel)) leave(n); });
                });
            }
            play(root);
        });
    }

    function start() {
        if (!window.MutationObserver) return;
        new MutationObserver(changed).observe(document.body, { childList: true, subtree: true });
        document.querySelectorAll(ROOTS).forEach(function (root) { known.add(root); remember(root); if (sized) sized.observe(root); });
    }
    if (document.body) start(); else document.addEventListener('DOMContentLoaded', start);

    // A script that showed or hid an item without adding or removing one (a class): where things are is taken again.
    return { remember: function (root) { if (root) remember(root); } };
})();
