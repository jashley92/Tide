// Paste-a-screenshot and drag-drop helper for the article editor. CSP-safe: one set of delegated document
// listeners attached from this external file (no inline handlers, no eval). It moves no bytes itself: it hands the
// clipboard image or dropped files to a hidden Blazor <InputFile> by setting its FileList and firing a native
// 'change', so the files stream to .NET over the normal InputFile path (type check and size cap unchanged).
// Registration is idempotent and supports several zones (paste goes to the one that's visible). A zone can take the
// whole page (an action's Files): a drop anywhere outside another zone, and a paste with nothing focused, go to it, even
// while it's hidden (in a closed context rail), and it asks to be shown (a 'tide:reveal' event, which the rail answers).
window.tidePasteDrop = (function () {
    let attached = false;
    const zones = []; // { zoneId, pasteInputId, dropInputId|null, anyFile, focusOnly, page }
    const root = document.documentElement;
    let dragEnds = 0, lastDrag = 0;

    // A zone takes part only while its wrapper element is in the DOM and visible, so paste goes to the editor on
    // screen and the listeners stay registered harmlessly across pages. Visible is "has a box". A paste goes first to
    // the zone holding the focus (an action's Files box, say); otherwise to the first visible zone that takes a paste
    // from anywhere on the page (a focus-only zone takes one only while it has the focus).
    function visibleZone(target) {
        const focused = document.activeElement;
        for (const z of zones) {
            const el = document.getElementById(z.zoneId);
            if (el && el.getClientRects().length > 0 && ((focused && el.contains(focused)) || (target && el.contains(target)))) return { z, el };
        }
        for (const z of zones) {
            if (z.focusOnly) continue;
            const el = document.getElementById(z.zoneId);
            if (el && (z.page || el.getClientRects().length > 0)) return { z, el };
        }
        return null;
    }

    // The zone a drop on `target` goes to: the one it's in, else a zone that takes the whole page (not from inside a
    // dialog, whose own controls are what's being used).
    function zoneForTarget(target) {
        if (!target || !target.nodeType) return null;
        let page = null;
        for (const z of zones) {
            const el = z.dropInputId && document.getElementById(z.zoneId);
            if (!el) continue;
            if (el.contains(target)) return { z, el };
            if (z.page && !page) page = { z, el, page: true };
        }
        const dialog = page && target.closest && target.closest('[role="dialog"], [role="alertdialog"]');
        return dialog && !dialog.contains(page.el) ? null : page;
    }

    // While files are dragged over a page that takes them, html[data-file-drag] shows its drop hint (app.css). A drag
    // fires dragover every few milliseconds, so the hint goes once they stop for a moment: dropped, or carried off the
    // window. One timer looks again until they have.
    function dragging(on) {
        if (!on) { clearTimeout(dragEnds); dragEnds = 0; root.removeAttribute('data-file-drag'); return; }
        lastDrag = performance.now();
        if (dragEnds) return;
        root.setAttribute('data-file-drag', '');
        (function wait() {
            const left = 300 - (performance.now() - lastDrag);
            dragEnds = left > 0 ? setTimeout(wait, left) : (root.removeAttribute('data-file-drag'), 0);
        })();
    }

    // Files that reached a page zone while it's hidden: it asks to be shown, so they're seen waiting.
    function onChange(e) {
        const t = e.target;
        if (!t || t.type !== 'file') return;
        for (const z of zones) {
            const el = z.page && (z.pasteInputId === t.id || z.dropInputId === t.id) && document.getElementById(z.zoneId);
            if (el && el.getClientRects().length === 0) el.dispatchEvent(new CustomEvent('tide:reveal', { bubbles: true }));
        }
    }

    // A button that picks files for a zone (data-pick names its hidden InputFile): a label can't take the keyboard's focus.
    function onClick(e) {
        const btn = e.target && e.target.closest && e.target.closest('[data-pick]');
        const inp = btn && document.getElementById(btn.getAttribute('data-pick'));
        if (inp) inp.click();
    }

    function push(inputId, files) {
        const inp = inputId && document.getElementById(inputId);
        if (!inp || !files || !files.length) return;
        const dt = new DataTransfer();
        for (const f of files) dt.items.add(f);
        inp.files = dt.files;
        inp.dispatchEvent(new Event('change', { bubbles: true }));
    }

    function stamp() {
        const d = new Date(), p = n => String(n).padStart(2, '0');
        return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
    }

    function imagesFrom(items) {
        const imgs = [];
        for (const it of items || []) {
            if (it.kind === 'file' && it.type && it.type.indexOf('image/') === 0) {
                const f = it.getAsFile();
                if (f) {
                    const ext = (f.type.split('/')[1] || 'png').replace('jpeg', 'jpg').replace('svg+xml', 'svg');
                    // Several pasted at once are told apart by their place in the paste.
                    const n = imgs.length ? `-${imgs.length + 1}` : '';
                    imgs.push(new File([f], `pasted-screenshot-${stamp()}${n}.${ext}`, { type: f.type }));
                }
            }
        }
        return imgs;
    }

    // A zone that takes any file also takes a pasted file that isn't an image (a file copied in Explorer or
    // Finder), under its own name.
    function otherFilesFrom(items) {
        const files = [];
        for (const it of items || []) {
            if (it.kind === 'file' && !(it.type && it.type.indexOf('image/') === 0)) {
                const f = it.getAsFile();
                if (f) files.push(f);
            }
        }
        return files;
    }

    // A paste into a text field that carries text is text: Excel, OneNote, Word and Outlook put a picture of what was
    // copied beside its text, and that picture mustn't replace the IOCs or words being pasted.
    function isTextField(el) {
        if (!el) return false;
        if (el.isContentEditable) return true;
        const tag = el.tagName;
        if (tag === 'TEXTAREA') return true;
        return tag === 'INPUT' && !/^(button|checkbox|color|file|hidden|image|radio|range|reset|submit)$/i.test(el.type || '');
    }

    function carriesText(data) {
        const types = data && data.types;
        return !!types && (Array.prototype.indexOf.call(types, 'text/plain') >= 0 || Array.prototype.indexOf.call(types, 'text/html') >= 0);
    }

    function onPaste(e) {
        const v = visibleZone(e.target);
        if (!v) return;
        if (isTextField(e.target) && carriesText(e.clipboardData)) return;
        const items = e.clipboardData && e.clipboardData.items;
        const imgs = imagesFrom(items);
        const files = imgs.length ? imgs : (v.z.anyFile ? otherFilesFrom(items) : []);
        if (files.length) { e.preventDefault(); push(v.z.pasteInputId, files); }
    }

    // Only a drag that carries files is taken; dragged text drops as text.
    function carriesFiles(e) {
        const types = e.dataTransfer && e.dataTransfer.types;
        return !!types && Array.prototype.indexOf.call(types, 'Files') >= 0;
    }

    // A file input takes a dropped file itself; anywhere else that no zone takes it, a dropped file would open in the
    // browser in place of TIDE, so the drop is refused instead.
    function fileInput(target) {
        return !!(target && target.closest && target.closest('input[type="file"]'));
    }

    function onDragOver(e) {
        if (!carriesFiles(e)) return;
        const v = zoneForTarget(e.target);
        if (!v) {
            if (!fileInput(e.target)) { e.preventDefault(); if (e.dataTransfer) e.dataTransfer.dropEffect = 'none'; }
            return;
        }
        e.preventDefault();
        if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
        if (v.page) dragging(true);
        else v.el.classList.add('tide-dropping');
    }

    function onDragLeave(e) {
        for (const z of zones) {
            const el = document.getElementById(z.zoneId);
            if (el && !el.contains(e.relatedTarget)) el.classList.remove('tide-dropping');
        }
    }

    function onDrop(e) {
        if (!carriesFiles(e)) return;
        const v = zoneForTarget(e.target);
        if (!v) { if (!fileInput(e.target)) e.preventDefault(); return; }
        e.preventDefault();
        if (v.page) dragging(false);
        else v.el.classList.remove('tide-dropping');
        const files = e.dataTransfer && e.dataTransfer.files;
        if (files && files.length) push(v.z.dropInputId, files);
    }

    // From the start, on every page: a file dropped where nothing takes it mustn't open in the browser.
    document.addEventListener('dragover', onDragOver, true);
    document.addEventListener('drop', onDrop, true);

    return {
        // pasteInputId: hidden InputFile that receives a pasted image. dropInputId: hidden InputFile that
        // receives dropped files (pass null to disable drop for this zone). anyFile: a pasted file that
        // isn't an image is taken too. focusOnly: the zone takes a paste only while it, or something in it, has the focus.
        // page: the zone takes a drop anywhere on the page, and a paste with nothing focused (so it isn't focusOnly).
        register: function (zoneId, pasteInputId, dropInputId, anyFile, focusOnly, page) {
            const existing = zones.find(z => z.zoneId === zoneId);
            if (existing) {
                // The same zone drawn again (another action's page): its inputs are the same, and so is how it takes a paste.
                existing.focusOnly = !!focusOnly;
                existing.page = !!page;
            } else {
                zones.push({ zoneId, pasteInputId, dropInputId: dropInputId || null, anyFile: !!anyFile, focusOnly: !!focusOnly, page: !!page });
            }
            if (!attached) {
                document.addEventListener('change', onChange, true);
                document.addEventListener('click', onClick);
                document.addEventListener('paste', onPaste, true);
                document.addEventListener('dragleave', onDragLeave, true);
                attached = true;
            }
        }
    };
})();
