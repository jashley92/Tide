// Present mode: keyboard navigation and full screen for the slide view. CSP-safe (an external file, no inline
// handlers). Keys go to .NET, which owns the current slide; typing in a field (meeting notes) is left alone.
window.tidePresent = (function () {
    let ref = null;
    const typing = window.tideShortcuts.typing, inMenu = window.tideShortcuts.inMenu;

    const keys = {
        ArrowRight: 'next', PageDown: 'next', ' ': 'next', Enter: 'next',
        ArrowLeft: 'prev', PageUp: 'prev', Backspace: 'prev',
        Home: 'first', End: 'last',
        f: 'fullscreen', F: 'fullscreen',
        d: 'discussed', D: 'discussed',
        Escape: 'close'
    };

    // Enter and Space on a focused button or link press it (End the meeting, Mark discussed, a slide dot); they turn
    // the slide only when nothing that takes them has focus.
    function pressable(target) {
        return !!(target && target.closest && target.closest('button, a[href], summary, [role="button"], [role="tab"]'));
    }

    function onKey(e) {
        if (!ref || e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey || typing(e.target) || inMenu(e.target)) return;
        // A screenshot shown full size has the keys; the slide behind it doesn't move.
        if (document.querySelector('.tide-lightbox')) return;
        if (document.querySelector('.tide-modal')) return;   // the palette or the shortcuts list is open: its keys are its own
        if (document.querySelector('.tide-atkm-panel')) return;   // the ATT&CK picker (the tag panel's): Esc closes it alone
        if (!window.tideShortcuts.on() && window.tideShortcuts.isLetter(e)) return;
        if ((e.key === 'Enter' || e.key === ' ') && pressable(e.target)) return;
        const action = keys[e.key];
        if (!action) return;
        // Escape leaves full screen by itself; only a second Escape (not in full screen) closes the zoomed image.
        if (action === 'close' && document.fullscreenElement) return;
        e.preventDefault();
        if (action === 'fullscreen') { toggleFullscreen(); return; }
        ref.invokeMethodAsync('OnPresentKey', action).catch(function () { });
    }

    // The whole page goes full screen, not just the slides: the palette, the shortcuts list and the toasts are drawn
    // beside them, and a full-screen element hides everything outside it (a dialog opened there froze the slides).
    function toggleFullscreen() {
        const el = document.documentElement;
        if (document.fullscreenElement) { document.exitFullscreen().catch(function () { }); }
        else if (el.requestFullscreen) { el.requestFullscreen().catch(function () { }); }
    }

    function onFullscreenChange() {
        if (ref) ref.invokeMethodAsync('OnFullscreenChanged', !!document.fullscreenElement).catch(function () { });
    }

    return {
        start: function (dotnetRef) {
            ref = dotnetRef;
            document.addEventListener('keydown', onKey);
            document.addEventListener('fullscreenchange', onFullscreenChange);
        },
        stop: function () {
            ref = null;
            document.removeEventListener('keydown', onKey);
            document.removeEventListener('fullscreenchange', onFullscreenChange);
            if (document.fullscreenElement) document.exitFullscreen().catch(function () { });
        },
        toggleFullscreen: toggleFullscreen
    };
})();
