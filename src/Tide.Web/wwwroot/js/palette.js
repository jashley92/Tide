// The command palette (Ctrl+K or Cmd+K, from anywhere) and the "?" shortcuts overlay. CSP-safe (an external file, no
// inline handlers). Both are drawn by Blazor (CommandPalette, ShortcutsOverlay), which registers itself here; the keys
// go to .NET, which owns the list and the highlighted entry. Focus moves in and back through tideDialog (dialog.js).
window.tidePalette = (function () {
    let palette = null, overlay = null;
    const typing = window.tideShortcuts.typing;
    // A layout change (to present mode and back) starts the new component before the old one stops, so each stop names
    // the component it is for and leaves a newer one alone.
    let paletteId = null, overlayId = null;
    // What was typed between Ctrl+K and the box appearing (a round trip to the server), so the first letters aren't lost.
    let early = null, earlyTimer = 0;

    // Combobox keys in the palette's box: these lose their default (the caret jumping, a form submit) and go to .NET.
    const comboKeys = { ArrowDown: 'down', ArrowUp: 'up', PageDown: 'last', PageUp: 'first', Enter: 'enter', Escape: 'escape' };

    // Another dialog (a confirmation, the ATT&CK picker, a screenshot shown full size, the palette itself) has the keys.
    function dialogOpen() {
        return !!document.querySelector('.tide-modal, .tide-atkm-panel, .tide-lightbox');
    }

    function reveal() {
        const active = document.querySelector('[data-palette-list] [aria-selected="true"]');
        if (active) active.scrollIntoView({ block: 'nearest' });
    }

    document.addEventListener('keydown', function (e) {
        if (e.isComposing) return;
        if (e.target && e.target.matches && e.target.matches('[data-palette-input]')) {
            const key = comboKeys[e.key];
            if (!key || !palette || e.altKey || e.ctrlKey || e.metaKey) return;
            e.preventDefault();
            palette.invokeMethodAsync('OnKey', key).then(reveal).catch(function () { });
            return;
        }
        if (early !== null && e.key && e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
            e.preventDefault();
            early += e.key;
            return;
        }
        // Ctrl+K works even in a text field: it's a modifier shortcut, so the letter switch doesn't cover it.
        if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && (e.key === 'k' || e.key === 'K')) {
            if (!palette) return;
            e.preventDefault();
            if (dialogOpen()) return;
            early = '';
            clearTimeout(earlyTimer);
            earlyTimer = setTimeout(function () { early = null; }, 2000);   // in case the box never comes
            palette.invokeMethodAsync('Open').catch(function () { early = null; });
            return;
        }
        // "?" is a single character, so it's off with the letter shortcuts (the top bar's button still opens the list).
        if (e.key === '?' && !e.ctrlKey && !e.metaKey && !e.altKey) {
            if (!overlay || typing(e.target) || dialogOpen()) return;
            if (!window.tideShortcuts.on()) return;
            e.preventDefault();
            overlay.invokeMethodAsync('Open').catch(function () { });
        }
    });

    // The palette is open: focus its box (remembering what had focus, to give it back), with anything typed early.
    function focus(input) {
        window.tideDialog.open(input);
        const text = early;
        early = null;
        if (text && input) {
            input.value = text;
            input.dispatchEvent(new Event('input', { bubbles: true }));
        }
    }

    return {
        focus: focus,
        startPalette: function (ref, id) { palette = ref; paletteId = id; },
        stopPalette: function (id) { if (paletteId === id) { palette = null; paletteId = null; } },
        startOverlay: function (ref, id) { overlay = ref; overlayId = id; },
        stopOverlay: function (id) { if (overlayId === id) { overlay = null; overlayId = null; } }
    };
})();
