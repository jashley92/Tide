// Runs in <head> before first paint (an external file, so it complies with the strict CSP that blocks inline
// scripts). Applies the remembered theme and sidebar state so there's no flash of the wrong theme.
(function () {
    var root = document.documentElement;
    // The theme chosen is light, dark or system (the operating system's, and the default: nothing saved, or "system").
    // theme.js follows a change to the system's while it's chosen.
    var dark = false;
    try { dark = !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches); } catch (e) { /* no matchMedia */ }
    try {
        var t = localStorage.getItem('tide-theme');
        root.setAttribute('data-bs-theme', t === 'light' || t === 'dark' ? t : (dark ? 'dark' : 'light'));
    } catch (e) {
        root.setAttribute('data-bs-theme', dark ? 'dark' : 'light');
    }
    try {
        if (localStorage.getItem('tide-nav') === 'collapsed') root.setAttribute('data-nav', 'collapsed');
    } catch (e) { /* private mode: leave the sidebar expanded */ }
    try {
        if (localStorage.getItem('tide-rail') === 'closed') root.setAttribute('data-rail', 'closed');
    } catch (e) { /* private mode: the context rail open where it docks */ }
    try {
        if (localStorage.getItem('tide-shortcuts') === 'off') root.setAttribute('data-shortcuts', 'off');
    } catch (e) { /* private mode: shortcuts on */ }
})();
