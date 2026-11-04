// Tabs (TabBar with a panel): the current tab is the tablist's one tab stop, and Left/Right/Home/End move to another
// tab and show it, as the ARIA tabs pattern has it. Showing it is a click, so .NET switches the panel and moves the tab
// stop (tabindex) when it re-renders. CSP-safe (an external file, no inline handlers); one listener for every tablist.
(function () {
    document.addEventListener('keydown', function (e) {
        if (e.ctrlKey || e.metaKey || e.altKey || !e.target || !e.target.closest) return;
        const tab = e.target.closest('[role="tab"]');
        const list = tab && tab.closest('[role="tablist"]');
        if (!list) return;
        const tabs = Array.prototype.slice.call(list.querySelectorAll('[role="tab"]:not([disabled])'));
        const at = tabs.indexOf(tab);
        let to;
        switch (e.key) {
            case 'ArrowRight': to = (at + 1) % tabs.length; break;
            case 'ArrowLeft': to = (at - 1 + tabs.length) % tabs.length; break;
            case 'Home': to = 0; break;
            case 'End': to = tabs.length - 1; break;
            default: return;
        }
        e.preventDefault();
        const next = tabs[to];
        if (!next || next === tab) return;
        next.focus();
        next.click();
    });
})();
