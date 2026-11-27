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

    // Only a tab reached from the keyboard is scrolled to: one pressed with a mouse or a finger takes focus as the press
    // starts, and scrolling then would move another tab under the pointer before the click lands (the page reveals the
    // new current tab once the click has chosen it).
    let pointing = false;
    document.addEventListener('pointerdown', function () { pointing = true; }, { capture: true, passive: true });
    document.addEventListener('keydown', function () { pointing = false; }, true);
    document.addEventListener('focusin', function (e) {
        const bar = e.target && e.target.parentElement;
        if (bar && bar.classList.contains('tide-tabbar')) pointing ? mark(bar) : reveal(bar, e.target);
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

// The current tab's mark slides to the tab chosen, rather than jumping: a page tab row's underline, and a switch's
// (tide-seg) outline, which locks onto the option chosen as its fill fades in. Blazor moves the current tab by changing
// two tabs' classes in one render; the mark is a line or an outline laid over the page from the old one, moved and
// stretched by transform to the new (the new one's own underline hidden meanwhile). Without motion it just changes.
(function () {
    const SLIDES = [
        { bar: '.tide-tabbar-page', item: '.tide-tabbar-item', on: 'active', kind: 'is-line' },
        { bar: '.tide-seg', item: '.tide-seg-opt', on: 'on', kind: 'is-box' },
    ];

    function slide(s, from, to) {
        const ts = window.tideShortcuts;
        if (ts.quiet()) return;
        const a = from.getBoundingClientRect(), b = to.getBoundingClientRect();
        if (!a.width || !b.width) return;
        const line = s.kind === 'is-line', m = document.createElement('div');
        m.className = 'tide-tab-slider ' + s.kind;
        m.setAttribute('aria-hidden', 'true');
        m.setAttribute('data-tide-copy', '');
        m.style.left = a.left + 'px';
        m.style.top = (line ? a.bottom - 2 : a.top) + 'px';
        m.style.width = a.width + 'px';
        m.style.height = (line ? 2 : a.height) + 'px';
        document.body.appendChild(m);
        to.setAttribute('data-tide-sliding', '');
        const dx = b.left - a.left;
        ts.drift(m, [{ transform: 'none' }, { transform: 'translateX(' + dx + 'px) scaleX(' + b.width / a.width + ')' }],
            { duration: ts.travel(Math.abs(dx)), easing: ts.token('--tide-ease-out') })
            .finished.catch(function () { }).then(function () { to.removeAttribute('data-tide-sliding'); });
    }

    function changed(records) {
        const moves = new Map();
        records.forEach(function (m) {
            const el = m.target;
            SLIDES.forEach(function (s) {
                if (!el.matches || !el.matches(s.item)) return;
                const bar = el.closest(s.bar);
                if (!bar) return;
                const was = (' ' + (m.oldValue || '') + ' ').indexOf(' ' + s.on + ' ') >= 0, is = el.classList.contains(s.on);
                if (was === is) return;
                const move = moves.get(bar) || { s: s };
                if (is) move.to = el; else move.from = el;
                moves.set(bar, move);
            });
        });
        moves.forEach(function (move) { if (move.from && move.to && move.from.isConnected) slide(move.s, move.from, move.to); });
    }

    function start() {
        if (window.MutationObserver) new MutationObserver(changed).observe(document.body, { subtree: true, attributes: true, attributeFilter: ['class'], attributeOldValue: true });
    }
    if (document.body) start(); else document.addEventListener('DOMContentLoaded', start);
})();
