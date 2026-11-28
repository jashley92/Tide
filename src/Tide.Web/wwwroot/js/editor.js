// The article editor's section pills (ArticleEditorPanel). CSP-safe: an external file. Jumping to a section scrolls its
// heading to the top (below the top bar and the pills, by the scroll padding in app.css) and then focuses it, where a
// plain focus() would only nudge it into view at the bottom. Smooth unless the person asks for less motion. A
// placeholder in the preview jumps to a field instead: it's scrolled to the middle and focused.
// The brief stepper (BriefStepper): K and J press its Previous and Next links, as they move in triage, unless focus is
// in a field, a menu or a dialog, or single-character shortcuts are switched off. Alt+Left and Alt+Right stay the
// browser's Back and Forward.
// Its reader preview (ReaderPreview): keys 1 and 2 pick the Team or Company-wide edition, unless focus is in a field, a
// dialog is open, or single-character shortcuts are switched off; the edition shown is kept in the address
// (?edition=company) without a navigation, so the editor's unsaved-changes guard isn't asked.
// The editor's own keys (ArticleEditorPanel, for someone who can edit): G then a letter goes to a section's first field
// (B bottom line, U bullets, W why it matters, R recommended actions, I indicators, T tags); N goes to the next gap the
// readiness checks flag, after the section focus is in; Ctrl+Shift+Enter saves, marks ready and goes on to the next
// article on today's brief. The letters pause as Triage's do (rows.js): while typing in a field, with focus in a menu,
// a dialog or the context rail, under an open confirmation, and when single-character shortcuts are off. A G not
// followed by one of those letters within a second and a half is let go, and the next key is the page's as usual.
// Ctrl+Shift+Enter works from a field too (it isn't a character), but not in a menu or dialog.
window.tideEditor = (function () {
    let ref = null, keysRef = null, gWait = 0;
    const shortcuts = window.tideShortcuts, typing = shortcuts.typing, inMenu = shortcuts.inMenu;

    function onStep(e) {
        if (e.defaultPrevented) return;   // the palette took it (Ctrl+K, then typing before its box is up)
        const key = (e.key || '').toLowerCase();
        if (e.ctrlKey || e.metaKey || e.altKey || (key !== 'j' && key !== 'k')) return;
        const stepper = document.querySelector('.tide-stepper');
        if (!stepper || typing(e.target) || inMenu(e.target) || document.querySelector('.tide-modal')) return;
        if (!window.tideShortcuts.on()) return;   // single characters: off with the switch
        e.preventDefault();
        const link = stepper.querySelector('a[href][data-step="' + (key === 'k' ? 'prev' : 'next') + '"]');
        if (link) link.click();
    }
    document.addEventListener('keydown', onStep);

    function onKey(e) {
        if (!ref || e.defaultPrevented || (e.key !== '1' && e.key !== '2')) return;
        if (e.ctrlKey || e.metaKey || e.altKey || typing(e.target)) return;
        // Focus in a menu or dialog (the account menu): the keys are its own.
        if (inMenu(e.target)) return;
        if (document.querySelector('.tide-modal')) return;
        if (!window.tideShortcuts.on()) return;   // a single character: off with the switch
        e.preventDefault();
        ref.invokeMethodAsync('OnEditionKey', e.key).catch(function () { });
    }

    // The letters after G that name a section.
    const sections = ['b', 'u', 'w', 'r', 'i', 't'];

    function letGo() { clearTimeout(gWait); gWait = 0; }

    // The section focus is in, by its heading's id (sec-bluf), for N to go on from; none above the first or outside them.
    function sectionAt(el) {
        const section = el && el.closest ? el.closest('section[aria-labelledby]') : null;
        return section ? section.getAttribute('aria-labelledby') : null;
    }

    function send(key, at) {
        if (keysRef) keysRef.invokeMethodAsync('OnEditorKey', key, at).catch(function () { });
    }

    function onEditorKey(e) {
        if (!keysRef) return;
        if (e.defaultPrevented || e.isComposing) { letGo(); return; }   // another's key (J, K, 1, 2): a G waiting goes
        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && e.shiftKey && !e.altKey) {
            if (e.repeat || inMenu(e.target) || document.querySelector('.tide-modal')) return;
            e.preventDefault();
            send('ready-next', null);
            return;
        }
        if (e.key === 'Shift' || e.key === 'CapsLock') return;   // on the way to a capital: the G still waits
        if (e.ctrlKey || e.metaKey || e.altKey) { letGo(); return; }
        if (document.querySelector('.tide-modal') || shortcuts.inRail(e.target)) return;
        if (typing(e.target) || inMenu(e.target)) { letGo(); return; }
        if (!shortcuts.on() && shortcuts.isLetter(e)) return;
        const key = (e.key || '').toLowerCase();
        if (gWait) {
            letGo();
            if (sections.indexOf(key) < 0) return;
            e.preventDefault();
            send('go-' + key, null);
            return;
        }
        if (key === 'g') {
            e.preventDefault();
            gWait = setTimeout(letGo, 1500);
        } else if (key === 'n') {
            e.preventDefault();
            send('gap', sectionAt(document.activeElement));
        }
    }

    return {
        // Starts the editor's own keys for its .NET object, or stops them (null).
        editorKeys: function (dotnetRef) {
            document.removeEventListener('keydown', onEditorKey);
            letGo();
            keysRef = dotnetRef;
            if (keysRef) document.addEventListener('keydown', onEditorKey);
        },
        // The words selected on the page, for a review comment (EP-80): trimmed, at most 300 characters.
        selection: function () {
            const sel = window.getSelection ? window.getSelection().toString() : '';
            return sel.replace(/\s+/g, ' ').trim().slice(0, 300);
        },
        // To a section's heading, or to the field with the id given (when it's there).
        jump: function (el, fieldId) {
            const field = fieldId ? document.getElementById(fieldId) : null;
            const to = field || el;
            if (!to) return;
            // From the rail open over the page (a gap's button in the preview): the page under it is inert until it closes.
            if (window.tideRail && window.tideRail.over() && !to.closest('#tide-rail')) window.tideRail.toggle();
            const smooth = !window.tideShortcuts.still();
            to.scrollIntoView({ block: field ? 'center' : 'start', behavior: smooth ? 'smooth' : 'auto' });
            to.focus({ preventScroll: true });
        },
        // To a list just opened below the save bar (History), with the bar above it still in view; focus stays put.
        reveal: function (id) {
            const el = document.getElementById(id);
            if (el && !el.hidden) el.scrollIntoView({ block: 'start', behavior: window.tideShortcuts.still() ? 'auto' : 'smooth' });
        },
        // To the preview's first gap (its dashed placeholder), focused: the readiness line's Show.
        firstGap: function () {
            const gap = document.querySelector('#reader-panel .tide-placeholder');
            if (gap) window.tideEditor.jump(gap, null);
        },
        // Starts the 1 and 2 keys for the preview's .NET object, or stops them (null).
        editionKeys: function (dotnetRef) {
            document.removeEventListener('keydown', onKey);
            ref = dotnetRef;
            if (ref) document.addEventListener('keydown', onKey);
        },
        // Puts ?edition=company in the address, or takes it out (null), keeping the rest and the history entry's state.
        showEdition: function (key) { window.tideEditor.showParam('edition', key); },
        // The same for any of the preview's choices kept in the address (?side=actions, the tab beside the article).
        showParam: function (name, value) {
            const url = new URL(window.location.href);
            if (value) url.searchParams.set(name, value); else url.searchParams.delete(name);
            history.replaceState(history.state, '', url.toString());
        }
    };
})();

// The section index's height (one row of pills, or two on a narrower window) for the page's scroll-padding-top, so a
// section jumped to stops below the pills whatever their height (20-editor.css).
(function () {
    if (!window.ResizeObserver || !window.MutationObserver) return;
    let seen = null;
    const sized = new ResizeObserver(function (es) {
        const h = es[0] && es[0].target.isConnected ? es[0].target.getBoundingClientRect().height : 0;
        document.documentElement.style.setProperty('--tide-pills-h', h + 'px');
    });
    const look = function () {
        const pills = document.querySelector('.tide-section-pills');
        if (pills === seen) return;
        if (seen) sized.unobserve(seen);
        seen = pills;
        if (pills) sized.observe(pills); else document.documentElement.style.removeProperty('--tide-pills-h');
    };
    const start = function () { look(); new MutationObserver(look).observe(document.body, { childList: true, subtree: true }); };
    if (document.body) start(); else document.addEventListener('DOMContentLoaded', start);
})();
