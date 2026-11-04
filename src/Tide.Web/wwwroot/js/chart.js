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
