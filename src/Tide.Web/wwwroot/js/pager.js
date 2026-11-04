// Pager: moving to another page brings the top of its list back into view when it has scrolled above the window, so
// the new page is read from its first row. The new page comes from the server a moment after the click, so the scroll
// waits for the list to change (or gives up after two seconds). CSP-safe (an external file, no inline handlers); one
// listener for every pager.
(function () {
    document.addEventListener('click', function (e) {
        const button = e.target && e.target.closest ? e.target.closest('.tide-pager button') : null;
        if (!button || button.disabled) return;
        const list = button.closest('.tide-dt, .tide-card');
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
