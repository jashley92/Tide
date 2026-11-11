// Tabs (TabBar with a panel): the current tab is the tablist's one tab stop, and Left/Right/Home/End move to another
// tab and show it, as the ARIA tabs pattern has it (tideRoving, in theme.js). Showing it is a click, so .NET switches
// the panel and moves the tab stop (tabindex) when it re-renders. CSP-safe (an external file, no inline handlers); one
// listener for every tablist.
// A tab row that scrolls sideways inside itself because it doesn't fit (the Actions views on a phone, or with wider fonts)
// keeps the tab that takes focus wholly in view, and its current tab once the page asks (tideTabs.revealCurrent, after a
// render that changed it); its data-more says which edges have more beyond them ("start", "end"), for the fade there.
// A row that fits, or wraps, is left alone.
window.tideTabs = (function () {
    // Room left beside a tab brought into view, so the fade on that edge doesn't cover it.
    const fade = 24;

    function mark(bar) {
        const text = [bar.scrollLeft > 1 && 'start', bar.scrollLeft + bar.clientWidth < bar.scrollWidth - 1 && 'end'].filter(Boolean).join(' ');
        if (bar.dataset.more !== text) bar.dataset.more = text;
    }

    function reveal(bar, el) {
        if (el && bar.scrollWidth > bar.clientWidth + 1) {
            const b = bar.getBoundingClientRect(), e = el.getBoundingClientRect();
            if (e.left < b.left + fade) bar.scrollLeft -= b.left + fade - e.left;
            else if (e.right > b.right - fade) bar.scrollLeft += e.right - (b.right - fade);
        }
        mark(bar);
    }

    document.addEventListener('focusin', function (e) {
        const bar = e.target && e.target.parentElement;
        if (bar && bar.classList.contains('tide-tabbar')) reveal(bar, e.target);
    });
    document.addEventListener('scroll', function (e) {
        if (e.target && e.target.classList && e.target.classList.contains('tide-tabbar')) mark(e.target);
    }, { capture: true, passive: true });
    window.addEventListener('resize', function () { document.querySelectorAll('.tide-tabbar[data-more]').forEach(mark); });

    return {
        revealCurrent: function () {
            document.querySelectorAll('.tide-tabbar').forEach(function (bar) {
                reveal(bar, bar.querySelector(':scope > [aria-pressed="true"], :scope > [aria-current="page"], :scope > [aria-selected="true"]'));
            });
        }
    };
})();

(function () {
    document.addEventListener('keydown', function (e) {
        if (e.ctrlKey || e.metaKey || e.altKey || !e.target || !e.target.closest) return;
        const tab = e.target.closest('[role="tab"]');
        const list = tab && tab.closest('[role="tablist"]');
        if (!list) return;
        const next = window.tideRoving(Array.prototype.slice.call(list.querySelectorAll('[role="tab"]:not([disabled])')), tab, e, 'x');
        if (!next) return;
        e.preventDefault();
        if (next === tab) return;
        next.focus();
        next.click();
    });
})();
