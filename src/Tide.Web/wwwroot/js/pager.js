// Pager: moving to another page brings the top of its list back into view when it has scrolled above the window, so
// the new page is read from its first row. The new page comes from the server a moment after the click, so the scroll
// waits for the list to change (or gives up after two seconds). CSP-safe (an external file, no inline handlers); one
// listener for every pager, whether its pages are buttons or links (a plain click on a link; one opening a new tab
// leaves this page where it is).
(function () {
    document.addEventListener('click', function (e) {
        const button = e.target && e.target.closest ? e.target.closest('.tide-pager button, .tide-pager a[href]') : null;
        if (!button || button.disabled) return;
        if (button.tagName === 'A' && (e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey)) return;
        const list = button.closest('.tide-dt, .tide-paged, .tide-card');
        if (!list || list.getBoundingClientRect().top >= 0) return;
        let done = false;
        const watch = new MutationObserver(function () {
            if (done) return;
            done = true;
            watch.disconnect();
            if (list.isConnected && list.getBoundingClientRect().top < 0) list.scrollIntoView({ block: 'start' });
        });
        watch.observe(list, { childList: true, subtree: true, characterData: true });
        setTimeout(function () { if (!done) { done = true; watch.disconnect(); } }, 2000);
    });
})();
