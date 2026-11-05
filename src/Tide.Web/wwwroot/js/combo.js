// The search pickers (SearchCombobox: adversaries, We run, the story picker, scorecard tags). CSP-safe: an external
// file. As the arrow keys move the highlight, the highlighted option is scrolled into view, the least distance that
// shows it ("nearest"), in the list or the pane around it; smoothly unless the person asks for less motion.
window.tideCombo = {
    reveal: function (id) {
        const el = id ? document.getElementById(id) : null;
        if (!el) return;
        const still = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        el.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: still ? 'auto' : 'smooth' });
    }
};
