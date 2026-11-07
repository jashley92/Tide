// Text boxes that grow with their text (textarea.tide-autogrow): an article's bullets and longer fields. CSP-safe (an external file).
// Where the browser sizes them itself (CSS field-sizing: content, in app.css) this does nothing; elsewhere it sets each
// one's height to its content when it's typed in, when Blazor draws or changes one, and when one comes into view (a
// <details> opening, a hidden tab shown). A box is re-fitted only when its text or width changed since it was last
// fitted, and the page's scroll is kept: re-fitting every box on every change briefly shortened the page, so a page
// scrolled near its bottom jumped.
(function () {
    if (window.CSS && CSS.supports && CSS.supports('field-sizing', 'content')) return;

    // What each box was fitted for: its text and width. A hidden box (no width) isn't fitted, so it is when it shows.
    const fitted = new WeakMap();
    const watched = new WeakSet();
    const sizes = window.ResizeObserver ? new ResizeObserver(function (entries) {
        entries.forEach(function (entry) { refit(entry.target); });
    }) : null;

    function fit(el) {
        const x = window.scrollX, y = window.scrollY;
        el.style.height = 'auto';
        el.style.height = (el.scrollHeight + el.offsetHeight - el.clientHeight) + 'px';
        if (window.scrollY !== y || window.scrollX !== x) window.scrollTo(x, y);
        fitted.set(el, { value: el.value, width: el.clientWidth });
    }

    function refit(el) {
        if (!el.clientWidth) return;   // hidden: fitted once it shows
        const was = fitted.get(el);
        if (was && was.value === el.value && was.width === el.clientWidth) return;
        fit(el);
    }

    function refitAll(root) {
        (root.querySelectorAll ? root.querySelectorAll('textarea.tide-autogrow') : []).forEach(function (el) {
            if (sizes && !watched.has(el)) { watched.add(el); sizes.observe(el); }
            refit(el);
        });
    }

    document.addEventListener('input', function (e) {
        if (e.target && e.target.matches && e.target.matches('textarea.tide-autogrow')) fit(e.target);
    });

    // A <details> opening shows the boxes in it ('toggle' doesn't bubble, so it's caught on the way down).
    document.addEventListener('toggle', function (e) {
        if (e.target && e.target.open) refitAll(e.target);
    }, true);

    // Blazor adds bullets, and sets their text, without an input event; it also shows a hidden tab by changing a class
    // or style, which the ResizeObserver sees as the box getting a size.
    let queued = false;
    new MutationObserver(function () {
        if (queued) return;
        queued = true;
        requestAnimationFrame(function () { queued = false; refitAll(document); });
    }).observe(document.documentElement, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['value'] });

    window.addEventListener('resize', function () { refitAll(document); });
})();
