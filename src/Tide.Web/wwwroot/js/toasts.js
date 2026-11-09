// Alt+Z presses the newest toast's Undo (ToastHost marks it data-toast-undo="newest", and says so to a screen reader),
// from wherever focus is, while that toast shows: a keyboard path to an Undo that would otherwise go in seconds. Alt
// with a letter isn't a single-character shortcut (WCAG 2.1.4), so the shortcuts switch doesn't turn it off, and none
// of the page's own keys use Alt. Matched by the key's place (e.code), since Option+Z on a Mac types another character.
document.addEventListener('keydown', function (e) {
    if (!e.altKey || e.ctrlKey || e.metaKey || e.shiftKey || e.repeat || e.defaultPrevented) return;
    if (e.code !== 'KeyZ' && e.key !== 'z' && e.key !== 'Z') return;
    var undo = document.querySelector('.tide-toasts [data-toast-undo="newest"]');
    if (!undo) return;
    e.preventDefault();
    undo.click();
});
