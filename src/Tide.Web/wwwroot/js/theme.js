// Theme and sidebar collapse. The initial state is applied by theme-init.js in <head> before paint; this changes and
// remembers it. Bootstrap 5.3 reads data-bs-theme; the sidebar CSS keys off data-nav="collapsed".
// The theme chosen (tide-theme) is "light", "dark" or "system", the default: the operating system's, followed live, so
// a system that turns dark at dusk turns TIDE dark with it. data-bs-theme is always the one in force, light or dark.
window.tideTheme = (function () {
    var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
    function systemDark() { return !!(media && media.matches); }
    // The one in force: "light" or "dark".
    function current() { return document.documentElement.getAttribute('data-bs-theme') || 'light'; }
    // The one chosen: "light", "dark" or "system". Kept here as well, for a browser that won't store it.
    var picked = null;
    function chosen() {
        var t = picked;
        try { t = localStorage.getItem('tide-theme') || t; } catch (e) { /* private mode */ }
        return t === 'light' || t === 'dark' ? t : 'system';
    }
    function resolve(t) { return t === 'system' ? (systemDark() ? 'dark' : 'light') : t; }
    function show(t) { document.documentElement.setAttribute('data-bs-theme', resolve(t)); }
    // Chosen from the account menu, the page crossfades into the new theme (a view transition, timed in 10-shell.css)
    // rather than cutting to it; at once with less motion asked for, in a browser without view transitions, or when
    // the theme in force doesn't change (System chosen while the system's is the one showing).
    function apply(t) {
        if (t !== 'light' && t !== 'dark') t = 'system';
        picked = t;
        try { localStorage.setItem('tide-theme', t); } catch (e) { /* private mode: in force until the page reloads */ }
        var ts = window.tideShortcuts;
        if (document.startViewTransition && ts && !ts.quiet() && resolve(t) !== current()) document.startViewTransition(function () { show(t); });
        else show(t);
    }
    function toggle() { apply(current() === 'dark' ? 'light' : 'dark'); }
    if (media) {
        var follow = function () { if (chosen() === 'system') show('system'); };
        if (media.addEventListener) media.addEventListener('change', follow);
        else if (media.addListener) media.addListener(follow);
    }
    return { toggle: toggle, apply: apply, current: current, chosen: chosen };
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
    // In a window too short for the whole menu, it scrolls: the current page's link is scrolled into the part that shows
    // (the nearest edge), on load and after each navigation (NavMenu calls this). Only the menu scrolls, never the page,
    // which scrollIntoView could also move.
    function reveal() {
        var nav = document.getElementById('tide-nav-links'), menu = nav && nav.querySelector('.tide-nav');
        var link = menu && menu.querySelector('a.active, a[aria-current="page"]');
        if (!link || menu.scrollHeight <= menu.clientHeight) return;
        var r = link.getBoundingClientRect(), box = menu.getBoundingClientRect();
        if (r.top < box.top) menu.scrollTop -= box.top - r.top;
        else if (r.bottom > box.bottom) menu.scrollTop += r.bottom - box.bottom;
    }
    // The prerendered page, before the circuit renders: the collapse button says what the page shows.
    syncCollapse();
    reveal();
    return {
        toggle: function () { apply(!collapsed()); }, collapsed: collapsed, isOpen: isOpen, setOpen: setOpen, reveal: reveal,
        start: function () { syncCollapse(); reveal(); }
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
    // The person asks for less motion: scroll and slide at once, not smoothly.
    // Read once; .matches follows a change to the system setting.
    var motion = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
    function still() { return !!(motion && motion.matches); }
    // Nothing should move: less motion asked for, a tab nobody is looking at, or a browser that can't animate.
    function quiet() { return still() || document.hidden || !document.body.animate; }
    // A motion token from the stylesheet (00-tokens.css): its text (an easing), or as ms (a duration, one in seconds too). Read once each
    // (they're the same in both themes), so a loop asking for one doesn't make the browser work out the page's style; an
    // easing that isn't there (the stylesheet didn't load) is the browser's own, so an animation using it still plays.
    var tokens = {};
    function token(name) {
        if (!(name in tokens)) tokens[name] = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        return tokens[name] || (name.indexOf('--tide-ease') === 0 ? 'ease-out' : '');
    }
    function ms(name) { var t = token(name); return (parseFloat(t) || 0) * (/\ds$/.test(t) ? 1000 : 1); }
    // A copy of something to fly across the page (a card held, a row promoted): no ids to clash with the original's,
    // hidden from assistive tech, and taking no clicks or focus. Given a rect, it is laid over the page there (its class
    // makes it fixed). Marked data-tide-copy, so flip.js never takes a list inside it for one of the page's.
    function copy(el, rect) {
        var c = el.cloneNode(true);
        c.querySelectorAll('[id]').forEach(function (n) { n.removeAttribute('id'); });
        c.removeAttribute('id');
        c.setAttribute('aria-hidden', 'true');
        c.setAttribute('data-tide-copy', '');
        c.inert = true;
        if (rect) {
            c.style.left = rect.left + 'px';
            c.style.top = rect.top + 'px';
            c.style.width = rect.width + 'px';
            c.style.height = rect.height + 'px';
            document.body.appendChild(c);
        }
        return c;
    }
    // Plays frames on a copy laid over the page, which goes when they're done (or cancelled). The animation is returned.
    function drift(c, frames, opts) {
        var a = c.animate(frames, Object.assign({ fill: 'forwards' }, opts));
        var gone = function () { c.remove(); };
        a.finished.then(gone, gone);
        return a;
    }
    // How long something takes to travel so far (px): a short hop is quick, a long way has room, from --tide-dur-fast
    // to --tide-dur-travel (growing with the square root of the distance, as the eye judges it).
    function travel(px) { return Math.min(ms('--tide-dur-travel'), ms('--tide-dur-fast') + 10 * Math.sqrt(Math.max(0, px))); }
    // Flies a copy of el from a rect (where it was seen) to where el is now (or to), el itself transparent meanwhile:
    // still there to take focus, which may go to it before it lands, and its copy shows the focus ring meanwhile. A
    // flight already under way for el is taken over from where its copy is, so a card moved again mid-flight turns
    // towards its new place rather than jumping or showing twice. A promise of the landing.
    var flights = new WeakMap();
    function fly(el, from, to) {
        to = to || el.getBoundingClientRect();
        var prev = flights.get(el);
        if (prev) {
            var at = prev.copy.getBoundingClientRect();
            from = { left: at.left, top: at.top, width: from.width, height: from.height };
            prev.anim.cancel();
        }
        var c = copy(el, from), dx = to.left - from.left, dy = to.top - from.top;
        c.classList.add('tide-flying');
        if (el.contains(document.activeElement)) c.setAttribute('data-tide-focused', '');
        el.setAttribute('data-tide-landing', '');
        var a = drift(c, [{ transform: 'none' }, { transform: 'translate(' + dx + 'px, ' + dy + 'px)' }],
            { duration: travel(Math.hypot(dx, dy)), easing: token('--tide-ease-out') });
        flights.set(el, { copy: c, anim: a });
        return a.finished.catch(function () { }).then(function () {
            if (flights.get(el) && flights.get(el).anim !== a) return false;   // taken over by a later flight
            flights.delete(el);
            el.removeAttribute('data-tide-landing');
            return true;
        });
    }
    // Sets a data attribute for a moment (--tide-dur-signature): a card's or a tab's brackets locking on. On again
    // before it went, it starts over (its own timer, not the last one's, takes it off).
    var pulses = new WeakMap();
    function pulse(el, attr) {
        if (el.hasAttribute(attr)) { el.removeAttribute(attr); void el.offsetWidth; }   // a reflow, only to play it again
        el.setAttribute(attr, '');
        clearTimeout(pulses.get(el));
        pulses.set(el, setTimeout(function () { el.removeAttribute(attr); }, ms('--tide-dur-signature')));
    }
    return { on: on, set: set, toggle: toggle, isLetter: isLetter, typing: typing, inMenu: inMenu, inRail: inRail, still: still, quiet: quiet, token: token, ms: ms, travel: travel, copy: copy, drift: drift, fly: fly, pulse: pulse };
})();

// A save refused for a field (a title left empty): .NET says which, after the toast that says why. The page scrolls it to
// the middle of the window and focuses it, and once it's there threat-colored brackets close in on it, hold, and fade
// over --tide-dur-attention. The field is marked aria-invalid until it's next changed, unless it says so itself. With less
// motion asked for, it jumps there and the brackets show where they end, then fade. Whether the field was found.
window.tideInvalid = (function () {
    var ts = window.tideShortcuts;
    function lockOn(el) {
        var r = el.getBoundingClientRect();
        if (!document.body.animate || !r.width) return;
        var b = document.createElement('div');
        b.className = 'tide-invalid-lock';
        b.setAttribute('aria-hidden', 'true');
        var place = function () {
            var at = el.getBoundingClientRect();
            b.style.left = at.left + 'px'; b.style.top = at.top + 'px'; b.style.width = at.width + 'px'; b.style.height = at.height + 'px';
        };
        place();
        document.body.appendChild(b);
        // It's fixed over the field, so it follows if the page scrolls meanwhile.
        document.addEventListener('scroll', place, true);
        var shut = ms('--tide-dur-base') / ms('--tide-dur-attention');
        var frames = ts.still()
            ? [{ opacity: 1 }, { opacity: 1, offset: .6 }, { opacity: 0 }]
            : [{ opacity: 0, transform: 'scale(1.06)' }, { opacity: 1, transform: 'none', offset: shut }, { opacity: 1, offset: .6 }, { opacity: 0 }];
        var a = ts.drift(b, frames, { duration: ms('--tide-dur-attention'), easing: token('--tide-ease-out') });
        var off = function () { document.removeEventListener('scroll', place, true); };
        a.finished.then(off, off);
    }
    function ms(name) { return ts.ms(name); }
    function token(name) { return ts.token(name); }
    function show(target) {
        var el = typeof target === 'string' ? document.querySelector(target) : target;
        if (!el) return false;
        if (!el.hasAttribute('aria-invalid')) {
            el.setAttribute('aria-invalid', 'true');
            var fixed = function () { el.removeAttribute('aria-invalid'); el.removeEventListener('input', fixed); el.removeEventListener('change', fixed); };
            el.addEventListener('input', fixed);
            el.addEventListener('change', fixed);
        }
        // The brackets close in once the page has stopped moving: at once when it didn't need to (or jumped), else when
        // its smooth scroll ends (or after the longest a move takes, for a browser without scrollend).
        var moved = false, started = false;
        var go = function () {
            if (started) return;
            started = true;
            document.removeEventListener('scroll', seen, true);
            document.removeEventListener('scrollend', go, true);
            lockOn(el);
        };
        var seen = function () { moved = true; };
        document.addEventListener('scroll', seen, true);
        document.addEventListener('scrollend', go, true);
        el.scrollIntoView({ block: 'center', behavior: ts.still() ? 'auto' : 'smooth' });
        el.focus({ preventScroll: true });
        requestAnimationFrame(function () {
            requestAnimationFrame(function () {
                if (!moved || ts.still()) go();
                else setTimeout(go, ms('--tide-dur-travel'));
            });
        });
        return true;
    }
    return { show: show };
})();

// A page that's slow to come (the server still rendering it): after --tide-dur-patience a thin bar scans under the top bar
// (.tide-nav-cue, in MainLayout), until .NET says the new page has rendered (MainLayout, done()). Nothing shows for a
// page that comes sooner. Navigation here is the interactive router's, so it starts when the address changes: a link
// followed (history.pushState) or Back and Forward (popstate). The state is on <html>, which Blazor never re-renders.
window.tideNavCue = (function () {
    var timer = 0;
    function done() {
        clearTimeout(timer);
        timer = 0;
        document.documentElement.removeAttribute('data-tide-waiting');
    }
    function start() {
        done();
        timer = setTimeout(function () { document.documentElement.setAttribute('data-tide-waiting', ''); }, window.tideShortcuts.ms('--tide-dur-patience'));
    }
    // The same address again (its own link clicked), or one that differs only after # (a link within the page), renders
    // nothing new, so there's nothing to wait for.
    function page(href) { return href.split('#')[0]; }
    var at = page(location.href);
    function moved() {
        var now = page(location.href);
        if (now !== at) { at = now; start(); }
    }
    var push = history.pushState;
    history.pushState = function () {
        var r = push.apply(this, arguments);
        moved();
        return r;
    };
    window.addEventListener('popstate', moved);
    return { done: done };
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
    // Opening, the page is made inert a frame after the rail starts to slide in: marking the whole page inert means
    // working out its style again, which in the same frame would hold the slide back. Focus is already in the rail.
    // Closing, it's at once, so focus can go back to the button.
    function hold() {
        var r = rail(), on = !!r && over();
        // Open over the page, the rail is a modal dialog, named as before; docked or closed it's the aside's own
        // complementary landmark.
        if (r) {
            if (on) { r.setAttribute('role', 'dialog'); r.setAttribute('aria-modal', 'true'); }
            else { r.removeAttribute('role'); r.removeAttribute('aria-modal'); }
        }
        if (on === held) return;
        held = on;
        var under = function () { if (held === on) document.querySelectorAll(UNDER).forEach(function (el) { el.toggleAttribute('inert', on); }); };
        if (on) requestAnimationFrame(function () { requestAnimationFrame(under); });
        else under();
        if (on) watch.observe(document.body, { childList: true, subtree: true });
        else watch.disconnect();
    }
    // A change that adds or takes away an element: text changing in the page (a clock, a count) can't bring a dialog or
    // take the rail away, so the dialogs aren't searched and measured for it.
    function elements(nodes) {
        return Array.prototype.some.call(nodes, function (n) { return n.nodeType === 1; });
    }
    function changed(records) {
        if (records && !records.some(function (m) { return elements(m.addedNodes) || elements(m.removedNodes); })) return;
        var r = rail();
        // Only a dialog that's showing: the reconnect dialog is always in the markup, hidden until it's needed.
        // A guide's step pointing into the rail (data-guide-card) isn't one: it's drawn over the rail it opened.
        var dialogUnder = !!r && Array.prototype.some.call(document.querySelectorAll('[role="dialog"], [role="alertdialog"]'),
            function (d) { return !r.contains(d) && !d.hasAttribute('data-guide-card') && d.getClientRects().length > 0; });
        // A dialog opening over the page closes the overlay. Focus that was in it (or dropped to the body as it went) moves
        // to the Context button first, so the dialog, when it closes, gives focus back there and not to the body.
        var lost = dialogUnder && over() && (r.contains(document.activeElement) || document.activeElement === document.body);
        if (!r || dialogUnder) root.removeAttribute('data-rail-over');
        sync();
        var btn = lost && toggleBtn();
        if (btn) btn.focus();
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
    // Opened again while the last one is still sliding off, the sheet turns back from where it had got to (the copy goes,
    // the rail itself takes over from there), rather than vanishing and coming in again from the edge.
    function setOver(open) {
        var back = null;
        if (leaving) {
            if (open && !window.tideShortcuts.still()) back = getComputedStyle(leaving.effect.target).translate;
            leaving.cancel();
        }
        if (!open && over()) leave();
        if (open) root.setAttribute('data-rail-over', '');
        else root.removeAttribute('data-rail-over');
        sync();
        var r = rail();
        if (back && r) r.animate([{ translate: back }, { translate: '0 0' }], { duration: window.tideShortcuts.ms('--tide-dur-panel'), easing: window.tideShortcuts.token('--tide-ease-out') });
        var to = open ? r : toggleBtn();
        if (to) to.focus();
    }
    // Closing, the sheet slides back off the window's right edge as the backdrop fades: a copy of it does (scrolled where
    // the rail was), as the rail itself is hidden at once (60-rail-and-pages.css). The sheet stays opaque; only the
    // backdrop fades. With reduced motion the whole copy fades where it is.
    var leaving = null;
    function leave() {
        var r = rail(), ts = window.tideShortcuts;
        if (!r || !document.body.animate || document.hidden) return;
        var c = ts.copy(r), body = r.querySelector('.tide-rail-body');
        c.classList.add('tide-leaving');
        c.removeAttribute('role');
        c.removeAttribute('aria-modal');
        document.body.appendChild(c);
        if (body && body.scrollTop) c.querySelector('.tide-rail-body').scrollTop = body.scrollTop;
        var opts = { duration: ts.ms('--tide-dur-panel-exit'), easing: ts.token('--tide-ease-in') };
        var a = leaving = ts.still() ? ts.drift(c, [{ opacity: 1 }, { opacity: 0 }], opts) : ts.drift(c, [{ translate: '0 0' }, { translate: '100% 0' }], opts);
        if (!ts.still()) ['::before', '::after'].forEach(function (p) { c.animate([{ opacity: 1 }, { opacity: 0 }], Object.assign({ pseudoElement: p, fill: 'forwards' }, opts)); });
        var done = function () { if (leaving === a) leaving = null; };
        a.finished.then(done, done);
    }
    function toggle() {
        var r = rail();
        // A rail the page lays out in its own flow (the article editor's Preview on a narrow window) is the page's: there's
        // nothing to show or hide, and an overlay would cover the switch that put it there.
        if (!r || (!over() && getComputedStyle(r).position === 'static')) return;
        if (!docks()) { setOver(!over()); return; }
        var close = root.getAttribute('data-rail') !== 'closed', btn = toggleBtn();
        // Focus inside a rail that's about to go would be lost: it goes to the button.
        if (close && btn && r.contains(document.activeElement)) btn.focus();
        if (close) root.setAttribute('data-rail', 'closed');
        else root.removeAttribute('data-rail');
        window.tidePrefs.set('rail', close ? 'closed' : 'open');
        sync();
    }
    // Opens the rail if it's closed, to show something that came to it (files dropped on the page). Docked, that isn't
    // remembered: the person didn't choose it.
    function show() {
        if (!rail() || isOpen()) return;
        if (!docks()) { setOver(true); return; }
        root.removeAttribute('data-rail');
        sync();
    }
    // Shuts a rail show() opened, again without remembering it: the overlay closes; a docked rail goes back to closed.
    function hide() {
        if (!rail()) return;
        if (over()) { setOver(false); return; }
        if (docks() && root.getAttribute('data-rail') !== 'closed') { root.setAttribute('data-rail', 'closed'); sync(); }
    }
    // A page with a rail has started: an overlay left open on the page before doesn't open over this one.
    function start() {
        root.removeAttribute('data-rail-over');
        sync();
    }
    window.addEventListener('resize', function () { if (rail()) sync(); });
    // Something in the closed rail asks to be seen (files that came to its Files box: paste-drop.js).
    document.addEventListener('tide:reveal', function (e) {
        var r = rail();
        if (!r || !r.contains(e.target)) return;
        show();
        e.target.scrollIntoView({ block: 'nearest' });
    });
    sync();
    // Whether the page has a rail, so "]" does something (the "?" list names the key only then).
    function has() { return !!rail(); }
    return { toggle: toggle, show: show, hide: hide, start: start, over: over, has: has };
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
    if (e.target.id === 'tide-rail' && window.tideRail.over()) {
        var box = e.target.getBoundingClientRect();
        if (e.clientX < box.left || e.clientX > box.right || e.clientY < box.top || e.clientY > box.bottom) { window.tideRail.toggle(); return; }
    }
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
    // The open overlay is a dialog itself: only a menu or dialog inside it, or elsewhere, keeps the key.
    var holder = e.target && e.target.closest && e.target.closest('[role="menu"], [role="dialog"], [role="alertdialog"]');
    if (e.isComposing || e.defaultPrevented || (holder && holder.id !== 'tide-rail')) return;
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
