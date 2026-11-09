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
window.tideEditor = (function () {
    let ref = null;
    const typing = window.tideShortcuts.typing, inMenu = window.tideShortcuts.inMenu;

    function onStep(e) {
        if (e.defaultPrevented) return;   // the palette took it (Ctrl+K, then typing before its box is up)
        const key = (e.key || '').toLowerCase();
        if (e.ctrlKey || e.metaKey || e.altKey || (key !== 'j' && key !== 'k')) return;
        const stepper = document.querySelector('.tide-stepper');
        if (!stepper || typing(e.target) || inMenu(e.target) || document.querySelector('.tide-modal, dialog[open]')) return;
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
        if (document.querySelector('.tide-modal, dialog[open]')) return;
        if (!window.tideShortcuts.on()) return;   // a single character: off with the switch
        e.preventDefault();
        ref.invokeMethodAsync('OnEditionKey', e.key).catch(function () { });
    }

    return {
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
            const smooth = !window.matchMedia('(prefers-reduced-motion: reduce)').matches;
            to.scrollIntoView({ block: field ? 'center' : 'start', behavior: smooth ? 'smooth' : 'auto' });
            to.focus({ preventScroll: true });
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
