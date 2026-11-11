// Row selection for a list page: the keys that move the selected row, and focus following it. CSP-safe (an external
// file, no inline handlers). .NET owns the selection and draws it: the selected row carries the class "selected",
// aria-current="true" and tabindex="0", every other row tabindex="-1", so the list is one tab stop (RowSelection in
// .NET writes these). A key goes to .NET as an action ("next", "prev", "open", or one of the page's own), and once .NET
// has drawn the answer focus goes to the row now selected, scrolled into view.
//
// J and the down arrow are "next", K and the up arrow "prev"; Enter with focus on a row is "open". Never while typing
// in a field, with focus in a menu or dialog, under an open confirmation, or with Ctrl, Alt or the command key held;
// the letters also obey the single-key switch (tideShortcuts), the arrows and Enter don't.
//
// One list has the keys at a time: start(ref, options) hands back a token, and stop(token) lets go only if no other
// list has started since. The options:
//   row     the class every row of the list carries (a class name, not a selector)
//   method  the [JSInvokable] method the actions go to ("OnRowKey" unless given)
//   keys    more keys, as { key: action }, a letter in lower case
//   follow  the page's own actions after which focus goes to the selected row, as it does after "next" and "prev";
//           after any other action focus moves there only if it was lost (the row it was in has gone)
//   onRow   the page's own actions taken only with focus in a row of the list (the board's left and right arrows),
//           so the key keeps its meaning anywhere else
//   held    function (e): true while something of the page's has the keys (a form open in a row); the page deals with
//           the key itself, send() for what it wants done
//   key     function (e, action, onRow): the action for this key, given the one found (or none); for keys that depend
//           on what has focus or what is open
//   open    function (row): opens the selected row here in the browser, in place of sending "open" to .NET
window.tideRows = (function () {
    let ref = null, opt = null, token = 0;
    const shortcuts = window.tideShortcuts;
    const moves = { j: 'next', ArrowDown: 'next', k: 'prev', ArrowUp: 'prev' };

    // Letters match in either case, so Caps Lock or Shift doesn't stop them.
    function keyOf(e) { return e.key && e.key.length === 1 ? e.key.toLowerCase() : e.key; }

    // The selected row, found by its two classes.
    function selected() {
        return opt ? document.getElementsByClassName(opt.row + ' selected')[0] || null : null;
    }

    function reveal() {
        const row = selected();
        if (row) row.scrollIntoView({ block: 'nearest' });
    }

    // After a move: focus goes to the newly selected row (focusing it scrolls it into view).
    function follow() {
        const row = selected();
        if (row && document.activeElement !== row) row.focus();
        reveal();
    }

    // After anything else: if the row that had focus has gone, focus went with it to the page, so it goes to the row
    // now selected and the focus ring and a screen reader's place carry on down the list. A control that took focus
    // itself keeps it.
    function settle() {
        const lost = !document.activeElement || document.activeElement === document.body || !document.activeElement.isConnected;
        if (lost) follow(); else reveal();
    }

    // Keys go to .NET one at a time, in the order pressed: each waits until the one before it is answered, so a key
    // pressed while another is still on its way (one that turns the page and reads it, say) acts on what that one left,
    // and none is lost. Focus moves only once the last is answered: a row taking focus selects itself (its focus handler
    // in .NET), so focusing the row an earlier answer drew, with later keys still waiting, would pull the selection back
    // to that older row. Meanwhile the selected row is only scrolled into view. Then focus follows the selection if any
    // of the keys was a move.
    let waiting = [], answering = false, moved = false;

    function busy() { return answering || waiting.length > 0; }

    function send(action) {
        if (!ref) return;
        waiting.push(action);
        moved = moved || opt.follow.indexOf(action) >= 0;
        if (!answering) next();
    }

    function next() {
        answering = true;
        ref.invokeMethodAsync(opt.method, waiting.shift()).then(done, done);
    }

    function done() {
        answering = false;
        if (ref && waiting.length > 0) { reveal(); next(); return; }
        const then = moved ? follow : settle;
        moved = false;
        then();
    }

    function onKey(e) {
        if (!ref || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
        if (document.querySelector('.tide-modal')) return;   // a confirmation is open: its keys are its own
        if (shortcuts.inRail(e.target)) return;               // so is the context rail, pressed in it or open over the list
        if (opt.held && opt.held(e)) return;
        if (shortcuts.typing(e.target) || shortcuts.inMenu(e.target)) return;
        if (!shortcuts.on() && shortcuts.isLetter(e)) return;
        const onRow = !!(e.target.classList && e.target.classList.contains(opt.row));
        let action = opt.keys[keyOf(e)];
        if (e.key === 'Enter' && onRow) action = 'open';
        if (opt.key) action = opt.key(e, action, onRow);
        if (!action) return;
        if (opt.onRow.indexOf(action) >= 0 && !(e.target.closest && e.target.closest('.' + opt.row))) return;
        e.preventDefault();
        if (action === 'open' && opt.open) opt.open(selected());
        else send(action);
    }

    return {
        start: function (dotnetRef, options) {
            options = options || {};
            opt = {
                row: options.row, method: options.method || 'OnRowKey',
                keys: Object.assign({}, moves, options.keys), follow: ['next', 'prev'].concat(options.follow || []), onRow: options.onRow || [],
                held: options.held, key: options.key, open: options.open
            };
            ref = dotnetRef;
            document.addEventListener('keydown', onKey);
            return ++token;
        },
        stop: function (started) {
            if (started !== token) return;   // another list has the keys now
            document.removeEventListener('keydown', onKey);
            waiting = [];
            ref = null;
            opt = null;
        },
        send: send,
        // The page's own calls to move focus to the selected row (after its own work, or a page of the list turned) are
        // left to the last key's answer while keys are on their way: see send.
        follow: function () { if (!busy()) follow(); },
        // Arriving at a row from the address, or another page of the list: it takes focus and scrolls to the middle of
        // the window, past the heading the router focused.
        focusSelected: function () {
            const row = selected();
            if (!row || busy()) return;
            row.focus({ preventScroll: true });
            row.scrollIntoView({ block: 'center' });
        },
        // This history entry's address becomes `url` (the page's own state is kept, and nothing is navigated), so a
        // reload, a copied link or Back returns to the tab, filters, page and row it names.
        remember: function (url) {
            history.replaceState(history.state, '', new URL(url, document.baseURI).href);
        }
    };
})();
