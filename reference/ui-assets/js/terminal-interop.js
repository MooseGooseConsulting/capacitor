// Store interactive (PTY-driven) terminal instances by terminalId.
window._terminals = {};

// AI-721: read-only terminal cache keyed by agentId. xterm instances survive
// Blazor component teardown so switching tabs/agents doesn't re-stream the
// whole buffer from the server. The detached DOM lives in a hidden holder
// (created lazily) until the next attach.
window._roTerminals = {};
window._roHolder    = null;

function _getRoHolder() {
    if (window._roHolder) return window._roHolder;
    var holder = document.createElement('div');
    holder.id = '__ro_terminal_holder__';
    holder.style.position = 'absolute';
    holder.style.left = '-99999px';
    holder.style.top = '0';
    holder.style.width = '1px';
    holder.style.height = '1px';
    holder.style.overflow = 'hidden';
    holder.setAttribute('aria-hidden', 'true');
    document.body.appendChild(holder);
    window._roHolder = holder;
    return holder;
}

// Detect terminal report sequences that xterm.js generates in response to
// queries from the running program (e.g. cursor position, device attributes).
// These must NOT be forwarded back to the PTY as user input — doing so creates
// a feedback loop where the program receives its own query responses as keystrokes,
// which can cause interactive prompts (like AskUserQuestion) to auto-select defaults.
//
// Patterns matched:
//   Cursor Position Report (CPR):  ESC [ Pn ; Pn R
//   Primary Device Attributes:     ESC [ ? Pn c
//   Secondary Device Attributes:   ESC [ > Pn c
//   Device Status Report response: ESC [ Pn n
var _termReportRe = /^\x1b\[[\?>]?[\d;]*[Rcn]$/;

// Strip DEC mode 2026 (synchronized output) sequences at byte level.
// Pattern: ESC(1b) [(5b) ?(3f) 2(32) 0(30) 2(32) 6(36) h(68)/l(6c)
function stripSyncOutput(buf) {
    var seq = [0x1b, 0x5b, 0x3f, 0x32, 0x30, 0x32, 0x36]; // \e[?2026
    var out = [];
    var i = 0;
    while (i < buf.length) {
        if (i + 8 <= buf.length &&
            buf[i] === seq[0] && buf[i+1] === seq[1] && buf[i+2] === seq[2] &&
            buf[i+3] === seq[3] && buf[i+4] === seq[4] && buf[i+5] === seq[5] &&
            buf[i+6] === seq[6] && (buf[i+7] === 0x68 || buf[i+7] === 0x6c)) {
            i += 8; // skip the sequence
        } else {
            out.push(buf[i]);
            i++;
        }
    }
    return new Uint8Array(out);
}

// Common write path used by both interactive and read-only terminals. `entry` is
// the cache record holding the xterm instance and a pending-chunks buffer; bytes
// are batched on requestAnimationFrame to avoid partial renders from Claude's
// Ink TUI.
function _writeBytesToEntry(entry, base64Data) {
    if (!entry) return;

    const binaryString = atob(base64Data);
    const bytes = new Uint8Array(binaryString.length);
    for (let i = 0; i < binaryString.length; i++) {
        bytes[i] = binaryString.charCodeAt(i);
    }

    if (!entry.pendingChunks) entry.pendingChunks = [];
    entry.pendingChunks.push(bytes);

    if (!entry.flushScheduled) {
        entry.flushScheduled = true;
        requestAnimationFrame(function () {
            if (entry.disposed) return;

            entry.flushScheduled = false;
            var chunks = entry.pendingChunks;
            entry.pendingChunks = [];

            var totalLen = 0;
            for (var i = 0; i < chunks.length; i++) totalLen += chunks[i].length;
            var merged = new Uint8Array(totalLen);
            var offset = 0;
            for (var i = 0; i < chunks.length; i++) {
                merged.set(chunks[i], offset);
                offset += chunks[i].length;
            }

            merged = stripSyncOutput(merged);

            try { entry.term.write(merged); } catch (e) { /* terminal disposed */ }
        });
    }
}

/**
 * Shared terminal setup. Creates xterm.js instance with fit/canvas addons and resize observer.
 * @param {string} containerId - DOM element ID to attach the terminal to
 * @param {string} terminalId - Unique ID for this terminal instance
 * @param {object} extraOpts - Extra Terminal constructor options (e.g. { disableStdin: true })
 * @returns {{ term, fitAddon, container }} or null if container not found
 */
function _createTerminal(containerId, terminalId, extraOpts) {
    const container = document.getElementById(containerId);
    if (!container) return null;

    const term = new Terminal(Object.assign({
        cols: 120,
        rows: 40,
        cursorBlink: false,
        cursorStyle: 'bar',
        cursorInactiveStyle: 'none',
        fontSize: 14,
        fontFamily: "'Spline Sans Mono', monospace",
        scrollback: 10000,
    }, extraOpts || {}));

    const fitAddon = new FitAddon.FitAddon();
    term.loadAddon(fitAddon);

    term.open(container);

    // Use canvas renderer for reliable rendering in WebKit WebView.
    // Skip on Chromium-based WebView2 (Windows) — WebGL renderer works fine there.
    var isWebKit = /AppleWebKit/.test(navigator.userAgent) && !/Chrome/.test(navigator.userAgent);
    if (isWebKit) {
        try {
            const canvasAddon = new CanvasAddon.CanvasAddon();
            term.loadAddon(canvasAddon);
        } catch (e) { /* fall back to default renderer */ }
    }

    // Fit terminal to container with safety clamp.
    function safeFit() {
        var entry = window._terminals[terminalId];
        if (entry && entry.fixedSize) return; // Skip auto-fit when locked to source dimensions
        try {
            if (container.offsetWidth > 0 && container.offsetHeight > 0) {
                var dims = fitAddon.proposeDimensions();
                if (dims && dims.cols > 0 && dims.cols <= 500 && dims.rows > 0 && dims.rows <= 200) {
                    fitAddon.fit();
                }
            }
        } catch (e) { /* ignore fit errors */ }
    }

    safeFit();
    var fitTimeoutId = setTimeout(safeFit, 100);

    var resizeObserver = new ResizeObserver(function () { safeFit(); });
    resizeObserver.observe(container);

    window._terminals[terminalId] = { term: term, fitAddon: fitAddon, resizeObserver: resizeObserver, disposed: false, fixedSize: false, fitTimeoutId: fitTimeoutId };
    return { term, fitAddon, container };
}

// AI-884: a hosted-agent PTY runs at a fixed size (e.g. 120×40) and the daemon
// reports those dims so the read-only viewer locks its xterm to them — rendering
// the TUI at exactly the width Claude drew for, instead of auto-fitting the panel
// (a column mismatch garbles cursor positioning). Because the grid is fixed, we
// scale the FONT so the whole 120×40 letterboxes into the panel: pick the smaller
// of the width/height ratios, clamp to a readable range. Runs on lock and on every
// container resize. `entry.container` must be the live (attached) container.
function _fitFixedFontReadOnly(entry) {
    if (!entry || entry.disposed || !entry.fixedSize) return;
    var term = entry.term, el = entry.element, container = entry.container;
    if (!term || !el || !container) return;

    requestAnimationFrame(function () {
        if (entry.disposed || !entry.fixedSize) return;
        try {
            var availW = container.clientWidth;
            var availH = container.clientHeight;
            // Skip while parked off-screen (hidden holder is ~1px) — the next
            // attach re-fits against the real container.
            if (availW < 50 || availH < 50) return;

            var naturalW = el.offsetWidth;
            var naturalH = el.offsetHeight;
            if (naturalW <= 0 || naturalH <= 0) return;

            var current = term.options.fontSize || 14;
            // Cell size scales linearly with font size, so one pass lands on target.
            var ratio  = Math.min(availW / naturalW, availH / naturalH);
            var target = current * ratio;
            target = Math.max(6, Math.min(14, Math.round(target * 2) / 2));

            if (Math.abs(target - current) >= 0.5) term.options.fontSize = target;
        } catch (e) { /* ignore */ }
    });
}

// AI-973: report a writable viewer's container grid (cols × rows that fit at the fixed base font)
// so the server can min-clamp the shared PTY across all viewers (tmux semantics). Keyed off the
// CONTAINER — not the rendered grid — so the daemon's clamp→announce (setFixedSizeReadOnly) can't
// feed back into a resize loop. Only writers carry a dotNetRef; read-only viewers letterbox instead.
function _reportViewportReadOnly(entry) {
    if (!entry || entry.disposed || !entry.dotNetRef || !entry.container) return;
    try {
        var dims = entry.fitAddon.proposeDimensions();
        if (!dims || !(dims.cols > 0) || !(dims.rows > 0)) return;

        var cols = Math.max(1, Math.min(500, dims.cols));
        var rows = Math.max(1, Math.min(200, dims.rows));

        if (entry._lastReported && entry._lastReported.cols === cols && entry._lastReported.rows === rows) return;

        entry._lastReported = { cols: cols, rows: rows };
        entry.dotNetRef.invokeMethodAsync('OnWebViewportResized', entry.agentId, cols, rows);
    } catch (e) { /* ignore */ }
}

// Debounce reports so a drag-resize doesn't spam the server.
function _scheduleViewportReport(entry) {
    if (!entry || !entry.dotNetRef) return;
    if (entry._reportTimer) clearTimeout(entry._reportTimer);
    entry._reportTimer = setTimeout(function () {
        entry._reportTimer = null;
        _reportViewportReadOnly(entry);
    }, 150);
}

// A writable viewer renders the server-clamped grid 1:1 at the base font — the clamp includes this
// viewer's reported size, so it normally fits the panel exactly. But if the daemon did NOT clamp to
// our report (e.g. a daemon predating resize aggregation that ignores the aggregate and keeps a
// larger PTY), the base-font grid would overflow the panel; fall back to font-letterbox so nothing
// is clipped. Loop-safe: the report fires on container resize (this observer), not on font changes,
// so letterboxing the font here never feeds back into a new report.
function _applyWriterFit(entry) {
    if (!entry || entry.disposed || !entry.fixedSize || !entry.container) return;

    if (entry.term.options.fontSize !== 14) { try { entry.term.options.fontSize = 14; } catch (e) { /* ignore */ } }

    requestAnimationFrame(function () {
        if (entry.disposed || !entry.fixedSize) return;
        var el = entry.element, c = entry.container;
        if (!el || !c) return;
        if (el.offsetWidth > c.clientWidth + 1 || el.offsetHeight > c.clientHeight + 1) _fitFixedFontReadOnly(entry);
    });
}

// Shared ResizeObserver body for the read-only cache. Writers report their viewport AND re-fit the
// current clamped grid (1:1, or letterbox if an un-clamping daemon left it oversized); read-only
// viewers fit/letterbox.
function _onRoContainerResize(entry) {
    if (!entry || entry.disposed) return;

    if (entry.dotNetRef) { _scheduleViewportReport(entry); _applyWriterFit(entry); return; }
    if (entry.fixedSize) { _fitFixedFontReadOnly(entry); return; }

    try {
        if (entry.container && entry.container.offsetWidth > 0 && entry.container.offsetHeight > 0) {
            var dims = entry.fitAddon.proposeDimensions();
            if (dims && dims.cols > 0 && dims.cols <= 500 && dims.rows > 0 && dims.rows <= 200) entry.fitAddon.fit();
        }
    } catch (e) { /* ignore */ }
}

window.terminalInterop = {
    /**
     * Creates a new xterm.js terminal with input and resize callbacks.
     * @param {string} containerId - DOM element ID to attach the terminal to
     * @param {string} terminalId - Unique ID for this terminal instance
     * @param {object} dotNetRef - .NET object reference for callbacks
     */
    create: function (containerId, terminalId, dotNetRef) {
        var result = _createTerminal(containerId, terminalId);
        if (!result) return;

        // Send user input back to .NET (filter out terminal report responses)
        result.term.onData(function (data) {
            if (_termReportRe.test(data)) return;
            dotNetRef.invokeMethodAsync('OnTerminalInput', terminalId, data);
        });

        // Notify .NET on resize (only for sane values)
        result.term.onResize(function (size) {
            if (size.cols > 0 && size.cols <= 500 && size.rows > 0 && size.rows <= 200) {
                dotNetRef.invokeMethodAsync('OnTerminalResize', terminalId, size.cols, size.rows);
            }
        });
    },

    /**
     * AI-721: attach (or create) a read-only xterm for the given agentId. If an
     * xterm already exists in the cache, its DOM is moved into the new container
     * and the existing scrollback is preserved — the caller MUST NOT trigger a
     * server-side replay in that case (signalled via the returned `created` flag).
     * @returns {{ created: boolean, cols: number, rows: number }}
     */
    attachReadOnly: function (containerId, agentId, dotNetRef) {
        const container = document.getElementById(containerId);
        if (!container) return { created: false, cols: 0, rows: 0 };

        var entry = window._roTerminals[agentId];

        if (entry && !entry.disposed) {
            // Reattach existing terminal DOM into the new container.
            try {
                if (entry.element && entry.element.parentNode !== container) {
                    container.appendChild(entry.element);
                }
            } catch (e) { /* DOM races during navigation — best-effort */ }

            // Track the live container so the fixed-size font-fit measures the
            // right element after a tab switch / re-mount.
            entry.container = container;
            // AI-973: refresh the writer ref (null for read-only viewers) and clear the last
            // reported size so the new container's grid is reported afresh.
            entry.dotNetRef    = dotNetRef || null;
            entry._lastReported = null;

            // Reconnect the resize observer to the new container.
            try {
                if (entry.resizeObserver) entry.resizeObserver.disconnect();
                entry.resizeObserver = new ResizeObserver(function () { _onRoContainerResize(entry); });
                entry.resizeObserver.observe(container);
            } catch (e) { /* ignore */ }

            // Re-fit / re-report on next tick — container dimensions may differ
            // from where the terminal was last mounted.
            setTimeout(function () {
                if (entry.dotNetRef) { _reportViewportReadOnly(entry); return; }
                if (entry.fixedSize) { _fitFixedFontReadOnly(entry); return; }
                try { entry.fitAddon.fit(); } catch (e) { /* ignore */ }
            }, 0);

            return { created: false, cols: entry.term.cols, rows: entry.term.rows };
        }

        // Cold start: create the xterm.
        const term = new Terminal({
            cols: 120,
            rows: 40,
            cursorBlink: false,
            cursorStyle: 'bar',
            cursorInactiveStyle: 'none',
            fontSize: 14,
            fontFamily: "'Spline Sans Mono', monospace",
            scrollback: 10000,
            disableStdin: true
        });

        const fitAddon = new FitAddon.FitAddon();
        term.loadAddon(fitAddon);
        term.open(container);

        var isWebKit = /AppleWebKit/.test(navigator.userAgent) && !/Chrome/.test(navigator.userAgent);
        if (isWebKit) {
            try {
                const canvasAddon = new CanvasAddon.CanvasAddon();
                term.loadAddon(canvasAddon);
            } catch (e) { /* fall back */ }
        }

        // The DOM node xterm just created — `.xterm` element under container.
        var element = container.querySelector('.xterm');

        var newEntry = {
            term: term,
            fitAddon: fitAddon,
            element: element,
            container: container,
            agentId: agentId,
            disposed: false,
            fixedSize: false,
            pendingChunks: [],
            flushScheduled: false,
            resizeObserver: null,
            dotNetRef: dotNetRef || null,   // AI-973: present only for writable viewers
            _lastReported: null,
            _reportTimer: null
        };

        var resizeObserver = new ResizeObserver(function () { _onRoContainerResize(newEntry); });
        resizeObserver.observe(container);
        newEntry.resizeObserver = resizeObserver;

        // Initial fit gives a sensible starting grid before the server responds; a delayed pass
        // catches late layout. Writers then report their viewport so the server min-clamps the PTY.
        try { fitAddon.fit(); } catch (e) { /* ignore */ }
        setTimeout(function () {
            if (newEntry.disposed) return;
            if (newEntry.dotNetRef) { _reportViewportReadOnly(newEntry); return; }
            if (!newEntry.fixedSize) {
                try { fitAddon.fit(); } catch (e) { /* ignore */ }
            }
        }, 100);

        window._roTerminals[agentId] = newEntry;
        return { created: true, cols: term.cols, rows: term.rows };
    },

    /**
     * AI-721: detach the read-only xterm from its current container without
     * disposing. The DOM element is parked in a hidden holder so xterm's internal
     * buffer survives until the next attachReadOnly call (or disposeReadOnly).
     */
    detachReadOnly: function (agentId) {
        var entry = window._roTerminals[agentId];
        if (!entry || entry.disposed) return;

        try {
            if (entry.resizeObserver) {
                entry.resizeObserver.disconnect();
                entry.resizeObserver = null;
            }
        } catch (e) { /* ignore */ }

        // AI-973: cancel any pending viewport report — the observer is gone and the container is
        // about to be nulled, so a deferred report would fire against a parked/disposed terminal.
        if (entry._reportTimer) { clearTimeout(entry._reportTimer); entry._reportTimer = null; }

        // Drop the reference to the live container so the cached entry (which
        // outlives the Blazor component on purpose) doesn't pin the detached
        // container node and its parent chain in memory across navigation /
        // tab switches. attachReadOnly re-sets it on the next attach, and the
        // fixed-size font-fit early-returns while it's null (AI-884).
        entry.container = null;

        try {
            if (entry.element) {
                _getRoHolder().appendChild(entry.element);
            }
        } catch (e) { /* DOM may have already been torn down */ }
    },

    /**
     * Creates a read-only xterm.js terminal (no input, no resize callbacks).
     * @param {string} containerId - DOM element ID to attach the terminal to
     * @param {string} terminalId - Unique ID for this terminal instance
     */
    createReadOnly: function (containerId, terminalId) {
        _createTerminal(containerId, terminalId, { disableStdin: true });
    },

    /**
     * Writes raw PTY bytes to the interactive terminal (base64-encoded).
     * Passes Uint8Array directly to xterm.js so its internal stateful UTF-8
     * decoder handles multi-byte characters split across PTY reads.
     */
    write: function (terminalId, base64Data) {
        _writeBytesToEntry(window._terminals[terminalId], base64Data);
    },

    /**
     * AI-721: writes raw PTY bytes to the read-only terminal cached under
     * <paramref name="agentId"/>. Safe to call when no terminal is currently
     * attached — output accumulates against the cached xterm and is rendered
     * on the next animation frame. NOTE: a write for an agent with NO cached
     * xterm is dropped (_writeBytesToEntry returns for a missing entry), which
     * is why a COLD mount must attachReadOnly BEFORE its subscribe replays
     * (see hasReadOnly / ReadOnlyTerminalView, AI-1562 FIX 2).
     */
    writeReadOnly: function (agentId, base64Data) {
        _writeBytesToEntry(window._roTerminals[agentId], base64Data);
    },

    /**
     * AI-1562 FIX 2: whether a read-only xterm is currently cached (created and not disposed) for this
     * agent. Lets ReadOnlyTerminalView distinguish a COLD mount (no xterm yet — attach FIRST so a
     * subscribe's historical replay lands in the freshly-created terminal instead of being dropped by
     * writeReadOnly) from a PRIOR-VIEWER-CACHED one (re-gate/clear FIRST, then attach).
     */
    hasReadOnly: function (agentId) {
        var entry = window._roTerminals[agentId];
        return !!(entry && !entry.disposed);
    },

    /**
     * Sets the terminal to a fixed size (cols x rows) and disables auto-fit.
     * Used for read-only terminals that must match the source terminal's dimensions.
     */
    setFixedSize: function (terminalId, cols, rows) {
        const entry = window._terminals[terminalId];
        if (!entry) return;

        // Mark as fixed so safeFit() no-ops
        entry.fixedSize = true;

        // Cancel pending delayed fit from _createTerminal
        if (entry.fitTimeoutId) {
            clearTimeout(entry.fitTimeoutId);
            entry.fitTimeoutId = null;
        }

        // Disconnect resize observer so fit addon doesn't override our size
        if (entry.resizeObserver) {
            entry.resizeObserver.disconnect();
            entry.resizeObserver = null;
        }

        try { entry.term.resize(cols, rows); } catch (e) { /* ignore */ }
    },

    /**
     * AI-721: setFixedSize variant for the read-only agent-keyed cache.
     */
    setFixedSizeReadOnly: function (agentId, cols, rows) {
        const entry = window._roTerminals[agentId];
        if (!entry) return;

        entry.fixedSize = true;

        // Keep the resize observer connected so the locked grid keeps tracking the panel.
        try { entry.term.resize(cols, rows); } catch (e) { /* ignore */ }

        // AI-973: a writable viewer renders the server-clamped grid 1:1 at the base font (its own
        // reported size is part of the clamp, so it normally fits), with a letterbox fallback if an
        // un-clamping daemon left the PTY larger than the panel. Read-only viewers (no reporting)
        // always letterbox the announced grid via font-scaling (AI-884).
        if (entry.dotNetRef) {
            _applyWriterFit(entry);
        } else {
            _fitFixedFontReadOnly(entry);
        }
    },

    /**
     * AI-973: force the writable read-only terminal to re-report its viewport size, bypassing the
     * _lastReported dedupe. Used after a transport reconnect — the server dropped this connection's
     * dims on the drop, so the size must be re-sent even though it hasn't changed locally. No-op for
     * read-only viewers (no dotNetRef) and parked/disposed terminals.
     */
    refreshViewportReport: function (agentId) {
        var entry = window._roTerminals[agentId];
        if (!entry || entry.disposed) return;

        entry._lastReported = null;
        _reportViewportReadOnly(entry);
    },

    /**
     * Focuses the terminal so it receives keyboard input.
     */
    focus: function (terminalId) {
        const entry = window._terminals[terminalId];
        if (entry) entry.term.focus();
    },

    /**
     * Resizes the terminal to fit its container.
     */
    fit: function (terminalId) {
        const entry = window._terminals[terminalId];
        if (entry) {
            try { entry.fitAddon.fit(); } catch (e) { }
        }
    },

    /**
     * Gets the current terminal dimensions.
     */
    getDimensions: function (terminalId) {
        const entry = window._terminals[terminalId];
        if (entry && entry.term.cols > 0) {
            return { cols: entry.term.cols, rows: entry.term.rows };
        }
        return { cols: 120, rows: 40 };
    },

    /**
     * Disposes of an interactive terminal instance.
     */
    dispose: function (terminalId) {
        const entry = window._terminals[terminalId];
        if (entry) {
            entry.disposed = true; // Prevent pending RAF callbacks from writing
            if (entry.resizeObserver) entry.resizeObserver.disconnect();
            entry.term.dispose();
            delete window._terminals[terminalId];
        }
    },

    /**
     * AI-721: clear the cached xterm scrollback for an agent without disposing
     * the instance. Used by TerminalSubscriptionCache on reconnect so the
     * server's buffer-replay doesn't append on top of pre-disconnect output.
     */
    clearReadOnly: function (agentId) {
        const entry = window._roTerminals[agentId];
        if (!entry || entry.disposed) return;
        try { entry.term.reset(); } catch (e) { /* ignore */ }
    },

    /**
     * AI-721: fully dispose a cached read-only terminal. Use this when the
     * agent ends and the buffer is no longer needed (or on circuit teardown).
     */
    disposeReadOnly: function (agentId) {
        const entry = window._roTerminals[agentId];
        if (!entry) return;

        entry.disposed = true;
        try { if (entry.resizeObserver) entry.resizeObserver.disconnect(); } catch (e) { /* ignore */ }
        try { entry.term.dispose(); } catch (e) { /* ignore */ }
        try {
            if (entry.element && entry.element.parentNode) {
                entry.element.parentNode.removeChild(entry.element);
            }
        } catch (e) { /* ignore */ }
        delete window._roTerminals[agentId];
    }
};
