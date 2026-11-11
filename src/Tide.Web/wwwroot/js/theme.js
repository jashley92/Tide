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
    // A key for the context rail, not the page's list: pressed in the rail, or while the rail is open over the page.
    function inRail(target) {
        return document.documentElement.hasAttribute('data-rail-over') || !!(target && target.closest && target.closest('#tide-rail'));
    }
    return { on: on, set: set, toggle: toggle, isLetter: isLetter, typing: typing, inMenu: inMenu, inRail: inRail };
})();

// Arrow keys, Home and End over a row or column of controls (a menu, a tablist, a toolbar), alike everywhere: the
// control to move to from `current` for the key pressed, or null for another key. The arrows go round. `axis` says which
// arrows count: 'x' (Left/Right), 'y' (Up/Down) or 'both'.
window.tideRoving = function (items, current, e, axis) {
    if (!items.length) return null;
    const at = items.indexOf(current), x = axis !== 'y', y = axis !== 'x';
    switch (e.key) {
        case 'ArrowRight': return x ? items[(at + 1) % items.length] : null;
        case 'ArrowDown': return y ? items[(at + 1) % items.length] : null;
        case 'ArrowLeft': return x ? items[(at - 1 + items.length) % items.length] : null;
        case 'ArrowUp': return y ? items[(at - 1 + items.length) % items.length] : null;
        case 'Home': return items[0];
        case 'End': return items[items.length - 1];
        default: return null;
    }
};

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

// The context rail (ContextRail): the region on the right of a page that has one. Where the stylesheet docks it beside
// the content (a wide window, a page held to the usual width), open or closed is remembered per browser as tide-rail
// and applied before paint by theme-init.js as data-rail="closed". Elsewhere it's an overlay, opened with
// data-rail-over and not remembered, as the phone menu isn't. The open overlay is modal, as a dialog is: the page under
// it is inert until it closes, behind a backdrop (app.css) that closes it on a click.
window.tideRail = (function () {
    var root = document.documentElement;
    // The page under the open overlay: everything in the layout but the rail. Toasts, the error bar and the reconnect
    // dialog aren't the page's, and stay live.
    var UNDER = '.tide-skip-link, .sidebar, .top-row, #main-content';
    var held = false, watch = new MutationObserver(changed);
    function rail() { return document.getElementById('tide-rail'); }
    function toggleBtn() { return document.getElementById('tide-rail-toggle'); }
    // app.css decides where the rail docks, and says so with --tide-rail-docks.
    function docks() {
        var r = rail();
        return !!r && getComputedStyle(r).getPropertyValue('--tide-rail-docks').trim() === '1';
    }
    function over() { return root.hasAttribute('data-rail-over'); }
    function isOpen() { return docks() ? root.getAttribute('data-rail') !== 'closed' : over(); }
    // While the overlay is open, the page under it is inert: out of the tab order and hidden from assistive technology,
    // so Tab stays in the rail. A docked rail leaves the page beside it as it is.
    // The markup is watched meanwhile, so nothing is left inert under a rail that has gone (a link in it opened a page
    // with none), and a dialog opened in the page under it (the palette, from Ctrl+K) closes the rail to be usable.
    function hold() {
        var on = !!rail() && over();
        if (on === held) return;
        held = on;
        document.querySelectorAll(UNDER).forEach(function (el) { el.toggleAttribute('inert', on); });
        if (on) watch.observe(document.body, { childList: true, subtree: true });
        else watch.disconnect();
    }
    function changed() {
        var r = rail();
        var dialogUnder = !!r && Array.prototype.some.call(document.querySelectorAll('[role="dialog"], [role="alertdialog"]'),
            function (d) { return !r.contains(d); });
        if (!r || dialogUnder) root.removeAttribute('data-rail-over');
        sync();
    }
    // Blazor draws the button once, as closed; this keeps what it says right, and the page under the overlay inert:
    // on load, when a page with a rail starts, on each toggle, and when the window's width changes where the rail sits.
    function sync() {
        if (over() && docks()) root.removeAttribute('data-rail-over');
        var btn = toggleBtn();
        if (btn) btn.setAttribute('aria-expanded', isOpen() ? 'true' : 'false');
        hold();
    }
    // Opens or closes the overlay. Focus goes into it as it opens, since it covers the page, and back to the button
    // as it closes.
    function setOver(open) {
        if (open) root.setAttribute('data-rail-over', '');
        else root.removeAttribute('data-rail-over');
        sync();
        var to = open ? rail() : toggleBtn();
        if (to) to.focus();
    }
    function toggle() {
        var r = rail();
        if (!r) return;
        if (!docks()) { setOver(!over()); return; }
        var close = root.getAttribute('data-rail') !== 'closed', btn = toggleBtn();
        // Focus inside a rail that's about to go would be lost: it goes to the button.
        if (close && btn && r.contains(document.activeElement)) btn.focus();
        if (close) root.setAttribute('data-rail', 'closed');
        else root.removeAttribute('data-rail');
        window.tidePrefs.set('rail', close ? 'closed' : 'open');
        sync();
    }
    // A page with a rail has started: an overlay left open on the page before doesn't open over this one.
    function start() {
        root.removeAttribute('data-rail-over');
        sync();
    }
    window.addEventListener('resize', function () { if (rail()) sync(); });
    sync();
    // Whether the page has a rail, so "]" does something (the "?" list names the key only then).
    function has() { return !!rail(); }
    return { toggle: toggle, start: start, over: over, has: has };
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
    // The Context button, and the rail's own Close (shown only while the rail is, so toggling closes it). A click on the
    // overlay's backdrop (drawn by the rail itself, beside it) closes it too.
    if (e.target.closest('#tide-rail-toggle, #tide-rail-close')) { window.tideRail.toggle(); return; }
    if (e.target.id === 'tide-rail' && window.tideRail.over() && e.clientX < e.target.getBoundingClientRect().left) { window.tideRail.toggle(); return; }
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

// The context rail's keys. "]" shows or hides it: a single character, so it's off with the letter shortcuts and while
// typing or in a menu or dialog (the top bar's Context button still works). AltGr counts as no modifier, for the
// keyboards that type "]" with it. Escape closes the rail while it's an overlay, unless a menu or dialog has the key;
// that Escape is the rail's alone, so the page under it (a list's Esc, Triage's reading pane) doesn't hear it as well.
document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape' && e.key !== ']') return;
    if (e.isComposing || e.defaultPrevented || window.tideShortcuts.inMenu(e.target)) return;
    if (e.key === 'Escape') {
        if (!window.tideRail.over()) return;
        e.preventDefault();
        e.stopImmediatePropagation();
        window.tideRail.toggle();
        return;
    }
    if (e.metaKey || ((e.ctrlKey || e.altKey) && !e.getModifierState('AltGraph'))) return;
    if (!window.tideShortcuts.on() || window.tideShortcuts.typing(e.target)) return;
    if (!document.getElementById('tide-rail')) return;
    e.preventDefault();
    window.tideRail.toggle();
});
