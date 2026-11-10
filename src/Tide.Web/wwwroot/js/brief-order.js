// The brief's running order: drag a row above or below another to move it there. CSP-safe (an external file, no
// inline handlers), as the actions board (board.js). The drop goes to .NET, which saves the new order
// (BriefService.ReorderAsync); only rows the person may move are draggable. Each row also has Move up and Move down,
// the way to do this from the keyboard, and focus() keeps focus on the moved row's button after it re-renders.
window.tideOrder = (function () {
    let ref = null, root = null, dragging = null;

    function row(e) { return e.target && e.target.closest ? e.target.closest('.tide-order-row[data-id]') : null; }

    function clear() {
        if (!root) return;
        root.querySelectorAll('.tide-order-row').forEach(function (r) { r.classList.remove('drop-before', 'drop-after'); });
    }

    // The lower half of a row drops after it, the upper half before.
    function after(e, r) {
        const box = r.getBoundingClientRect();
        return e.clientY > box.top + box.height / 2;
    }

    function onDragStart(e) {
        const r = e.target && e.target.closest ? e.target.closest('.tide-order-row[draggable="true"]') : null;
        if (!r) return;
        dragging = r;
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', r.dataset.id);   // Firefox starts a drag only with data set
        r.classList.add('dragging');
    }

    function onDragOver(e) {
        if (!dragging) return;
        const r = row(e);
        if (!r) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        clear();
        if (r !== dragging) r.classList.add(after(e, r) ? 'drop-after' : 'drop-before');
    }

    function onDrop(e) {
        if (!dragging) return;
        const r = row(e);
        if (!r) return;
        e.preventDefault();
        const id = dragging.dataset.id, target = r.dataset.id, below = after(e, r);
        end();
        if (id !== target && ref) ref.invokeMethodAsync('OnRowDropped', id, target, below).catch(function () { });
    }

    function end() {
        if (dragging) dragging.classList.remove('dragging');
        dragging = null;
        clear();
    }

    return {
        start: function (dotnetRef, el) {
            ref = dotnetRef;
            root = el;
            root.addEventListener('dragstart', onDragStart);
            root.addEventListener('dragover', onDragOver);
            root.addEventListener('drop', onDrop);
            root.addEventListener('dragend', end);
        },
        stop: function () {
            if (root) {
                root.removeEventListener('dragstart', onDragStart);
                root.removeEventListener('dragover', onDragOver);
                root.removeEventListener('drop', onDrop);
                root.removeEventListener('dragend', end);
            }
            ref = null;
            root = null;
            dragging = null;
        },
        // After a move: the same button on the moved row, or the other one when it's now at the end it moved towards.
        focus: function (el, id, dir) {
            if (!el) return;
            const other = dir === 'up' ? 'down' : 'up';
            const pick = function (d) { return el.querySelector('[data-id="' + id + '"] [data-move="' + d + '"]'); };
            const b = pick(dir);
            const target = b && !b.disabled ? b : pick(other);
            if (target && target.focus) target.focus();
        },
        // After a warning's one-click fix, whose button has gone with the warning: the row's title. A fix that was
        // refused leaves its button, and focus on it.
        focusTitle: function (el, id) {
            const row = el && el.querySelector('[data-id="' + id + '"]');
            if (!row || row.contains(document.activeElement)) return;
            const title = row.querySelector('.tide-run-title');
            if (title) title.focus();
        },
        // After a row is taken off: its Undo, which the toast draws a moment later.
        focusUndo: function () {
            let tries = 0;
            const look = function () {
                const all = document.querySelectorAll('.tide-toast-undo');
                if (all.length > 0) { all[all.length - 1].focus(); return; }
                if (++tries < 10) setTimeout(look, 50);
            };
            look();
        }
    };
})();
