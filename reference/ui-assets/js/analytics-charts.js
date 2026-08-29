// Rewrites MudChart x-axis <text> labels (raw single-line) into <tspan> lines using
// C#-computed wrap lines. MudChart owns the label <text> and its `x`, so this runs post-render.
// Lines are applied via textContent/tspan text (encoded) — also neutralising the raw MarkupString
// injection MudChart does at first paint. (AI-1324)
(function () {
  'use strict';
  const SVG_NS = 'http://www.w3.org/2000/svg';
  const LINE_DY = '1.15em';
  const OBS_OPTS = { childList: true, subtree: true, characterData: true };

  function labelTexts(container) {
    return container ? container.querySelectorAll('g.mud-charts-xaxis > text') : [];
  }

  function paint(container, lines) {
    const texts = labelTexts(container);
    if (texts.length !== lines.length) return false; // index guard: mid-render or mismatch
    for (let i = 0; i < texts.length; i++) {
      const el = texts[i];
      const parts = lines[i];
      if (!Array.isArray(parts) || parts.length === 0) continue;
      const x = el.getAttribute('x');
      if (parts.length === 1) {
        if (el.childElementCount === 0 && el.textContent === parts[0]) continue; // already correct
        el.textContent = parts[0];
        continue;
      }
      while (el.firstChild) el.removeChild(el.firstChild);
      for (let j = 0; j < parts.length; j++) {
        const t = document.createElementNS(SVG_NS, 'tspan');
        t.setAttribute('x', x);
        if (j > 0) t.setAttribute('dy', LINE_DY);
        t.textContent = parts[j];
        el.appendChild(t);
      }
    }
    return true;
  }

  function observe(container) {
    const g = container.querySelector('g.mud-charts-xaxis');
    if (!g || g.__labelObserver) return;
    const obs = new MutationObserver(function () {
      obs.disconnect();                       // MudChart rewrote labels: re-apply, guard self-mutations
      paint(container, g.__labelLines || []);
      obs.observe(g, OBS_OPTS);
    });
    obs.observe(g, OBS_OPTS);
    g.__labelObserver = obs;
  }

  window.capacitorWrapAxisLabels = function (container, lines) {
    if (!container || !Array.isArray(lines)) return;
    let tries = 0;
    (function attempt() {
      if (labelTexts(container).length === lines.length) {
        const g = container.querySelector('g.mud-charts-xaxis');
        if (g) g.__labelLines = lines;        // keep observer's lines fresh across data changes
        paint(container, lines);
        observe(container);
        return;
      }
      if (tries++ < 30) requestAnimationFrame(attempt); // wait out MudChart's async settle
    })();
  };

  // Disconnect the per-chart observer when the chart/component is removed (VisualRenderer dispose),
  // so long-lived sessions don't retain observers/DOM refs. Best-effort; safe if already gone.
  window.capacitorWrapAxisLabelsTeardown = function (container) {
    const g = container && container.querySelector && container.querySelector('g.mud-charts-xaxis');
    if (!g) return;
    if (g.__labelObserver) { g.__labelObserver.disconnect(); delete g.__labelObserver; }
    delete g.__labelLines;
  };
})();
