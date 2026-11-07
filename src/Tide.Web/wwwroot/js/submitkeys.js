// Ctrl+Enter (Cmd+Enter on a Mac) in a text box does the main action of the form or dialog it's in (EP-90): post the
// comment, save the template, record the reply. CSP-safe: an external file, one listener on the page, nothing to start.
// The button it presses, in order:
//   1. the [data-submit] button of the box's nearest [data-submit-scope] (a scope with no such button: nothing);
//   2. in a real <form>, its submit button, or requestSubmit() when it has none;
//   3. in an open dialog, its one .btn-primary button (none, or more than one: nothing).
// A <textarea> takes part anywhere; a single-line box only inside a [data-submit-scope] (in a form, Enter already
// submits). A DOM click() is what Blazor's @onclick hears. A disabled button is never pressed.
(function () {
    var dialogs = 'dialog[open], .modal.show, [role="dialog"]';
    var lineTypes = ['text', 'search', 'url', 'email', 'tel', 'number', 'date', 'time', 'datetime-local'];

    // What .NET last heard of a box's value: as drawn when it got focus, then each change event.
    var heard = new WeakMap();

    function isArea(el) { return el.tagName === 'TEXTAREA'; }
    function isLine(el) { return el.tagName === 'INPUT' && lineTypes.indexOf((el.type || 'text').toLowerCase()) >= 0; }

    document.addEventListener('focusin', function (e) {
        if (isArea(e.target) || isLine(e.target)) heard.set(e.target, e.target.value);
    });
    document.addEventListener('change', function (e) {
        if (isArea(e.target) || isLine(e.target)) heard.set(e.target, e.target.value);
    }, true);

    // The button to press for this box, the form to submit, or null.
    function find(box) {
        var scope = box.closest('[data-submit-scope]');
        if (scope) {
            // Its own button, not one of a scope inside it.
            var own = Array.prototype.filter.call(scope.querySelectorAll('[data-submit]'), function (b) {
                return b.closest('[data-submit-scope]') === scope;
            });
            return own[0] || null;
        }
        if (!isArea(box)) return null;
        if (box.form) return box.form.querySelector('button[type="submit"], button:not([type]), input[type="submit"]') || box.form;
        var dialog = box.closest(dialogs);
        if (!dialog) return null;
        var primary = dialog.querySelectorAll('button.btn-primary');
        return primary.length === 1 ? primary[0] : null;
    }

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Enter' || !(e.ctrlKey || e.metaKey) || e.altKey || e.isComposing || e.repeat || e.defaultPrevented) return;
        var box = e.target;
        if (!box || !(isArea(box) || isLine(box)) || box.readOnly || box.disabled) return;
        var to = find(box);
        if (!to) return;

        // A box bound on change (@bind) hasn't told .NET what was typed: a mouse click would blur it first, which does.
        var unsaid = box.value !== heard.get(box);
        if (unsaid) box.dispatchEvent(new Event('change', { bubbles: true }));

        if (to.tagName === 'FORM') { e.preventDefault(); to.requestSubmit(); return; }
        if (!to.disabled) { e.preventDefault(); to.click(); return; }
        if (!unsaid) return;   // held back for its own reason (busy, a box still empty)

        // Held back only until .NET has the text (an empty-box rule): press it once it's let go, if that's soon and the
        // person is still in the box.
        e.preventDefault();
        var watch = new MutationObserver(function () {
            if (to.disabled) return;
            stop();
            if (document.activeElement === box && to.isConnected) to.click();
        });
        var timer = setTimeout(stop, 1500);
        function stop() { watch.disconnect(); clearTimeout(timer); }
        watch.observe(to, { attributes: true, attributeFilter: ['disabled'] });
    });
})();
