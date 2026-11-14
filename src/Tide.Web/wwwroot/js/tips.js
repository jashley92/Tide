// A page's tip, or a step of the tour or a guide (GuideStepDialog): set next to the control it's about, with a small
// arrow on the side facing it, and the control ringed (a pulsing ring that settles; steady under reduced motion). Below
// the control, else above it, else beside it; never in a corner: a step whose control isn't on the page is left out of
// its guide (present), and one shown before its control is drawn waits in the middle, with no arrow. Never over
// the sticky top bar, nor over the context rail or the presenter's rail, docked or open over the page: it moves left of
// it, and points at a control in it from there. A tip that needs its control (needsTarget) isn't shown without it. A tip
// isn't modal: this never takes a key from it, and moves focus only to give it back to the page's heading when Got it
// took it away. A step is a dialog (options.dialog): focus that lands outside it (a new page focuses its heading as it
// opens) goes back to its main button. A step's control may be drawn after the step (a page still loading, a guide
// started from Help): it's looked for again as the page changes, and scrolled into view once found. A selector can name
// fallbacks, "a || b": the first that's shown; a rail's button there marks a step about what's in that rail, which is
// opened for it (railFor) rather than its button ringed. A step whose control is in a
// shut tab, section, menu or picker names what opens it (opens): it's opened first, which only shows it, and one the
// guide opened is closed again (closes) when a step leaves it or the guide ends. Nothing in it is chosen, so closing
// leaves the page as it was. CSP-safe (an external file).
window.tideTips = (function () {
    var card = null, ring = null, arrow = null, target = null, looking = null, opener = null, size = null, frame = 0;
    // What the guide opened for its steps (a section, a menu, a picker), to close as they leave it: { opens, closes }.
    var popout = null;
    // The heading of the page a guide is leaving for its own (leaving): its steps are looked for once it's gone.
    var leavingFrom = null;
    var needsTarget = false, dialog = false, inside = false;
    // ARROW_HALF is half the arrow's 14px square (app.css); ARROW keeps it off the card's rounded corners.
    var GAP = 12, EDGE = 8, ARROW = 16, ARROW_HALF = 7, RING = 4;
    // A control taller than this share of the window is shown from its top.
    var TALL = 0.6;
    // Whether this step's control, pushed out of the window after it was found, has been brought back already.
    var brought = false;
    // Whether this step's page has been scrolled to make room for its card beside its control (place), once a step.
    var nudged = false;
    var root = document.documentElement;

    function shown(el) { return !!el && el.isConnected && el.getClientRects().length > 0 && !el.closest('[inert], [hidden]'); }

    function clamp(n, lo, hi) { return Math.max(lo, Math.min(n, hi)); }

    // The first of the selector's choices that's shown, else the first that's there at all.
    function pick(selector) {
        var found = null;
        // A step about something in a rail that's open, or opening for it (railWanted), never rings the rail's button.
        choices(selector).filter(function (one) { return !(railWanted && RAIL_BUTTONS[one]); }).some(function (one) {
            var all = document.querySelectorAll(one);
            for (var i = 0; i < all.length; i++) if (shown(all[i])) { found = all[i]; return true; }
            if (!found && all.length) found = all[0];
            return false;
        });
        return found;
    }

    // A step's control, again, when it isn't shown: drawn since, or drawn anew. Marked and scrolled into view once found.
    function find() {
        if (!looking || shown(target)) return;
        var now = pick(looking);
        if (now === target) return;
        if (target) { target.classList.remove('tide-tip-target'); sizeWatch.unobserve(target); }
        target = now && grown(now);
        if (!shown(target)) return;
        // Its scroll margin keeps it clear of the top bar; one taller than most of the window shows from its top. (Read
        // before the mark is written, so this costs no extra layout.)
        var tall = target.getBoundingClientRect().height > innerHeight * TALL;
        brought = false;
        nudged = false;
        target.classList.add('tide-tip-target');
        sizeWatch.observe(target);
        watchPage();
        target.scrollIntoView({ block: tall ? 'start' : 'nearest', inline: 'nearest' });
    }

    // A control too small to ring well (a radio hidden in a button group, an icon) is ringed by the group or label it's
    // shown in: the first of those, then its parents, at least MIN square and shown.
    var MIN = 16;
    function sized(el) { var r = el.getBoundingClientRect(); return r.width >= MIN && r.height >= MIN; }
    function grown(el) {
        if (!shown(el) || sized(el)) return el;
        var label = el.id ? document.querySelector('label[for="' + el.id + '"]') : null;
        var around = [label, el.closest('[role="group"], .btn-group, fieldset, label'), el.parentElement, el.parentElement && el.parentElement.parentElement];
        for (var i = 0; i < around.length; i++) if (around[i] && shown(around[i]) && sized(around[i])) return around[i];
        return el;
    }

    // The rails (the context rail, present mode's presenter's rail), and their buttons: a step's fallback to these is
    // still the rail, not its control.
    var RAIL_PANES = ['#tide-rail', '#present-rail'];
    var RAILS = RAIL_PANES.concat(['#tide-rail-toggle', '#present-rail-toggle']);

    // A selector's choices, "a || b" (pick, there).
    function choices(selector) { return selector.split('||').map(function (c) { return c.trim(); }); }

    // Whether a step has its control: one of its choices (other than the rail, for a step about something in it) shown,
    // or in the shut rail where the step falls back to the rail's button, or in what it opens (a tab, section, menu or
    // picker) while that's shut and can't be looked in. For a step about the rail itself, the rail or its button.
    function there(selector, opens) {
        var own = choices(selector);
        var mine = own.filter(function (c) { return RAILS.indexOf(c) < 0; });
        if (!mine.length) return own.some(function (c) { return !!document.querySelector(c); });
        var railed = mine.length < own.length;
        return mine.some(function (c) {
            return Array.prototype.some.call(document.querySelectorAll(c), function (el) {
                return shown(el) || railed && !!el.closest(RAIL_PANES.join(', '));
            });
        }) || !!opens && !!document.querySelector(opens) && !isOpen(document.querySelector(opens));
    }

    // A step about something in a rail (its selector names the rail's button): the rail is kept open for it, over the
    // page or docked as the window has room for (keepRail), and shut again when a step leaves it or the guide ends if
    // the guide opened it. The context rail opens without being remembered (tideRail.show); the presenter's by its button.
    var railOpened = null, railWanted = null;
    var RAIL_BUTTONS = { '#tide-rail-toggle': 'context', '#present-rail-toggle': 'present' };

    function railFor(selector) {
        var own = selector ? choices(selector) : [];
        var wants = null;
        own.forEach(function (c) { if (RAIL_BUTTONS[c]) wants = RAIL_BUTTONS[c]; });
        railWanted = wants;
        if (railOpened && railOpened !== wants) { shutRail(railOpened); railOpened = null; }
        keepRail();
    }

    // The rail a step is about, open in the form the window has room for: opened if it's shut as the step starts, and
    // again if it shuts during it (the window crossing the width where it docks, ] or its Close), so the step always
    // rings what's in it. One the guide opened is shut as the guide leaves it.
    function keepRail() {
        if (!railWanted) return;
        if (railWanted === 'context') {
            if (!window.tideRail || shown(document.getElementById('tide-rail'))) return;
            window.tideRail.show();
        } else {
            var button = document.getElementById('present-rail-toggle');
            if (!button || button.getAttribute('aria-expanded') === 'true') return;
            button.click();
        }
        railOpened = railWanted;
    }

    function shutRail(which) {
        if (which === 'context') { if (window.tideRail) window.tideRail.hide(); return; }
        var close = document.getElementById('present-rail-close');
        if (close && close.isConnected) close.click();
    }

    // Whether what `el` opens is open: a tab selected, a button's section or menu expanded, a <details> open.
    function isOpen(el) {
        if (el.getAttribute('aria-selected') === 'true' || el.getAttribute('aria-expanded') === 'true') return true;
        var details = el.tagName === 'SUMMARY' && el.parentElement;
        return !!details && details.open;
    }

    // A step's tab, section, menu or picker, opened if it's shut; one the guide opened is closed when a step leaves it
    // (a step with other opens, or none) or the guide ends: by its own close (a picker's Close, which drops what was
    // ticked), else by its opener again.
    function openFor(opens, closes) {
        if (popout && popout.opens !== opens) {
            var close = popout.closes && document.querySelector(popout.closes);
            var by = document.querySelector(popout.opens);
            // Its close may be under a rail opened over the page since (inert, not shown): a click still closes it.
            if (close && close.isConnected) close.click();
            else if (by && isOpen(by)) by.click();
            popout = null;
        }
        if (!opens) return;
        var el = document.querySelector(opens);
        if (!el || popout) return;
        if (!isOpen(el)) {
            el.click();
            // A tab stays as it is chosen; anything else the guide opened, it closes.
            if (el.getAttribute('role') !== 'tab') popout = { opens: opens, closes: closes || null };
        }
    }

    // Where the window is free of the sticky top bar (the app's, or present mode's): a card never goes above it.
    function clear() {
        var bar = document.querySelector('.top-row, .tide-present-top');
        return (shown(bar) ? Math.max(0, bar.getBoundingClientRect().bottom) : 0) + EDGE;
    }

    // The rail it keeps off: the context rail, or present mode's presenter's rail.
    function railBox() {
        var rail = RAIL_PANES.map(function (id) { return document.querySelector(id); }).filter(Boolean)[0];
        return shown(rail) ? rail.getBoundingClientRect() : null;
    }

    // The page's width left of any scrollbar (the body scrolls, not <html>), so a card at the right keeps its whole
    // border in view.
    function width() { return Math.min(root.clientWidth, document.body.clientWidth); }

    // How far down a card beside a control at `r` starts: level with what shows of it, inside the window.
    function sideTop(r, high) { return clamp(r.top, high, innerHeight - size.height - EDGE); }

    // Where the card goes for a control at `r`: below it, else above it (across, centred on it but inside the page), else
    // beside it (right, then left), from the top of what shows of it. Null when none fits.
    function beside(r, w, high) {
        var h = size.height, cw = size.width, below = Math.max(high, r.bottom + GAP);
        var across = clamp(r.left + r.width / 2 - cw / 2, EDGE, w - cw - EDGE);
        if (below + h <= innerHeight - EDGE) return { side: 'below', top: below, left: across };
        if (r.top - GAP - h >= high) return { side: 'above', top: r.top - GAP - h, left: across };
        var top = sideTop(r, high);
        if (r.right + GAP + cw <= w - EDGE) return { side: 'right', top: top, left: r.right + GAP };
        if (r.left - GAP - cw >= EDGE) return { side: 'left', top: top, left: r.left - GAP - cw };
        return null;
    }

    // The card next to its control, its arrow facing it, and the ring round the control; in the middle, with no arrow or
    // ring, while its control isn't shown (or nowhere beside it fits). Then left of the rail if it would be over it: a
    // control in the rail is pointed at from there. Everything is read before anything is written, so a move costs one
    // layout.
    function place() {
        frame = 0;
        if (!card) return;
        if (dialog) keepRail();
        find();
        var hasTarget = shown(target);
        if (needsTarget && !hasTarget) { card.hidden = true; ringAt(null); arrowAt(null); return; }
        var r = hasTarget ? target.getBoundingClientRect() : null, w = width(), rail = railBox(), high = clear();
        // A step's control pushed out of the window once found (a section it opened filling in above it): brought back
        // into view once, so scrolling the page away from it afterwards stays where it's scrolled. Not for a tip, which
        // never moves the page.
        if (dialog && r && !brought && (r.top >= innerHeight || r.bottom <= high || r.height <= innerHeight * TALL && (r.bottom > innerHeight || r.top < high))) {
            brought = true;
            target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
            r = target.getBoundingClientRect();
        }
        var at = r && beside(r, w, high);
        // Nowhere beside it fits where the page has it (a tall control, or a card that wider letters made taller), though
        // the window can show both: for a step, once, the page is scrolled just enough for the card to fit under it, else
        // over it. A control too tall for that keeps the card in the middle.
        if (dialog && r && !at && !nudged && r.height + GAP + size.height <= innerHeight - EDGE - high) {
            nudged = true;
            // In whole pixels, rounded up: the page scrolls by whole pixels.
            var down = Math.ceil(r.bottom + GAP + size.height - (innerHeight - EDGE));
            if (down > 0) { scrollBy({ top: down, behavior: 'instant' }); r = target.getBoundingClientRect(); at = beside(r, w, high); }
            var up = Math.ceil(high + size.height + GAP - r.top);
            if (!at && up > 0) { scrollBy({ top: -up, behavior: 'instant' }); r = target.getBoundingClientRect(); at = beside(r, w, high); }
        }
        at = at || { side: 'none', top: Math.max(high, (innerHeight - size.height) / 2), left: Math.max(EDGE, (w - size.width) / 2) };
        // A control in the top bar, above the rail, keeps its card under it: moved off the rail it couldn't point at it.
        var overTheRail = r && rail && r.bottom <= rail.top + 1;
        var onTheRail = rail && !overTheRail && at.left + size.width > rail.left && at.left < rail.right && at.top < rail.bottom && at.top + size.height > rail.top;
        // Narrowed for a rail that has since gone or moved: back to its own width, measured again. (Narrowed, it may fit
        // beside the rail with no overlap: that's what narrowing was for, not a reason to widen.)
        var narrowedFor = rail ? String(Math.round(rail.left)) : '';
        if (card.style.maxWidth && card.dataset.narrowedFor !== narrowedFor) { card.style.maxWidth = ''; delete card.dataset.narrowedFor; return; }
        if (onTheRail) {
            // Too little room left of the rail (a narrow window with the rail open over it): the card narrows to fit.
            var room = rail.left - GAP - EDGE;
            if (room < size.width && room >= 240 && card.style.maxWidth !== room + 'px') {
                card.style.maxWidth = room + 'px';
                card.dataset.narrowedFor = narrowedFor;
                return;   // measured again at its new width (cardWatch), then placed
            }
            at.left = Math.max(EDGE, rail.left - GAP - size.width);
            // A control in the rail: the card is now left of it.
            if (r && r.left >= rail.left - 1 && r.top >= rail.top - 1) {
                at.side = 'left';
                at.top = sideTop(r, high);
            }
        }
        card.hidden = false;
        card.style.top = at.top + 'px';
        card.style.left = at.left + 'px';
        card.dataset.side = at.side;
        card.classList.add('is-placed');
        // Marked again if a redraw took the mark off (a NavLink rewriting its class as the page changes).
        if (r && !target.classList.contains('tide-tip-target')) target.classList.add('tide-tip-target');
        ringAt(at.side === 'none' ? null : r);
        arrowAt(r && at, r, high);
    }

    // An element of tips.js's own drawn over the page (the ring, the arrow): at the end of the page, or of what's full
    // screen (present mode), outside anything Blazor draws; made again when that changes.
    function overlay(el, cls) {
        var home = document.fullscreenElement || document.body;
        if (el && el.parentNode === home) return el;
        if (el) el.remove();
        el = document.createElement('div');
        el.className = cls;
        el.setAttribute('aria-hidden', 'true');
        home.appendChild(el);
        pageWatch.takeRecords();   // its own insert isn't the page changing
        return el;
    }

    // The card's arrow, on the side facing its control (at `r`) and opposite its middle, inside the card's corners: its
    // own element over the card's edge (so it's no part of the card's box), turned to face the control.
    function arrowAt(at, r, high) {
        if (!at || at.side === 'none') { if (arrow) arrow.hidden = true; return; }
        arrow = overlay(arrow, 'tide-tip-arrow');
        var along = at.side === 'below' || at.side === 'above'
            ? clamp(r.left + r.width / 2 - at.left, ARROW, size.width - ARROW)
            : clamp((Math.max(r.top, high) + Math.min(r.bottom, innerHeight)) / 2 - at.top, ARROW, size.height - ARROW);
        var x, y;
        if (at.side === 'below') { x = at.left + along; y = at.top; }
        else if (at.side === 'above') { x = at.left + along; y = at.top + size.height; }
        else if (at.side === 'left') { x = at.left + size.width; y = at.top + along; }
        else { x = at.left; y = at.top + along; }
        arrow.hidden = false;
        arrow.dataset.side = at.side;
        arrow.classList.toggle('is-step', dialog);
        arrow.style.left = (x - ARROW_HALF) + 'px';
        arrow.style.top = (y - ARROW_HALF) + 'px';
    }

    // The ring round the control: drawn over the page (and a step's scrim), never over the control itself; following it
    // as the page scrolls or resizes, and gone with the card.
    function ringAt(r) {
        if (!r) { if (ring) ring.hidden = true; return; }
        ring = overlay(ring, 'tide-tip-ring');
        ring.hidden = false;
        ring.style.top = (r.top - RING) + 'px';
        ring.style.left = (r.left - RING) + 'px';
        ring.style.width = (r.width + 2 * RING) + 'px';
        ring.style.height = (r.height + 2 * RING) + 'px';
    }

    // At most once a frame, however many scrolls, resizes or rail changes come in it.
    function soon() { if (!frame) frame = requestAnimationFrame(place); }

    // The rail opening or closing (theme.js marks it on <html>) moves the card off it.
    var railWatch = new MutationObserver(soon);

    // The sticky top bar growing or shrinking: the card is placed again, clear of it.
    var barWatch = new ResizeObserver(soon);

    // A step's control not found yet, or gone, or the page moving under it: the page changing (a stylesheet added
    // included) may draw it or move it. Read without a layout (place, in the next frame, measures). A style or class
    // change to the control or anything it's in moves it too, with no node added and no size changed (a popout placed
    // against its button again), and so does text changing beside it (a count growing): the ring follows. Only those
    // are watched for (watchPage), not every style and text change on the page. Not this script's own placing of the
    // card, its arrow and ring.
    var pageWatch = new MutationObserver(function (records) {
        if (records.some(function (m) { return m.type !== 'attributes' || [card, ring, arrow].indexOf(m.target) < 0; })) soon();
    });
    var MOVES = ['style', 'class', 'hidden'];

    // What pageWatch watches: nodes added or removed anywhere; and, once the control is found, style, class and hidden on
    // it and on each element it's in, and text in what holds it. Again whenever the control is found anew.
    function watchPage() {
        pageWatch.disconnect();
        pageWatch.observe(root, { childList: true, subtree: true });
        if (!shown(target)) return;
        var holder = target.parentElement;
        for (var el = target; el && el !== root; el = el.parentElement) {
            pageWatch.observe(el, el === holder
                ? { attributes: true, attributeFilter: MOVES, characterData: true, subtree: true }
                : { attributes: true, attributeFilter: MOVES });
        }
    }

    // The control or the page changing size (letters widening, a list growing) moves the card, its arrow and the ring.
    var sizeWatch = new ResizeObserver(soon);

    // The card growing or shrinking (a longer step, wider letters) is measured again and placed again, so it stays in
    // view.
    var cardWatch = new ResizeObserver(function () {
        if (!card || card.hidden) return;
        var box = card.getBoundingClientRect();
        if (size && box.width === size.width && box.height === size.height) return;
        size = { width: box.width, height: box.height };
        soon();
    });

    // Focus on the page's heading (present mode's is outside <main>).
    function toPage() {
        var to = document.querySelector('main h1') || document.querySelector('h1');
        if (!to) return;
        if (!to.hasAttribute('tabindex')) to.setAttribute('tabindex', '-1');
        to.focus();
    }

    // Whether focus is in the card: a tip's Got it taking it away gives it to the page's heading, not the page; a step
    // keeps it.
    document.addEventListener('focusin', function (e) {
        inside = !!card && card.contains(e.target);
        if (!dialog || !card || !card.isConnected || inside) return;
        var main = card.querySelector('.btn-primary');
        if (main) setTimeout(function () { if (dialog && card && card.isConnected && !card.contains(document.activeElement)) main.focus(); }, 0);
    });

    return {
        // options.needsTarget: show it only beside its control. options.dialog: a step, which keeps focus.
        // options.opens / options.closes: what to open first, and what closes it again (openFor).
        show: function (el, selector, options) {
            this.hide();
            card = el;
            card.style.maxWidth = '';
            delete card.dataset.narrowedFor;
            needsTarget = !!(options && options.needsTarget);
            dialog = !!(options && options.dialog);
            // A tour's or guide's first step: what had focus (its Show me around, Help's Start) gets it back at the end.
            if (dialog && options.first) opener = document.activeElement !== document.body ? document.activeElement : null;
            // A control in a shut tab, section, menu or picker: it's opened first (showing it saves nothing), and what the
            // last step opened is closed if this one isn't in it.
            if (dialog) {
                // What the last step opened is closed before a rail opens over the page (making it inert).
                openFor(options.opens || null, options.closes || null);
                railFor(selector);
            }
            if (dialog && selector) {
                // A step looks for its control (find, from place) now and again as the page changes; a tip shows beside
                // what's there as it opens.
                looking = selector;
            } else {
                target = selector ? pick(selector) : null;
                if (shown(target)) sizeWatch.observe(target);
            }
            // The page changing (a step's control drawn, a list growing above a tip's control) places it again.
            watchPage();
            sizeWatch.observe(document.body);
            // Measured where it starts, and again if its size changes (cardWatch).
            var box = card.getBoundingClientRect();
            size = { width: box.width, height: box.height };
            place();
            addEventListener('resize', soon, { passive: true });
            addEventListener('scroll', soon, { capture: true, passive: true });
            railWatch.observe(root, { attributes: true, attributeFilter: ['data-rail', 'data-rail-over'] });
            cardWatch.observe(card);
            // The top bar settling (its tools wrapping to a second line as the page draws) moves the card clear of it.
            var bar = document.querySelector('.top-row, .tide-present-top');
            if (bar) barWatch.observe(bar);
            // Whether it's shown, for the page to announce it.
            return !card.hidden;
        },
        hide: function () {
            if (target) target.classList.remove('tide-tip-target');
            if (frame) cancelAnimationFrame(frame);
            if (ring) { ring.remove(); ring = null; }
            if (arrow) { arrow.remove(); arrow = null; }
            var lost = inside && !dialog && (!document.activeElement || document.activeElement === document.body);
            card = target = size = looking = null;
            frame = 0;
            inside = dialog = false;
            railWatch.disconnect();
            barWatch.disconnect();
            pageWatch.disconnect();
            sizeWatch.disconnect();
            cardWatch.disconnect();
            removeEventListener('resize', soon, { passive: true });
            removeEventListener('scroll', soon, { capture: true, passive: true });
            if (lost) toPage();
        },
        // Which of a guide's steps ({ target, opens }) have their control on the page once it has settled: nothing
        // loading and no change for a moment (four seconds at most). A step without its control is left out, never
        // shown in a corner.
        present: function (steps) {
            return new Promise(function (resolve) {
                var quiet = 0, most = 0, done = false;
                // Settled: the page left (leaving) gone, nothing still loading (LoadingBlock), and no change for a moment.
                function settled() {
                    if (leavingFrom && leavingFrom.isConnected || document.querySelector('.tide-loading')) { quiet = setTimeout(settled, 300); return; }
                    answer();
                }
                function answer() {
                    if (done) return;
                    done = true;
                    leavingFrom = null;
                    watch.disconnect();
                    clearTimeout(quiet);
                    clearTimeout(most);
                    resolve(steps.map(function (s) { return !s.target || there(s.target, s.opens); }));
                }
                var watch = new MutationObserver(function () { clearTimeout(quiet); quiet = setTimeout(settled, 300); });
                watch.observe(root, { childList: true, subtree: true });
                quiet = setTimeout(settled, 300);
                most = setTimeout(answer, 4000);
            });
        },
        // A guide is about to open its own page: present waits for this one's heading to go first.
        leaving: function () { leavingFrom = document.querySelector('#main-content h1, main h1'); },
        // A tour or guide has ended: focus back on what started it when that's still on the page, else the page's
        // heading. A tip the new page shows stays up.
        end: function () {
            openFor(null);
            railFor(null);
            var to = opener;
            opener = null;
            if (shown(to)) to.focus();
            else toPage();
        }
    };
})();
