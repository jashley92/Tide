// Actions board: drag a card to another lane, or onto a chip on the closed strip, to move the action there. CSP-safe (an
// external file, no inline handlers). The drop goes to .NET, which checks and makes the move (ActionService.MoveAsync);
// only cards the person may move are draggable. Each card also has a Move menu, the way to do this from the keyboard.
// The drag is by pointer events (mouse, pen and touch alike), not the browser's drag and drop, so the card can be lifted,
// follow the pointer and settle where it lands: a mouse or pen starts it by moving a few pixels with the button down, a finger by
// holding still a moment first (so a swipe still scrolls the page). The held card is a copy (the ghost). Over its own lane
// the card itself stays as the dashed gap it left; over another lane it leaves its own (which closes up) and a dashed
// slot opens where it would land, the cards there sliding aside to make room (flip.js moves them). Lanes are sorted
// (ActionKanban.LaneOrder), so the slot opens at the card's place in that order, not under the pointer. Esc cancels; a
// press that isn't a drag is a click.
// While a card is held, .NET hears when the drag starts, each time it crosses into another column, and when it ends (a
// few calls a drag, not the pointer's path), so everyone else's board can show who is moving it and where (BoardDrags).
// A card that moves for any other reason (its Move menu, someone else's move) travels from where it was (flip.js).
// The Move menu, from the keyboard: opening it focuses its first option (focusMenu), the arrows and Home/End move
// between its options, and Esc gives focus back to the Move button (.NET closes the menu). After a move from it, focus
// goes to the card's Move button in its new lane, or when it has left the board (closed), to the card that was after it
// in its lane, else the one before, else the lane's heading (focusPlace). A card dragged (pressing it gives it focus) does the
// same once its drop is done.
window.tideBoard = (function () {
    let ref = null, root = null, dragging = null, over = null, marked = null, focusedAtPress = null, press = null, ghost = null, slot = null;
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
        root.querySelectorAll('.over').forEach(function (c) { if (c !== col) c.classList.remove('over'); });
        if (col) col.classList.add('over');
    }

    function end() {
        if (dragging) {
            dragging.classList.remove('dragging');
            tell('OnDragEnded');
        }
        dragging = null;
        over = null;
        marked = null;
        if (root) mark(null);
    }

    // Over another lane, the slot opens there at the card's place in its order, and the card leaves its own lane; over its
    // own lane, a closed chip or nothing, the slot goes and the card is back as the gap it left.
    function makeRoom(col, card) {
        const lane = col && col.classList.contains('tide-kcol') ? col.querySelector('.tide-kcol-cards') : null;
        if (!lane) { clearRoom(card); return; }
        if (!slot) {
            slot = document.createElement('div');
            slot.className = 'tide-kslot';
            slot.setAttribute('aria-hidden', 'true');
            slot.style.height = card.offsetHeight + 'px';
        }
        const mine = card.dataset.order || '';
        const before = Array.prototype.find.call(lane.querySelectorAll('.tide-kcard'), function (c) { return c !== card && (c.dataset.order || '') > mine; })
            || lane.querySelector('.tide-kcol-more, .tide-kcol-drop');
        if (slot.parentNode !== lane || slot.nextElementSibling !== before) lane.insertBefore(slot, before);
        card.classList.add('tide-kcard-away');
    }

    function clearRoom(card) {
        if (slot) { slot.remove(); slot = null; }
        if (card) card.classList.remove('tide-kcard-away');
    }

    // Pressed on a card it may move: it may become a drag. While it's pressed (only then), the page's pointer moves and
    // letting go are heard. Pressing a card gives it focus (each card is focusable), so a card dragged off the board after
    // a click, or with focus already in it, leaves focus beside where it was (focusedAtPress).
    function onPointerDown(e) {
        focusedAtPress = e.target && e.target.closest ? e.target.closest('.tide-kcard') : null;
        if (e.button !== 0 || press || dragging) return;
        const card = e.target && e.target.closest ? e.target.closest('.tide-kcard[draggable="true"]') : null;
        if (!card || e.target.closest('button, input, select, textarea, [role="menu"]')) return;
        press = { card: card, id: e.pointerId, x: e.clientX, y: e.clientY, touch: e.pointerType === 'touch', ready: e.pointerType !== 'touch', timer: 0 };
        if (press.touch) press.timer = setTimeout(function () { if (press && !dragging) { press.ready = true; card.classList.add('tide-kcard-held'); } }, HOLD);
        hear(true);
    }

    function hear(on) {
        const method = on ? 'addEventListener' : 'removeEventListener';
        document[method]('pointermove', onPointerMove);
        document[method]('pointerup', onPointerUp);
        document[method]('pointercancel', cancel);
        document[method]('selectstart', onSelectStart);
    }

    function forget() {
        if (!press) return;
        clearTimeout(press.timer);
        press.card.classList.remove('tide-kcard-held');
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
        const sel = window.getSelection && window.getSelection();
        if (sel) sel.removeAllRanges();
        card.classList.add('dragging');
        card.classList.remove('tide-kcard-held');
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
        const to = col && root.contains(col) && col.dataset.status !== dragging.dataset.status ? col : null;
        if (to === marked) return;
        marked = to;
        mark(to);
        makeRoom(to, dragging);
        report(to ? to.dataset.status : null);
    }

    // The click that follows letting go isn't one: it would open the card's link, or select a lane's card under it.
    function swallowClick() {
        const stop = function (e) { e.preventDefault(); e.stopPropagation(); };
        window.addEventListener('click', stop, { capture: true, once: true });
        setTimeout(function () { window.removeEventListener('click', stop, true); }, 0);
    }

    // Brackets locked onto a card for a moment: it has landed.
    function lockOn(card) {
        card.classList.add('tide-kcard-locked');
        setTimeout(function () { card.classList.remove('tide-kcard-locked'); }, window.tideShortcuts.ms('--tide-dur-signature'));
    }

    // The slot goes and the ghost glides from where it was let go to where the card now is (where the slot was, once .NET
    // has moved it there), and the card shows again as it arrives, its brackets locked on.
    function settle(g, card) {
        clearRoom(card);
        const done = function () {
            g.remove();
            if (!card) return;
            card.style.visibility = '';
            lockOn(card);
        };
        if (!card || window.tideShortcuts.still() || !g.animate) { done(); return; }
        const to = card.getBoundingClientRect(), left = parseFloat(g.style.left), top = parseFloat(g.style.top);
        card.style.visibility = 'hidden';
        g.animate([{ transform: g.style.transform || 'none' }, { transform: 'translate(' + (to.left - left) + 'px, ' + (to.top - top) + 'px)' }],
            { duration: window.tideShortcuts.ms('--tide-dur-panel'), easing: window.tideShortcuts.token('--tide-ease-out'), fill: 'forwards' })
            .finished.then(done, done);
    }

    function onPointerUp(e) {
        if (!press || e.pointerId !== press.id) return;
        if (!dragging) { forget(); return; }
        const card = dragging, id = card.dataset.id, from = card.dataset.status, to = over;
        const place = focusedAtPress === card ? beside(card) : null, g = ghost;
        swallowClick();
        forget();
        ghost = null;
        end();
        if (!to || !ref) { settle(g, card); return; }
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
        ref.invokeMethodAsync('OnCardDropped', id, from, to).then(function () {
            land();
            requestAnimationFrame(function () { if (place) focusPlace(id, place.next, place.prev, place.heading); });
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
    }

    // The browser's own drag would take the pointer away (cards keep draggable="true", which says which may move); a
    // finger that's holding a card doesn't scroll the page or open the long-press menu; a mouse dragging a card doesn't
    // select the text it passes over.
    function onNativeDrag(e) { if (e.target && e.target.closest && e.target.closest('.tide-kcard')) e.preventDefault(); }
    function onTouchMove(e) { if (press && press.ready) e.preventDefault(); }
    function onContextMenu(e) { if (press && press.touch) e.preventDefault(); }
    function onSelectStart(e) { if (press) e.preventDefault(); }

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
            focusedAtPress = null;
        }
    };
})();
