// One-click copy of a rendered block (the SharePoint copy) as rich HTML with a plain-text alternative, so it pastes into
// a SharePoint page with its headings and lists. CSP-safe: a delegated listener on [data-copy-target]; the result is
// written to the element named by data-copy-status (never to the button, whose markup Blazor owns).
(function () {
    function say(btn, text) {
        const status = document.getElementById(btn.getAttribute('data-copy-status') || '');
        if (!status) return;
        status.textContent = text;
        setTimeout(function () { status.textContent = ''; }, 4000);
    }

    function selectAndCopy(el) {
        const range = document.createRange();
        range.selectNodeContents(el);
        const sel = window.getSelection();
        sel.removeAllRanges();
        sel.addRange(range);
        let ok = false;
        try { ok = document.execCommand('copy'); } catch (_) { ok = false; }
        sel.removeAllRanges();
        return ok;
    }

    document.addEventListener('click', function (e) {
        const btn = e.target.closest('[data-copy-target]');
        if (!btn) return;
        const el = document.getElementById(btn.getAttribute('data-copy-target'));
        if (!el) return;
        e.preventDefault();
        if (navigator.clipboard && window.ClipboardItem) {
            const item = new ClipboardItem({
                'text/html': new Blob([el.innerHTML], { type: 'text/html' }),
                'text/plain': new Blob([el.innerText], { type: 'text/plain' })
            });
            navigator.clipboard.write([item]).then(
                function () { say(btn, 'Copied. Paste it into the SharePoint page.'); },
                function () { say(btn, selectAndCopy(el) ? 'Copied.' : 'Select the text below and press Ctrl+C.'); });
        } else {
            say(btn, selectAndCopy(el) ? 'Copied.' : 'Select the text below and press Ctrl+C.');
        }
    });
})();
