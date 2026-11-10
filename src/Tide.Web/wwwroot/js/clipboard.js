// One-click copy. A text box's value (an outreach email's subject and body) or a block's text (a hunt query, a command,
// an indicator) goes as plain text, a block's line breaks as LF whatever the page was served with. A button with
// data-copy-rich copies its block as rich HTML with a plain-text alternative instead, so the SharePoint view pastes with
// its headings and lists. data-copy-done says what a copy that worked says ("Copied." without it). CSP-safe: a delegated
// listener on [data-copy-target]; the result is written to the element named by data-copy-status (never to the button,
// whose markup Blazor owns).
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
    // block's contents, and say whether that worked (with `done`) or how to do it by hand. With `text`, that exact text
    // is what goes on the clipboard, not the selection as the browser writes it (CRLF line breaks on Windows).
    function legacyCopy(btn, el, done, text) {
        let ok = false;
        function exact(e) {
            e.clipboardData.setData('text/plain', text);
            e.preventDefault();
        }
        if (text !== undefined) document.addEventListener('copy', exact);
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
        document.removeEventListener('copy', exact);
        say(btn, ok ? done : 'Select the text and press Ctrl+C.');
    }

    // Plain text: a box's text as it stands, edits included, or a block's text with LF line breaks.
    function copyText(btn, el, done) {
        const text = isField(el) ? el.value : el.textContent.replace(/\r\n?/g, '\n');
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(function () { say(btn, done); }, function () { legacyCopy(btn, el, done, text); });
        } else {
            legacyCopy(btn, el, done, text);
        }
    }

    // A block as it's drawn, with its text as the alternative.
    function copyRich(btn, el, done) {
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
    }

    document.addEventListener('click', function (e) {
        const btn = e.target.closest('[data-copy-target]');
        if (!btn) return;
        const el = document.getElementById(btn.getAttribute('data-copy-target'));
        if (!el) return;
        e.preventDefault();
        const done = btn.getAttribute('data-copy-done') || 'Copied.';
        if (btn.hasAttribute('data-copy-rich') && !isField(el)) copyRich(btn, el, done);
        else copyText(btn, el, done);
    });
})();
