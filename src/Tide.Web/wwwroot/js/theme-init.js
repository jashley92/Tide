// Runs in <head> before first paint (an external file, so it complies with the strict CSP that blocks inline
// scripts). Applies the remembered theme and sidebar state so there's no flash of the wrong theme.
(function () {
    var root = document.documentElement;
    try {
        var t = localStorage.getItem('tide-theme');
        if (!t) { t = (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) ? 'dark' : 'light'; }
        root.setAttribute('data-bs-theme', t);
    } catch (e) {
        root.setAttribute('data-bs-theme', 'light');
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
