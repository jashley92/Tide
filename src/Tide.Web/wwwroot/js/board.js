// Actions board: drag a card to another lane, or onto a chip on the closed strip, to move the action there. CSP-safe (an
// external file, no inline handlers). The drop goes to .NET, which checks and makes the move (ActionService.MoveAsync);
// only cards the person may move are draggable. Each card also has a Move menu, the way to do this from the keyboard.
// The drag is by pointer events (mouse, pen and touch alike), not the browser's drag and drop, so the card can be lifted,
// follow the pointer and settle where it lands: a mouse or pen starts it by moving 6px with the button down, a finger by
// holding still a moment first (so a swipe still scrolls the page). The held card is a copy (the ghost); the card itself
// stays in its lane as the dashed gap it left, until .NET moves it. Esc cancels; a press that isn't a drag is a click.
// While a card is held, .NET hears when the drag starts, each time it crosses into another column, and when it ends (a
// few calls a drag, not the pointer's path), so everyone else's board can show who is moving it and where (BoardDrags).
// When someone else's move arrives, play() slides the card in from the column it left.
// The Move menu, from the keyboard: opening it focuses its first option (focusMenu), the arrows and Home/End move
// between its options, and Esc gives focus back to the Move button (.NET closes the menu). After a move from it, focus
// goes to the card's Move button in its new lane, or when it has left the board (closed), to the card that was after it
// in its lane, else the one before, else the lane's heading (focusPlace). A card dragged (pressing it gives it focus) does the
// same once its drop is done.
window.tideBoard = (function () {
    let ref = null, root = null, dragging = null, over = null, focusedAtPress = null, press = null, ghost = null;
    const SLOP = 6, HOLD = 280;

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
        if (root) mark(null);
    }

    // The card pressed on: pressing a card gives it focus (each card is focusable, the selected one the tab stop), so a
    // card dragged off the board after a click, or with focus already in it, leaves focus beside where it was.
    function onPress(e) {
        focusedAtPress = e.target && e.target.closest ? e.target.closest('.tide-kcard') : null;
    }

    // A token's value in ms, or the easing it names (the stylesheet's motion tokens).
    function token(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }

    function onPointerDown(e) {
        onPress(e);
        if (e.button !== 0 || press || dragging) return;
        const card = e.target && e.target.closest ? e.target.closest('.tide-kcard[draggable="true"]') : null;
        if (!card || e.target.closest('button, input, select, textarea, [role="menu"]')) return;
        press = { card: card, id: e.pointerId, x: e.clientX, y: e.clientY, touch: e.pointerType === 'touch', ready: e.pointerType !== 'touch', timer: 0 };
        if (press.touch) press.timer = setTimeout(function () { if (press && !dragging) { press.ready = true; card.classList.add('tide-kcard-held'); } }, HOLD);
    }

    function forget() {
        if (!press) return;
        clearTimeout(press.timer);
        press.card.classList.remove('tide-kcard-held');
        press = null;
    }

    // The press became a drag: the ghost (a copy of the card, outside the board) follows the pointer from here.
    function lift() {
        const card = press.card, r = card.getBoundingClientRect();
        dragging = card;
        over = null;
        try { card.setPointerCapture(press.id); } catch (_) { /* a pointer already let go */ }
        ghost = card.cloneNode(true);
        ghost.querySelectorAll('[id]').forEach(function (n) { n.removeAttribute('id'); });
        ghost.removeAttribute('id');
        ghost.setAttribute('aria-hidden', 'true');
        ghost.inert = true;
        ghost.classList.add('tide-kcard-ghost');
        ghost.classList.remove('selected', 'tide-kcard-held');
        ghost.style.left = r.left + 'px';
        ghost.style.top = r.top + 'px';
        ghost.style.width = r.width + 'px';
        ghost.style.height = r.height + 'px';
        document.body.appendChild(ghost);
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
        mark(to);
        report(to ? to.dataset.status : null);
    }

    // The click that follows letting go isn't one: it would open the card's link, or select a lane's card under it.
    function swallowClick() {
        const stop = function (e) { e.preventDefault(); e.stopPropagation(); };
        window.addEventListener('click', stop, { capture: true, once: true });
        setTimeout(function () { window.removeEventListener('click', stop, true); }, 0);
    }

    // The ghost glides from where it was let go to where the card now is, and the card shows again as it arrives.
    function settle(g, card) {
        const done = function () {
            g.remove();
            if (!card) return;
            card.style.visibility = '';
            card.classList.add('tide-kcard-locked');
            setTimeout(function () { card.classList.remove('tide-kcard-locked'); }, 600);
        };
        if (!card || window.tideShortcuts.still() || !g.animate) { done(); return; }
        const to = card.getBoundingClientRect(), left = parseFloat(g.style.left), top = parseFloat(g.style.top);
        card.style.visibility = 'hidden';
        g.animate([{ transform: g.style.transform || 'none' }, { transform: 'translate(' + (to.left - left) + 'px, ' + (to.top - top) + 'px)' }],
            { duration: parseFloat(token('--tide-dur-panel')) || 280, easing: token('--tide-ease-out') || 'ease-out', fill: 'forwards' })
            .finished.then(done, done);
    }

    function onPointerUp(e) {
        if (!press || e.pointerId !== press.id) return;
        if (!dragging) { forget(); return; }
        const target = root.querySelector('.tide-kcol.over, .tide-kchip.over');
        const card = dragging, id = card.dataset.id, from = card.dataset.status, to = target ? target.dataset.status : null;
        const place = focusedAtPress === card ? beside(card) : null, g = ghost;
        swallowClick();
        forget();
        ghost = null;
        end();
        if (!to || !ref) { settle(g, card); return; }
        // The ghost settles once .NET has moved the card, or after a moment if it's still asking (a drop on the closed
        // strip opens a dialog first): then it settles back where the card still is.
        let settled = false;
        const land = function () {
            if (settled) return;
            settled = true;
            requestAnimationFrame(function () { settle(g, card.isConnected ? card : root && root.querySelector('.tide-kcard[data-id="' + id + '"]')); });
        };
        setTimeout(land, 400);
        ref.invokeMethodAsync('OnCardDropped', id, from, to).then(function () {
            land();
            if (place) requestAnimationFrame(function () { focusPlace(id, place.next, place.prev, place.heading); });
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

    // The browser's own drag would take the pointer away; a finger that's holding a card doesn't scroll the page or
    // open the long-press menu.
    function onNativeDrag(e) { if (e.target && e.target.closest && e.target.closest('.tide-kcard')) e.preventDefault(); }
    // Nor does a mouse dragging a card select the text it passes over.
    function onSelectStart(e) { if (press) e.preventDefault(); }
    function onTouchMove(e) { if (press && press.ready) e.preventDefault(); }
    function onContextMenu(e) { if (press && press.touch) e.preventDefault(); }

    // Someone else moved these cards: each slides in from the column it left (the card's glow and "Moved by" are
    // rendered by .NET). Without motion (the person's setting, or no Web Animations), the glow alone says it arrived.
    function play(moves) {
        if (!root || !moves) return;
        const still = window.tideShortcuts.still();
        moves.forEach(function (m) {
            const card = root.querySelector('.tide-kcard[data-id="' + m.id + '"]');
            if (!card) return;
            const from = root.querySelector('.tide-kcol[data-status="' + m.from + '"] .tide-kcol-cards');
            if (still || !from || !card.animate) return;
            const dx = from.getBoundingClientRect().left - card.getBoundingClientRect().left;
            card.animate([{ transform: 'translateX(' + dx + 'px)', opacity: .6 }, { transform: 'none', opacity: 1 }],
                { duration: 450, easing: 'cubic-bezier(.2, .8, .2, 1)' });
        });
    }

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

    function focusPlace(id, next, prev, heading) {
        if (focusCard(card(id)) || (next && focusCard(card(next))) || (prev && focusCard(card(prev)))) return;
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
        play: play,
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
            document.addEventListener('pointermove', onPointerMove);
            document.addEventListener('pointerup', onPointerUp);
            document.addEventListener('pointercancel', cancel);
            document.addEventListener('keydown', onKeyDown, true);
            document.addEventListener('selectstart', onSelectStart);
        },
        stop: function () {
            if (dragging || press) cancel();
            if (root) {
                root.removeEventListener('pointerdown', onPointerDown, true);
                root.removeEventListener('dragstart', onNativeDrag);
                root.removeEventListener('touchmove', onTouchMove, { passive: false });
                root.removeEventListener('contextmenu', onContextMenu);
            }
            document.removeEventListener('pointermove', onPointerMove);
            document.removeEventListener('pointerup', onPointerUp);
            document.removeEventListener('pointercancel', cancel);
            document.removeEventListener('keydown', onKeyDown, true);
            document.removeEventListener('selectstart', onSelectStart);
            ref = null;
            root = null;
            dragging = null;
            over = null;
            focusedAtPress = null;
        }
    };
})();
