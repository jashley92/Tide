// Tabs (TabBar with a panel): the current tab is the tablist's one tab stop, and Left/Right/Home/End move to another
// tab and show it, as the ARIA tabs pattern has it (tideRoving, in theme.js). Showing it is a click, so .NET switches
// the panel and moves the tab stop (tabindex) when it re-renders. CSP-safe (an external file, no inline handlers); one
// listener for every tablist.
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
