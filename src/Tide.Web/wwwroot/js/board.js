// Actions board: drag a card to another lane, or onto a chip on the closed strip, to move the action there. CSP-safe (an
// external file, no inline handlers). The drop goes to .NET, which checks and makes the move (ActionService.MoveAsync);
// only cards the person may move are draggable. Each card also has a Move menu, the way to do this from the keyboard.
// While a card is held, .NET hears when the drag starts, each time it crosses into another column, and when it ends (a
// few calls a drag, not the pointer's path), so everyone else's board can show who is moving it and where (BoardDrags).
// When someone else's move arrives, play() slides the card in from the column it left.
// The Move menu, from the keyboard: opening it focuses its first option (focusMenu), the arrows and Home/End move
// between its options, and Esc gives focus back to the Move button (.NET closes the menu). After a move from it, focus
// goes to the card's Move button in its new column (focusMove).
window.tideBoard = (function () {
    let ref = null, root = null, dragging = null, over = null;

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

    // Where a card is over: a lane, or a chip on the closed strip (both carry the status a drop moves it to).
    function column(e) { return e.target && e.target.closest ? e.target.closest('.tide-kcol, .tide-kchip') : null; }

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

    function onDragStart(e) {
        const card = e.target && e.target.closest ? e.target.closest('.tide-kcard[draggable="true"]') : null;
        if (!card) return;
        dragging = card;
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', card.dataset.id);   // Firefox starts a drag only with data set
        card.classList.add('dragging');
        over = null;
        tell('OnDragging', card.dataset.id, null);
    }

    function onDragOver(e) {
        if (!dragging) return;
        const col = column(e);
        if (!col || col.dataset.status === dragging.dataset.status) { mark(null); report(null); return; }
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        mark(col);
        report(col.dataset.status);
    }

    function onDragLeave(e) {
        const col = column(e);
        if (col && !col.contains(e.relatedTarget)) col.classList.remove('over');
    }

    function onDrop(e) {
        if (!dragging) return;
        const col = column(e);
        if (!col) return;
        e.preventDefault();
        const id = dragging.dataset.id, from = dragging.dataset.status, to = col.dataset.status;
        end();
        if (from !== to && ref) ref.invokeMethodAsync('OnCardDropped', id, from, to).catch(function () { });
    }

    // Someone else moved these cards: each slides in from the column it left (the card's glow and "Moved by" are
    // rendered by .NET). Without motion (the person's setting, or no Web Animations), the glow alone says it arrived.
    function play(moves) {
        if (!root || !moves) return;
        const still = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
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

    function focusMove(id) {
        const c = card(id);
        const move = c && c.querySelector('.tide-kcard-move');
        if (move) move.focus();
    }

    function onKeyDown(e) {
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

    return {
        play: play,
        focusMenu: focusMenu,
        focusMove: focusMove,
        start: function (dotnetRef, el) {
            ref = dotnetRef;
            root = el;
            root.addEventListener('dragstart', onDragStart);
            root.addEventListener('dragover', onDragOver);
            root.addEventListener('dragleave', onDragLeave);
            root.addEventListener('drop', onDrop);
            root.addEventListener('dragend', end);
            root.addEventListener('keydown', onKeyDown);
        },
        stop: function () {
            if (root) {
                root.removeEventListener('dragstart', onDragStart);
                root.removeEventListener('dragover', onDragOver);
                root.removeEventListener('dragleave', onDragLeave);
                root.removeEventListener('drop', onDrop);
                root.removeEventListener('dragend', end);
                root.removeEventListener('keydown', onKeyDown);
            }
            ref = null;
            root = null;
            dragging = null;
            over = null;
        }
    };
})();
