// One-click copy of a rendered block (the SharePoint copy) as rich HTML with a plain-text alternative, so it pastes into
// a SharePoint page with its headings and lists; or of a text box's value as plain text. data-copy-done says what a
// copy that worked says. CSP-safe: a delegated listener on [data-copy-target]; the result is
// written to the element named by data-copy-status (never to the button, whose markup Blazor owns).
(function () {
    function say(btn, text) {
        const status = document.getElementById(btn.getAttribute('data-copy-status') || '');
        if (!status) return;
        status.textContent = text;
        setTimeout(function () { status.textContent = ''; }, 4000);
    }

    function isField(el) {
        return el instanceof HTMLTextAreaElement || el instanceof HTMLInputElement;
    }

    // Where the clipboard API isn't there or is refused: select the text and copy it the old way, a box's own text or a
    // block's contents, and say whether that worked (with `done`) or how to do it by hand.
    function legacyCopy(btn, el, done) {
        let ok = false;
        if (isField(el)) {
            el.focus();
            el.select();
            try { ok = document.execCommand('copy'); } catch (_) { ok = false; }
        } else {
            const range = document.createRange();
            range.selectNodeContents(el);
            const sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(range);
            try { ok = document.execCommand('copy'); } catch (_) { ok = false; }
            sel.removeAllRanges();
        }
        say(btn, ok ? done : 'Select the text and press Ctrl+C.');
    }

    // A box's text as it stands, edits included (an outreach email's subject and body): plain text only.
    function copyField(btn, el) {
        const done = btn.getAttribute('data-copy-done') || 'Copied.';
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(el.value).then(function () { say(btn, done); }, function () { legacyCopy(btn, el, done); });
        } else {
            legacyCopy(btn, el, done);
        }
    }

    document.addEventListener('click', function (e) {
        const btn = e.target.closest('[data-copy-target]');
        if (!btn) return;
        const el = document.getElementById(btn.getAttribute('data-copy-target'));
        if (!el) return;
        e.preventDefault();
        if (isField(el)) {
            copyField(btn, el);
            return;
        }
        const done = btn.getAttribute('data-copy-done') || 'Copied. Paste it into the SharePoint page.';
        if (navigator.clipboard && window.ClipboardItem) {
            const item = new ClipboardItem({
                'text/html': new Blob([el.innerHTML], { type: 'text/html' }),
                'text/plain': new Blob([el.innerText], { type: 'text/plain' })
            });
            navigator.clipboard.write([item]).then(
                function () { say(btn, done); },
                function () { legacyCopy(btn, el, 'Copied.'); });
        } else {
            legacyCopy(btn, el, 'Copied.');
        }
    });
})();
