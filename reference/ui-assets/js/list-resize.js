window.Capacitor = window.Capacitor || {};

window.Capacitor.initListResize = function (layoutEl, resizerEl, opts) {
    if (!layoutEl || !resizerEl) return;
    if (resizerEl.dataset.kapacitorResizer === '1') return;
    resizerEl.dataset.kapacitorResizer = '1';

    var KEY = 'kapacitor.layout.listPanelWidth.v1';
    var MIN = (opts && opts.min) || 240;
    var MAX = (opts && opts.max) || 560;

    function clamp(n) {
        return Math.min(MAX, Math.max(MIN, n));
    }

    function readPersistedWidth() {
        try {
            var raw = localStorage.getItem(KEY);
            var n = parseInt(raw || '', 10);
            return Number.isFinite(n) ? clamp(n) : null;
        } catch (e) {
            return null;
        }
    }

    function setWidth(px) {
        layoutEl.style.setProperty('--list-panel-width', px + 'px');
    }

    function currentWidth() {
        var v = parseInt(getComputedStyle(layoutEl).getPropertyValue('--list-panel-width'), 10);
        return Number.isFinite(v) ? v : 320;
    }

    var saved = readPersistedWidth();
    if (saved !== null) setWidth(saved);

    var dragging = false;
    var startX = 0;
    var startW = 0;
    var prevUserSelect = '';

    function onMove(e) {
        if (!dragging) return;
        setWidth(clamp(startW + (e.clientX - startX)));
    }

    function stop() {
        if (!dragging) return;
        dragging = false;
        resizerEl.classList.remove('dragging');
        // Restore the prior inline value rather than blanking it.
        document.body.style.userSelect = prevUserSelect;
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', stop);
        window.removeEventListener('pointercancel', stop);
        try {
            localStorage.setItem(KEY, String(currentWidth()));
        } catch (e) {
            // Storage disabled / quota exceeded — silently no-op.
        }
    }

    resizerEl.addEventListener('pointerdown', function (e) {
        dragging = true;
        startX = e.clientX;
        startW = currentWidth();
        prevUserSelect = document.body.style.userSelect;
        // setPointerCapture keeps the col-resize cursor continuous during drag.
        // Listeners go on window so a release outside the resizer still stops the drag.
        try { resizerEl.setPointerCapture(e.pointerId); } catch (_) { }
        resizerEl.classList.add('dragging');
        document.body.style.userSelect = 'none';
        window.addEventListener('pointermove', onMove);
        window.addEventListener('pointerup', stop);
        window.addEventListener('pointercancel', stop);
        e.preventDefault();
    });
};
