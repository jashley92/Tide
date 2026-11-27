// The tooltip on a LineChart: the date and each line's value at the point nearest the pointer, or at the point the
// arrow keys have stepped to while the chart has focus. CSP-safe: delegated listeners on [data-chart]. The stops and
// their values are rendered by Blazor; this only marks one active and writes the tooltip (an element Blazor leaves
// empty), so a re-render never fights it.
(function () {
    function stops(plot) { return Array.from(plot.querySelectorAll('.tide-chart-stop')); }

    function hide(plot) {
        plot.querySelectorAll('.tide-chart-stop.active').forEach(function (s) { s.classList.remove('active'); });
        const tip = plot.querySelector('.tide-chart-tip');
        if (tip) { tip.classList.remove('show'); tip.replaceChildren(); }
    }

    function show(plot, stop) {
        if (!stop) { hide(plot); return; }
        plot.querySelectorAll('.tide-chart-stop.active').forEach(function (s) { if (s !== stop) s.classList.remove('active'); });
        stop.classList.add('active');
        const tip = plot.querySelector('.tide-chart-tip');
        if (!tip) return;
        const title = document.createElement('strong');
        title.textContent = stop.getAttribute('data-title') || '';
        const lines = [title];
        let top = 100;
        // The dots come highest first; past the first few (data-rest), one line counts them (data-more).
        stop.querySelectorAll('.tide-chart-dot:not([data-rest])').forEach(function (dot) {
            const line = document.createElement('span');
            const swatch = document.createElement('i');
            swatch.className = 'tide-chart-swatch ' + Array.from(dot.classList).filter(function (c) { return /^tide-chart-[sd]\d+$/.test(c); }).join(' ');
            line.appendChild(swatch);
            const name = dot.getAttribute('data-name');
            line.appendChild(document.createTextNode((name ? name + ': ' : '') + (dot.getAttribute('data-value') || '')));
            lines.push(line);
            top = Math.min(top, Number.parseFloat(dot.style.top) || 0);
        });
        const more = stop.getAttribute('data-more');
        if (more) {
            const line = document.createElement('small');
            line.className = 'tide-chart-tip-more';
            line.textContent = more;
            lines.push(line);
        }
        tip.replaceChildren(...lines);
        // Beside the point, on whichever side has room; below the highest value when it's in the top half, else above
        // it. Then clamped into the chart, so it never runs off its edge or the screen's.
        tip.style.left = '0px';
        tip.style.top = '0px';
        tip.style.right = '';
        tip.style.bottom = '';
        tip.classList.add('show');
        const width = plot.clientWidth, height = plot.clientHeight, w = tip.offsetWidth, h = tip.offsetHeight;
        const x = stop.offsetLeft, y = height * top / 100;
        const left = x > width / 2 ? x - 10 - w : x + 10;
        const below = top < 50 ? y : y - h;
        tip.style.left = Math.max(0, Math.min(left, width - w)) + 'px';
        tip.style.top = Math.max(0, Math.min(below, height - h)) + 'px';
    }

    function nearest(plot, clientX) {
        let best = null, bestDistance = Infinity;
        stops(plot).forEach(function (s) {
            const r = s.getBoundingClientRect();
            const d = Math.abs(r.left + r.width / 2 - clientX);
            if (d < bestDistance) { best = s; bestDistance = d; }
        });
        return best;
    }

    document.addEventListener('pointermove', function (e) {
        const plot = e.target.closest?.('[data-chart]');
        if (plot) show(plot, nearest(plot, e.clientX));
    });

    document.addEventListener('pointerout', function (e) {
        const plot = e.target.closest?.('[data-chart]');
        if (!plot || plot.contains(e.relatedTarget)) return;
        if (document.activeElement === plot) show(plot, plot.querySelector('.tide-chart-stop.active'));
        else hide(plot);
    });

    document.addEventListener('focusin', function (e) {
        const plot = e.target.closest?.('[data-chart]');
        if (plot) { const all = stops(plot); show(plot, plot.querySelector('.tide-chart-stop.active') || all[all.length - 1]); }
    });

    document.addEventListener('focusout', function (e) {
        const plot = e.target.closest?.('[data-chart]');
        if (plot && !plot.contains(e.relatedTarget)) hide(plot);
    });

    document.addEventListener('keydown', function (e) {
        const plot = e.target.closest?.('[data-chart]');
        if (!plot) return;
        if (e.key === 'Escape') { hide(plot); return; }
        const all = stops(plot);
        if (all.length === 0) return;
        const at = all.indexOf(plot.querySelector('.tide-chart-stop.active'));
        let next;
        switch (e.key) {
            case 'ArrowLeft': case 'ArrowDown': next = at < 0 ? all.length - 1 : Math.max(0, at - 1); break;
            case 'ArrowRight': case 'ArrowUp': next = at < 0 ? all.length - 1 : Math.min(all.length - 1, at + 1); break;
            case 'Home': next = 0; break;
            case 'End': next = all.length - 1; break;
            default: return;
        }
        e.preventDefault();
        show(plot, all[next]);
    });
})();

// A chart's motion. The first time a chart's plot comes into view on a page, it sweeps in from the left over
// --tide-dur-signature, a thin scan edge travelling with it (an instrument tracing its reading). Once per chart per page
// view: a plot in the same place on the same page (a re-render, or Blazor building it again for a new range) has
// already been seen, so it never sweeps again. When the lines change under a plot (weekly to quarterly, 30 days to a
// year, a line taken off), the old reading fades out over the new one fading in, over --tide-dur-fast: opacity only, no
// sweep. With less motion asked for there is no sweep, only that fade.
(function () {
    const t = window.tideShortcuts;
    if (!t || !window.IntersectionObserver || !window.MutationObserver) return;
    const plots = document.getElementsByClassName('tide-chart-plot');
    const places = new WeakMap();   // a plot: its place on the page (the how-manyth chart)
    let path = location.pathname, readings = new Map();   // a place on this page: its chart's last reading

    // A copy of the plot as it reads now, to fade out over its next reading: no tooltip, no point marked.
    function snap(plot) {
        const c = plot.cloneNode(true);
        c.querySelector('.tide-chart-tip')?.remove();
        c.querySelectorAll('.active').forEach(function (n) { n.classList.remove('active'); });
        c.classList.add('tide-chart-ghost');
        c.removeAttribute('data-tide-sweep');
        c.removeAttribute('data-chart');
        c.removeAttribute('tabindex');
        return c;
    }

    // Uncovers the plot from the left, clipped a little outside it so the dots on its edges show whole; the clip goes
    // when it's done. The scan edge is the frame's (::after), not the plot's, so the clip never cuts it off: it moves
    // over the plot on the line between shown and not yet. Started `from` so far in when it carries on a sweep the plot
    // it replaced was making.
    function sweep(plot, from) {
        const frame = plot.parentElement;
        frame.setAttribute('data-tide-scan', '');   // first: it makes the frame the plot's offset parent
        const x = plot.offsetLeft, w = plot.offsetWidth;
        frame.style.setProperty('--tide-scan-height', plot.offsetHeight + 'px');
        const opts = { duration: t.ms('--tide-dur-signature'), easing: t.token('--tide-ease-grab'), fill: 'forwards' };
        const a = plot.animate([{ clipPath: 'inset(-8px calc(100% + 8px) -8px -8px)' }, { clipPath: 'inset(-8px)' }], opts);
        let edge = null;
        try {
            edge = frame.animate([{ transform: 'translateX(' + (x - 9) + 'px)', opacity: 1 }, { opacity: 1, offset: .85 }, { transform: 'translateX(' + (x + w + 7) + 'px)', opacity: 0 }],
                Object.assign({ pseudoElement: '::after' }, opts));
        } catch { /* a browser that can't animate a pseudo-element: the plot sweeps without its edge */ }
        if (from) { a.currentTime = from; if (edge) edge.currentTime = from; }
        const done = function () {
            plot.removeAttribute('data-tide-sweep');
            frame.removeAttribute('data-tide-scan');
            frame.style.removeProperty('--tide-scan-height');
            a.cancel();
            edge?.cancel();
        };
        a.finished.then(done, done);
        return a;
    }

    // Watched through the plot's frame (its axis and dates around it), as the browser counts a plot clipped away to
    // nothing as out of view.
    const framed = new WeakMap();   // a frame: the plot in it waiting to sweep
    const view = new IntersectionObserver(function (entries) {
        entries.forEach(function (e) {
            const plot = framed.get(e.target);
            if (!e.isIntersecting || !plot) return;
            view.unobserve(e.target);
            framed.delete(e.target);
            const r = readings.get(places.get(plot)) ?? {};
            r.waiting = false;
            if (t.quiet()) plot.removeAttribute('data-tide-sweep');
            else r.sweep = sweep(plot);
        });
    }, { threshold: .25 });
    function hold(plot) {
        plot.setAttribute('data-tide-sweep', '');
        const frame = plot.parentElement;
        framed.set(frame, plot);
        view.unobserve(frame);
        view.observe(frame);
    }

    // What the plot reads now: a copy to fade out later, and its lines and bands, to tell a new reading from the same one
    // drawn again (as Blazor does, taking over the prerendered page).
    function lines(plot) {
        return Array.from(plot.querySelectorAll('svg polyline, svg rect'), function (n) {
            return [n.getAttribute('class'), n.getAttribute('points'), n.getAttribute('y'), n.getAttribute('height')].join(' ');
        }).join('|');
    }
    function reading(plot, now) { return { snap: snap(plot), lines: now === undefined ? lines(plot) : now }; }

    // The plot may read differently: if so, the last reading fades out over this one fading in (when it's on screen).
    function changed(plot) {
        const at = places.get(plot), old = readings.get(at), drawn = lines(plot);
        if (old && old.lines === drawn) return;   // the same reading drawn again: nothing to copy
        const now = reading(plot, drawn);
        now.waiting = plot.hasAttribute('data-tide-sweep');
        readings.set(at, now);
        if (!old || document.hidden || !document.body.animate || plot.hasAttribute('data-tide-sweep')) return;
        const r = plot.getBoundingClientRect();
        if (r.bottom < 0 || r.top > innerHeight) return;
        const opts = { duration: t.ms('--tide-dur-fast'), easing: t.token('--tide-ease-fade') };
        t.drift(t.copy(old.snap, r), [{ opacity: 1 }, { opacity: 0 }], opts);
        plot.animate([{ opacity: 0 }, { opacity: 1 }], opts);
    }

    function meet(plot) {
        const at = Array.prototype.indexOf.call(plots, plot), old = readings.get(at);
        places.set(plot, at);
        const svg = plot.querySelector('svg');
        if (svg) new MutationObserver(function () { changed(plot); })
            .observe(svg, { subtree: true, childList: true, attributes: true, attributeFilter: ['points', 'class', 'y', 'height'] });
        // Built again where a chart already showed (Blazor taking over the prerendered page, or drawing it again for a
        // new range): a change, not a debut, unless the one it replaces was still waiting to sweep, which this one does,
        // or sweeping, which this one carries on from where it got to.
        if (old?.sweep?.playState === 'running') {
            plot.setAttribute('data-tide-sweep', '');
            readings.set(at, Object.assign(reading(plot), { sweep: sweep(plot, old.sweep.currentTime) }));
            return;
        }
        if (old && !old.waiting) { changed(plot); return; }
        readings.set(at, Object.assign(reading(plot), { waiting: !t.still() }));
        if (!t.still()) hold(plot);
    }

    // New plots are met once a frame at most (before they're first painted), however much else Blazor changes.
    let queued = false;
    function scan() {
        queued = false;
        if (location.pathname !== path) { path = location.pathname; readings = new Map(); }
        for (const plot of plots) if (!places.has(plot) && !plot.hasAttribute('data-tide-copy')) meet(plot);
    }
    scan();
    new MutationObserver(function () { if (!queued) { queued = true; requestAnimationFrame(scan); } })
        .observe(document.body, { childList: true, subtree: true });
})();
