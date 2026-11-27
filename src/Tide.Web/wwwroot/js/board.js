// Actions board: drag a card to another lane, or onto a chip on the closed strip, to move the action there. CSP-safe (an
// external file, no inline handlers). The drop goes to .NET, which checks and makes the move (ActionService.MoveAsync);
// only cards the person may move are draggable. Each card also has a Move menu, the way to do this from the keyboard.
// The drag is by pointer events (mouse, pen and touch alike), not the browser's drag and drop, so the card can be lifted,
// follow the pointer and settle where it lands: a mouse or pen starts it by moving a few pixels with the button down, a finger by
// holding still a moment first (so a swipe still scrolls the page). The held card is a copy (the ghost). Where it would
// land, a dashed slot opens and the cards there slide aside to make room (flip.js moves them); the card leaves its own
// place, which closes up. In the team's own order (the board's data-sort is "manual") the slot follows the pointer, up
// and down a lane and into another, and the drop puts the card there (ActionService.PlaceAsync). Sorted by due date or
// priority, the slot opens at the card's place in that order (ActionKanban.LaneOrder) in another lane, and over its own
// lane the card is just the gap it left. Esc cancels; a press that isn't a drag is a click.
// While a card is held, .NET hears when the drag starts, each time it crosses into another column, and when it ends (a
// few calls a drag, not the pointer's path), so everyone else's board can show who is moving it and where (BoardDrags).
// A card that moves for any other reason (its Move menu, someone else's move) travels from where it was (flip.js).
// The Move menu, from the keyboard: opening it focuses its first option (focusMenu), the arrows and Home/End move
// between its options, and Esc gives focus back to the Move button (.NET closes the menu). After a move from it, focus
// goes to the card's Move button in its new lane, or when it has left the board (closed), to the card that was after it
// in its lane, else the one before, else the lane's heading (focusPlace). A card dragged does the same once its drop is
// done, and one put back (Esc, or let go where it was) gets focus where it is, so J and K carry on from it.
// The drag's state is kept in data-tide-* attributes on the card, which .NET never draws: a card drawn again mid-drag
// (someone else changed it) keeps them.
window.tideBoard = (function () {
    let ref = null, root = null, dragging = null, over = null, marked = null, press = null, ghost = null, slot = null;
    let drop = null, home = null, lastInside = null;   // where a drop would put the card ({ status, before }), the card it was above at first, the lane last under it
    let mids = null;   // the lane under the pointer's cards and where each one's middle is laid out, until a slot moves or a lane scrolls
    const SLOP = 6, HOLD = 280, ASKING = 400;   // px before a press is a drag; ms a finger holds first; ms before .NET counts as asking

    function tell(method) {
        if (!ref) return;
        const args = Array.prototype.slice.call(arguments, 1);
        ref.invokeMethodAsync.apply(ref, [method].concat(args)).catch(function () { });
    }

    // Says which column the held card is over, only when that changes: null while over its own column or none.
    function report(status) {
        if (!dragging || status === over) return;
        over = status;
        tell('OnDragging', dragging.dataset.id, status);
    }

    function mark(col) {
        root.querySelectorAll('[data-tide-over]').forEach(function (c) { if (c !== col) c.removeAttribute('data-tide-over'); });
        if (col) col.setAttribute('data-tide-over', '');
    }

    function end() {
        if (dragging) {
            dragging.removeAttribute('data-tide-drag');
            tell('OnDragEnded');
        }
        dragging = null;
        over = null;
        marked = null;
        drop = null;
        home = null;
        lastInside = null;
        mids = null;
        if (root) mark(null);
    }

    function manual() { return !!root && root.dataset.sort === 'manual'; }

    // The card a drop at pointer height y in this lane would go above (in the team's own order), or at its place by due
    // date or priority; null at the end.
    function above(lane, cards, card, y) {
        // Where each card is laid out, not where a glide making room has it this frame, so the slot doesn't flicker
        // back and forth as the cards move. Measured once until the slot moves (which lays the lane out again) or
        // something scrolls, not on every move of the pointer.
        if (manual()) {
            if (!mids || mids.lane !== lane) mids = {
                lane: lane, at: cards.map(function (c) {
                    const r = c.getBoundingClientRect(), t = getComputedStyle(c).transform, dy = t && t !== 'none' ? new DOMMatrixReadOnly(t).m42 : 0;
                    return { card: c, mid: r.top - dy + r.height / 2 };
                })
            };
            const hit = mids.at.find(function (m) { return y < m.mid; });
            return hit ? hit.card : null;
        }
        const mine = card.dataset.order || '';
        return cards.find(function (c) { return (c.dataset.order || '') > mine; }) || null;
    }

    // Opens the slot where a drop would put the card: in the lane under the pointer (its own lane too, in the team's own
    // order), the card leaving its place. Over its own place, a closed chip or nothing, the slot goes and the card is
    // back as the gap it left.
    function makeRoom(col, card, y) {
        const lane = col && col.classList.contains('tide-kcol') ? col.querySelector('.tide-kcol-cards') : null;
        const own = !!col && col.dataset.status === card.dataset.status;
        if (!lane || (own && !manual())) { clearRoom(card); drop = null; return; }
        const cards = Array.prototype.filter.call(lane.querySelectorAll('.tide-kcard'), function (c) { return c !== card; });
        const next = above(lane, cards, card, y);
        if (own && (next ? next.dataset.id : null) === home) { clearRoom(card); drop = null; return; }
        if (!slot) {
            slot = document.createElement('div');
            slot.className = 'tide-kslot';
            slot.setAttribute('aria-hidden', 'true');
            slot.style.height = card.offsetHeight + 'px';
        }
        const before = next || lane.querySelector('.tide-kcol-more, .tide-kcol-drop');
        if (slot.parentNode !== lane || slot.nextElementSibling !== before) { lane.insertBefore(slot, before); mids = null; }
        card.setAttribute('data-tide-away', '');
        drop = { status: col.dataset.status, before: next ? next.dataset.id : null };
    }

    function clearRoom(card) {
        if (slot) { slot.remove(); slot = null; mids = null; }
        if (card) card.removeAttribute('data-tide-away');
    }

    // Pressed on a card it may move: it may become a drag. While it's pressed (only then), the page's pointer moves and
    // letting go are heard.
    function onPointerDown(e) {
        if (e.button !== 0 || press || dragging) return;
        const card = e.target && e.target.closest ? e.target.closest('.tide-kcard[draggable="true"]') : null;
        if (!card || e.target.closest('button, input, select, textarea, [role="menu"]')) return;
        press = { card: card, id: e.pointerId, x: e.clientX, y: e.clientY, touch: e.pointerType === 'touch', ready: e.pointerType !== 'touch', timer: 0 };
        if (press.touch) press.timer = setTimeout(function () { if (press && !dragging) { press.ready = true; card.setAttribute('data-tide-pressed', ''); } }, HOLD);
        hear(true);
    }

    function hear(on) {
        const method = on ? 'addEventListener' : 'removeEventListener';
        document[method]('pointermove', onPointerMove);
        document[method]('pointerup', onPointerUp);
        document[method]('pointercancel', cancel);
        document[method]('selectstart', onSelectStart);
        document[method]('scroll', onScroll, { capture: true, passive: true });
    }

    function forget() {
        if (!press) return;
        clearTimeout(press.timer);
        press.card.removeAttribute('data-tide-pressed');
        press = null;
        hear(false);
    }

    // The press became a drag: the ghost (a copy of the card, outside the board) follows the pointer from here.
    function lift() {
        const card = press.card, r = card.getBoundingClientRect();
        dragging = card;
        over = null;
        try { card.setPointerCapture(press.id); } catch (_) { /* a pointer already let go */ }
        ghost = window.tideShortcuts.copy(card, r);
        ghost.classList.add('tide-kcard-ghost');
        ghost.classList.remove('selected', 'tide-kcard-held');
        ghost.removeAttribute('data-tide-pressed');
        const sel = window.getSelection && window.getSelection();
        if (sel) sel.removeAllRanges();
        let next = card.nextElementSibling;
        while (next && !next.classList.contains('tide-kcard')) next = next.nextElementSibling;
        home = next ? next.dataset.id : null;
        card.setAttribute('data-tide-drag', '');
        card.removeAttribute('data-tide-pressed');
        tell('OnDragging', card.dataset.id, null);
    }

    function onPointerMove(e) {
        if (!press || e.pointerId !== press.id) return;
        if (!dragging) {
            if (Math.hypot(e.clientX - press.x, e.clientY - press.y) < SLOP) return;
            if (!press.ready) { forget(); return; }   // a finger that moved before the hold: the page scrolls
            lift();
        }
        e.preventDefault();
        ghost.style.transform = 'translate(' + (e.clientX - press.x) + 'px, ' + (e.clientY - press.y) + 'px)';
        const under = document.elementFromPoint(e.clientX, e.clientY);
        const col = under && under.closest ? under.closest('.tide-kcol, .tide-kchip') : null;
        const inside = col && root.contains(col) ? col : null;
        const to = inside && inside.dataset.status !== dragging.dataset.status ? inside : null;
        // In the team's own order the slot follows the pointer, so it's placed on each move; sorted, only the lane
        // under it matters.
        if (manual() || inside !== lastInside) makeRoom(inside, dragging, e.clientY);
        lastInside = inside;
        if (to === marked) return;
        marked = to;
        mark(to);
        report(to ? to.dataset.status : null);
    }

    // The click that follows letting go isn't one: it would open the card's link, or select a lane's card under it.
    function swallowClick() {
        const stop = function (e) { e.preventDefault(); e.stopPropagation(); };
        window.addEventListener('click', stop, { capture: true, once: true });
        setTimeout(function () { window.removeEventListener('click', stop, true); }, 0);
    }

    // Brackets locked onto a card for a moment: it has landed.
    function lockOn(card) { window.tideShortcuts.pulse(card, 'data-tide-locked'); }

    // The slot goes and the ghost glides from where it was let go to where the card now is (where the slot was, once .NET
    // has moved it there), and the card shows again as it arrives, its brackets locked on. Meanwhile the card waits
    // transparent (data-tide-landing), not hidden: focus goes to it once the drop is done.
    function settle(g, card) {
        const ts = window.tideShortcuts;
        clearRoom(card);
        const done = function () {
            g.remove();
            if (!card) return;
            card.removeAttribute('data-tide-landing');
            lockOn(card);
        };
        if (!card || ts.quiet()) { done(); return; }
        const to = card.getBoundingClientRect(), left = parseFloat(g.style.left), top = parseFloat(g.style.top);
        card.setAttribute('data-tide-landing', '');
        ts.drift(g, [{ transform: g.style.transform || 'none' }, { transform: 'translate(' + (to.left - left) + 'px, ' + (to.top - top) + 'px)' }],
            { duration: ts.ms('--tide-dur-panel'), easing: ts.token('--tide-ease-out') }).finished.then(done, done);
    }

    function onPointerUp(e) {
        if (!press || e.pointerId !== press.id) return;
        if (!dragging) { forget(); return; }
        const card = dragging, id = card.dataset.id, from = card.dataset.status, at = drop, to = at ? at.status : over;
        const place = beside(card), g = ghost;
        swallowClick();
        forget();
        ghost = null;
        end();
        if (!to || !ref) { settle(g, card); focusCard(card); return; }
        // The ghost settles once .NET has moved the card (into the slot); if it's still asking after a moment (a drop on
        // the closed strip opens a dialog first) it settles back where the card is, which travels on if .NET then moves it.
        const current = function () { return card.isConnected ? card : root && root.querySelector('.tide-kcard[data-id="' + id + '"]'); };
        let landed = false;
        const land = function () {
            if (landed) return;
            landed = true;
            requestAnimationFrame(function () { const c = current(); settle(g, c); });
        };
        setTimeout(land, ASKING);
        ref.invokeMethodAsync('OnCardDropped', id, from, to, at ? at.before : null).then(function () {
            land();
            requestAnimationFrame(function () { focusPlace(id, place.next, place.prev, place.heading); });
        }, land);
    }

    // Cancelled (Esc, or the browser took the pointer back): the card goes back where it was.
    function cancel() {
        if (!dragging) { forget(); return; }
        const card = dragging, g = ghost;
        forget();
        ghost = null;
        end();
        settle(g, card);
        focusCard(card);
    }

    // The browser's own drag would take the pointer away (cards keep draggable="true", which says which may move); a
    // finger that's holding a card doesn't scroll the page or open the long-press menu; a mouse dragging a card doesn't
    // select the text it passes over.
    function onNativeDrag(e) { if (e.target && e.target.closest && e.target.closest('.tide-kcard')) e.preventDefault(); }
    function onTouchMove(e) { if (press && press.ready) e.preventDefault(); }
    function onContextMenu(e) { if (press && press.touch) e.preventDefault(); }
    function onSelectStart(e) { if (press) e.preventDefault(); }
    function onScroll() { mids = null; }

    function card(id) { return root ? root.querySelector('.tide-kcard[data-id="' + id + '"]') : null; }

    function focusMenu(id) {
        const c = card(id);
        const first = c && c.querySelector('.tide-kcard-menu [role="menuitem"]:not([disabled])');
        if (first) first.focus();
    }

    // The cards either side of this one in its lane, and its lane's heading.
    function beside(c) {
        const lane = c.closest('.tide-kcol'), cards = lane ? Array.prototype.slice.call(lane.querySelectorAll('.tide-kcard')) : [], at = cards.indexOf(c);
        const id = function (x) { return x ? x.dataset.id : null; };
        return { next: id(cards[at + 1]), prev: id(cards[at - 1]), heading: lane && lane.querySelector('.tide-kcol-head') ? lane.querySelector('.tide-kcol-head').id : null };
    }

    // Focus to a card's Move button (or its title, on a card the person can't move).
    function focusCard(c) {
        const el = c && (c.querySelector('.tide-kcard-move') || c.querySelector('.tide-action-ref'));
        if (el) el.focus();
        return !!el;
    }

    // After a move (its Move menu, or a drop): the card, if still on the board, locks on where it landed; focus goes there.
    function focusPlace(id, next, prev, heading) {
        const moved = card(id);
        if (moved) lockOn(moved);
        if (focusCard(moved) || (next && focusCard(card(next))) || (prev && focusCard(card(prev)))) return;
        const h = heading && document.getElementById(heading);
        if (h) h.focus();
    }

    function onKeyDown(e) {
        if (e.key === 'Escape' && dragging) { e.preventDefault(); e.stopPropagation(); cancel(); return; }
        const menu = e.target && e.target.closest ? e.target.closest('.tide-kcard-menu') : null;
        if (!menu || e.ctrlKey || e.metaKey || e.altKey) return;
        if (e.key === 'Escape') {
            e.preventDefault();
            const move = menu.closest('.tide-kcard').querySelector('.tide-kcard-move');
            if (move) move.focus();
            return;
        }
        const items = Array.prototype.slice.call(menu.querySelectorAll('[role="menuitem"]:not([disabled])'));
        const to = window.tideRoving(items, document.activeElement, e, 'both');
        if (!to) return;
        e.preventDefault();
        to.focus();
    }

    // Arriving at a card the address names (?card=): it takes focus, scrolled to the middle of its lane.
    function reveal() {
        const c = document.querySelector('.tide-kcard.selected');
        if (!c) return;
        c.focus({ preventScroll: true });
        c.scrollIntoView({ block: 'center' });
    }

    return {
        reveal: reveal,
        focusMenu: focusMenu,
        focusPlace: focusPlace,
        start: function (dotnetRef, el) {
            ref = dotnetRef;
            root = el;
            root.addEventListener('pointerdown', onPointerDown, true);
            root.addEventListener('dragstart', onNativeDrag);
            root.addEventListener('touchmove', onTouchMove, { passive: false });
            root.addEventListener('contextmenu', onContextMenu);
            document.addEventListener('keydown', onKeyDown, true);
        },
        stop: function () {
            if (dragging || press) cancel();
            if (root) {
                root.removeEventListener('pointerdown', onPointerDown, true);
                root.removeEventListener('dragstart', onNativeDrag);
                root.removeEventListener('touchmove', onTouchMove, { passive: false });
                root.removeEventListener('contextmenu', onContextMenu);
            }
            document.removeEventListener('keydown', onKeyDown, true);
            ref = null;
            root = null;
            dragging = null;
            over = null;
        }
    };
})();
