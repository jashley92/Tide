// The Library's lists (LibraryBrowser): choosing an entry with the keys, as Triage's rows are chosen. J and K move to
// the next and the previous row from anywhere on the page (not while typing, in a menu or under a dialog, and not when
// single-key shortcuts are off); the up and down arrows do the same while a row has focus, so they still scroll the
// page from anywhere else. The rows are one tab stop (a roving tabindex, as .NET renders them): moving focuses the row,
// and focus chooses it (.NET, @onfocus), so the highlight and focus are always the same row.
// .NET keeps the address naming the entry shown (tideLibrary.remember, after each change of it): the address is
// replaced in place, so a copied link or a reload opens the same entry, with no navigation to take focus out of the list.
// CSP-safe (an external file, no inline handlers); one listener for every list.
window.tideLibrary = (function () {
    const rowOf = function (target) { return target && target.closest ? target.closest('.tide-library-row') : null; };

    document.addEventListener('keydown', function (e) {
        if (e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
        const key = e.key && e.key.length === 1 ? e.key.toLowerCase() : e.key;   // either case, so Caps Lock doesn't stop them
        if (key !== 'j' && key !== 'k' && key !== 'ArrowDown' && key !== 'ArrowUp') return;
        const list = document.querySelector('.tide-library-list');
        if (!list || document.querySelector('.tide-modal')) return;
        if (window.tideShortcuts.typing(e.target) || window.tideShortcuts.inMenu(e.target)) return;
        const focused = rowOf(e.target);
        let by = 0;
        if (key === 'j' || key === 'k') {
            if (!window.tideShortcuts.on()) return;
            by = key === 'j' ? 1 : -1;
        } else if (focused && (key === 'ArrowDown' || key === 'ArrowUp')) {
            by = key === 'ArrowDown' ? 1 : -1;
        }
        if (!by) return;
        const rows = Array.prototype.slice.call(list.querySelectorAll('.tide-library-row'));
        const from = focused || list.querySelector('.tide-library-row.selected');
        const next = rows[rows.indexOf(from) + by];
        e.preventDefault();
        if (next) next.focus();
        else if (from && from !== document.activeElement) from.focus();
    });

    return {
        // This history entry's address becomes the page and entry shown (the page's own state is kept).
        remember: function (url) {
            const address = new URL(url, document.baseURI).href;
            if (address !== location.href) history.replaceState(history.state, '', address);
        }
    };
})();
