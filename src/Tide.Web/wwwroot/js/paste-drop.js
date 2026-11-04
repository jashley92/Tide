// Paste-a-screenshot and drag-drop helper for the article editor. CSP-safe: one set of delegated document
// listeners attached from this external file (no inline handlers, no eval). It moves no bytes itself: it hands the
// clipboard image or dropped files to a hidden Blazor <InputFile> by setting its FileList and firing a native
// 'change', so the files stream to .NET over the normal InputFile path (type check and size cap unchanged).
// Registration is idempotent and supports several zones (paste goes to the one that's visible).
window.tidePasteDrop = (function () {
    let attached = false;
    const zones = []; // { zoneId, pasteInputId, dropInputId|null, anyFile }

    // A zone takes part only while its wrapper element is in the DOM and visible, so paste goes to the editor on
    // screen and the listeners stay registered harmlessly across pages. Visible is "has a box".
    function visibleZone() {
        for (const z of zones) {
            const el = document.getElementById(z.zoneId);
            if (el && el.getClientRects().length > 0) return { z, el };
        }
        return null;
    }

    function zoneForTarget(target) {
        for (const z of zones) {
            if (z.dropInputId && target.closest && target.closest('#' + z.zoneId)) {
                const el = document.getElementById(z.zoneId);
                if (el) return { z, el };
            }
        }
        return null;
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
        const v = visibleZone();
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

    function onDragOver(e) {
        const v = zoneForTarget(e.target);
        if (!v || !carriesFiles(e)) return;
        e.preventDefault();
        if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
        v.el.classList.add('tide-dropping');
    }

    function onDragLeave(e) {
        for (const z of zones) {
            const el = document.getElementById(z.zoneId);
            if (el && !el.contains(e.relatedTarget)) el.classList.remove('tide-dropping');
        }
    }

    function onDrop(e) {
        const v = zoneForTarget(e.target);
        if (!v || !carriesFiles(e)) return;
        e.preventDefault();
        v.el.classList.remove('tide-dropping');
        const files = e.dataTransfer && e.dataTransfer.files;
        if (files && files.length) push(v.z.dropInputId, files);
    }

    return {
        // pasteInputId: hidden InputFile that receives a pasted image. dropInputId: hidden InputFile that
        // receives dropped files (pass null to disable drop for this zone). anyFile: a pasted file that
        // isn't an image is taken too.
        register: function (zoneId, pasteInputId, dropInputId, anyFile) {
            if (!zones.some(z => z.zoneId === zoneId)) {
                zones.push({ zoneId, pasteInputId, dropInputId: dropInputId || null, anyFile: !!anyFile });
            }
            if (!attached) {
                document.addEventListener('paste', onPaste, true);
                document.addEventListener('dragover', onDragOver, true);
                document.addEventListener('dragleave', onDragLeave, true);
                document.addEventListener('drop', onDrop, true);
                attached = true;
            }
        }
    };
})();
