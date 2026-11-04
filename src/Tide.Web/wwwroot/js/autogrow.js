// Text boxes that grow with their text (textarea.tide-autogrow): an article's bullets and longer fields. CSP-safe (an external file).
// Where the browser sizes them itself (CSS field-sizing: content, in app.css) this does nothing; elsewhere it sets each
// one's height to its content when it's typed in, and when Blazor draws or changes one.
(function () {
    if (window.CSS && CSS.supports && CSS.supports('field-sizing', 'content')) return;

    function fit(el) {
        el.style.height = 'auto';
        el.style.height = (el.scrollHeight + el.offsetHeight - el.clientHeight) + 'px';
    }

    function fitAll(root) {
        (root.querySelectorAll ? root.querySelectorAll('textarea.tide-autogrow') : []).forEach(fit);
    }

    document.addEventListener('input', function (e) {
        if (e.target && e.target.matches && e.target.matches('textarea.tide-autogrow')) fit(e.target);
    });

    // Blazor adds bullets, and sets their text, without an input event.
    let queued = false;
    new MutationObserver(function () {
        if (queued) return;
        queued = true;
        requestAnimationFrame(function () { queued = false; fitAll(document); });
    }).observe(document.documentElement, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['value'] });

    window.addEventListener('resize', function () { fitAll(document); });
})();
