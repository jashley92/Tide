// Light/dark theme and sidebar collapse. The initial state is applied by theme-init.js in <head> before paint; this
// flips and remembers it. Bootstrap 5.3 reads data-bs-theme; the sidebar CSS keys off data-nav="collapsed".
window.tideTheme = (function () {
    function current() { return document.documentElement.getAttribute('data-bs-theme') || 'light'; }
    function apply(t) {
        document.documentElement.setAttribute('data-bs-theme', t);
        try { localStorage.setItem('tide-theme', t); } catch (e) { /* private mode */ }
    }
    function toggle() { apply(current() === 'dark' ? 'light' : 'dark'); }
    return { toggle: toggle, apply: apply, current: current };
})();

window.tideNav = (function () {
    function collapsed() { return document.documentElement.getAttribute('data-nav') === 'collapsed'; }
    // The collapse button (at the top of the sidebar) says whether the sidebar is expanded, and its tooltip what a click
    // does. Blazor renders it once and never changes those attributes, so this keeps them right: on load, after the
    // circuit's first render (start), and on each toggle.
    function syncCollapse() {
        var btn = document.getElementById('nav-collapse-toggle');
        if (!btn) return;
        btn.setAttribute('aria-expanded', collapsed() ? 'false' : 'true');
        btn.setAttribute('title', collapsed() ? 'Expand the sidebar' : 'Collapse the sidebar');
    }
    function apply(isCollapsed) {
        if (isCollapsed) document.documentElement.setAttribute('data-nav', 'collapsed');
        else document.documentElement.removeAttribute('data-nav');
        try { localStorage.setItem('tide-nav', isCollapsed ? 'collapsed' : 'expanded'); } catch (e) { /* private mode */ }
        syncCollapse();
    }
    // The More group this replaced remembered itself here; that key is no longer read.
    try { localStorage.removeItem('tide-nav-more'); } catch (e) { /* private mode */ }
    // The phone menu (≤640px): the links open under the top bar. Not remembered: it starts closed on every page load.
    function isOpen() { return document.documentElement.hasAttribute('data-nav-open'); }
    function setOpen(open) {
        if (open) document.documentElement.setAttribute('data-nav-open', '');
        else document.documentElement.removeAttribute('data-nav-open');
        var btn = document.getElementById('nav-menu-toggle');
        if (btn) btn.setAttribute('aria-expanded', open ? 'true' : 'false');
    }
    // The prerendered page, before the circuit renders: the collapse button says what the page shows.
    syncCollapse();
    return {
        toggle: function () { apply(!collapsed()); }, collapsed: collapsed, isOpen: isOpen, setOpen: setOpen, start: syncCollapse
    };
})();

// Single-letter keyboard shortcuts (triage, present mode) can be turned off (WCAG 2.1.4), for speech input and
// anyone who types into the page by accident. Remembered per browser; arrows and Page Up/Down aren't affected.
window.tideShortcuts = (function () {
    // The state is on <html> (set before paint by theme-init.js), which Blazor never re-renders. The account menu
    // (ProfileMenu) reads and sets it.
    function on() { return document.documentElement.getAttribute('data-shortcuts') !== 'off'; }
    function set(enabled) {
        if (enabled) document.documentElement.removeAttribute('data-shortcuts');
        else document.documentElement.setAttribute('data-shortcuts', 'off');
        try { localStorage.setItem('tide-shortcuts', enabled ? 'on' : 'off'); } catch (e) { /* private mode */ }
    }
    function toggle() { set(!on()); }
    // A key that is a single letter is a character shortcut.
    function isLetter(e) { return e.key.length === 1 && /[a-z]/i.test(e.key); }
    // Focus in a field: the keys are being typed, not pressed as shortcuts.
    function typing(target) {
        if (!target) return false;
        const tag = (target.tagName || '').toLowerCase();
        return tag === 'input' || tag === 'textarea' || tag === 'select' || target.isContentEditable;
    }
    // Focus in a menu or dialog (the account menu, a confirmation): the keys are its own, not the page's.
    function inMenu(target) {
        return !!(target && target.closest && target.closest('[role="menu"], [role="dialog"], [role="alertdialog"]'));
    }
    return { on: on, set: set, toggle: toggle, isLetter: isLetter, typing: typing, inMenu: inMenu };
})();

// Small choices remembered per browser, for .NET to read back (the hunt panel's last query language). A blocked or
// private-mode store just forgets: get answers null.
window.tidePrefs = (function () {
    function get(name) {
        try { return localStorage.getItem('tide-' + name); } catch (e) { return null; }
    }
    function set(name, value) {
        try { localStorage.setItem('tide-' + name, String(value)); } catch (e) { /* private mode */ }
    }
    return { get: get, set: set };
})();

// Copies text to the clipboard (the Copy buttons on indicators). Returns whether it worked.
window.tideCopy = async function (text) {
    try { await navigator.clipboard.writeText(text); return true; }
    catch (e) { return false; }
};

// Delegated handlers, attached from an external file so they satisfy the strict CSP that blocks inline handlers.
document.addEventListener('click', function (e) {
    if (!e.target || !e.target.closest) return;
    if (e.target.closest('#nav-collapse-toggle')) { window.tideNav.toggle(); return; }
    if (e.target.closest('#nav-menu-toggle')) { window.tideNav.setOpen(!window.tideNav.isOpen()); return; }
    // Choosing a link closes the phone menu, so the page it opens is what you see.
    if (window.tideNav.isOpen() && e.target.closest('#tide-nav-links a')) window.tideNav.setOpen(false);
    // The circuit-disconnect modal: retry the connection, or reload when the server lost the session.
    if (e.target.closest('#components-reconnect-retry')) {
        if (window.Blazor && window.Blazor.reconnect) window.Blazor.reconnect();
        return;
    }
    if (e.target.closest('#components-reconnect-reload')) { location.reload(); return; }
    // The error bar's dismiss button (a button, so the keyboard reaches it); Blazor wires it too once the bar shows.
    if (e.target.closest('#blazor-error-ui .dismiss')) {
        var bar = document.getElementById('blazor-error-ui');
        if (bar) bar.style.display = 'none';
    }
});

// Escape closes the phone menu and returns focus to its button.
document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape' || !window.tideNav.isOpen()) return;
    window.tideNav.setOpen(false);
    var btn = document.getElementById('nav-menu-toggle');
    if (btn) btn.focus();
});
