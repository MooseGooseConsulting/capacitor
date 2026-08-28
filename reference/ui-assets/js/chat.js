// Programmatic scroll-to-bottom with suppression to avoid triggering user scroll detection.
// Used for explicit scrolls (FAB button, conversation switch).
// Keeps scrolling until scrollHeight stabilizes — needed because content-visibility: auto
// causes elements to render at placeholder height first, then expand to actual size across
// multiple frames as the viewport moves.
window.chatScrollToBottom = (element) => {
    if (!element) return;
    element._chatScrollToken = (element._chatScrollToken || 0) + 1;
    const token = element._chatScrollToken;
    element._chatProgrammaticScroll = true;

    let stableFrames = 0;
    let lastHeight = -1;

    const settle = () => {
        if (element._chatScrollToken !== token) return; // superseded by newer scroll
        element.scrollTop = element.scrollHeight;

        if (element.scrollHeight === lastHeight) {
            stableFrames++;
        } else {
            stableFrames = 0;
            lastHeight = element.scrollHeight;
        }

        if (stableFrames < 3) {
            requestAnimationFrame(settle);
        } else {
            element._chatProgrammaticScroll = false;
        }
    };
    settle();
};

// Sets up content-driven auto-scroll using MutationObserver + ResizeObserver.
// Automatically scrolls to bottom when content grows, unless the user has scrolled up.
// Also handles user scroll position detection (replaces the old chatScrollSetup).
// Distance (px) from the bottom within which the view is considered "pinned".
// Shared by the scroll listener and the auto-scroll decision so they agree.
const CHAT_AT_BOTTOM_THRESHOLD = 50;

window.chatScrollSetup = (element, dotnetRef) => {
    if (!element) return;

    let autoScrollEnabled = true;
    let lastScrollHeight = element.scrollHeight;
    let checkRafId = 0;

    // --- Auto-scroll: detect content height changes and scroll if enabled ---

    const doScroll = () => {
        element._chatScrollToken = (element._chatScrollToken || 0) + 1;
        const token = element._chatScrollToken;
        element._chatProgrammaticScroll = true;

        let stableFrames = 0;
        let lastHeight = -1;

        const settle = () => {
            if (element._chatScrollToken !== token) return;
            element.scrollTop = element.scrollHeight;

            if (element.scrollHeight === lastHeight) {
                stableFrames++;
            } else {
                stableFrames = 0;
                lastHeight = element.scrollHeight;
            }

            if (stableFrames < 3) {
                requestAnimationFrame(settle);
            } else {
                element._chatProgrammaticScroll = false;
            }
        };
        settle();
    };

    const checkAndScroll = () => {
        checkRafId = 0;
        const sh = element.scrollHeight;
        if (sh !== lastScrollHeight) {
            const grew = sh > lastScrollHeight;
            // Was the view pinned to the bottom of the content as it existed *before*
            // this growth? Measured against the previous height (lastScrollHeight), not
            // the new one — so a freshly appended message below the fold still counts as
            // "at bottom" and gets followed (streaming), while content-visibility: auto
            // resolving off-screen turn heights mid-scroll does NOT (the user has scrolled
            // away from the bottom, so prevHeight - scrollTop - clientHeight is large).
            // Without this, that virtualization growth yanked the view to the bottom every
            // frame during a user scroll — severe strobe in Firefox (AI-831).
            const wasAtBottom = lastScrollHeight - element.scrollTop - element.clientHeight < CHAT_AT_BOTTOM_THRESHOLD;
            lastScrollHeight = sh;
            if (grew && autoScrollEnabled && wasAtBottom) doScroll();
        }
    };

    const scheduleCheck = () => {
        if (!checkRafId) checkRafId = requestAnimationFrame(checkAndScroll);
    };

    // ResizeObserver on direct children — catches content-visibility resolution and layout shifts
    const ro = new ResizeObserver(scheduleCheck);
    for (const child of element.children) ro.observe(child);

    // MutationObserver — catches DOM changes (Blazor diffing, new messages).
    // Only observe/unobserve direct children with ResizeObserver (m.target === element)
    // to avoid accumulating observed descendants. Deep mutations still trigger scheduleCheck.
    const mo = new MutationObserver((mutations) => {
        for (const m of mutations) {
            if (m.type === 'childList' && m.target === element) {
                for (const node of m.addedNodes)
                    if (node.nodeType === Node.ELEMENT_NODE) ro.observe(node);
                for (const node of m.removedNodes)
                    if (node.nodeType === Node.ELEMENT_NODE) ro.unobserve(node);
            }
        }
        scheduleCheck();
    });
    mo.observe(element, { childList: true, subtree: true, characterData: true });

    // --- Scroll listener: detect user scroll position ---

    let scrollRafId = 0;
    const scrollHandler = () => {
        if (scrollRafId) return;
        scrollRafId = requestAnimationFrame(() => {
            scrollRafId = 0;
            if (element._chatProgrammaticScroll) return;
            const isAtBottom = element.scrollHeight - element.scrollTop - element.clientHeight < CHAT_AT_BOTTOM_THRESHOLD;
            autoScrollEnabled = isAtBottom;
            dotnetRef.invokeMethodAsync('OnScrollChanged', isAtBottom);
        });
    };

    element.addEventListener('scroll', scrollHandler, { passive: true });

    // --- Control interface ---

    element._chatAutoScroll = {
        reset: () => {
            autoScrollEnabled = true;
            lastScrollHeight = 0;
        },
        cleanup: () => {
            mo.disconnect();
            ro.disconnect();
            if (checkRafId) cancelAnimationFrame(checkRafId);
            if (scrollRafId) cancelAnimationFrame(scrollRafId);
            element.removeEventListener('scroll', scrollHandler);
        }
    };
};

window.chatScrollTeardown = (element) => {
    if (element?._chatAutoScroll) {
        element._chatAutoScroll.cleanup();
        delete element._chatAutoScroll;
    }
};

// Reset auto-scroll state (conversation switch, FAB click).
// Re-enables auto-scroll and resets height tracking so the next content change triggers a scroll.
// Also does an immediate scroll-to-bottom since content may already be in the DOM.
window.chatAutoScrollReset = (element) => {
    if (!element) return;
    if (element._chatAutoScroll) element._chatAutoScroll.reset();
    chatScrollToBottom(element);
};

window.chatInputSetup = (element, dotnetRef) => {
    if (!element) return;
    // Find the textarea inside the MudTextField wrapper
    const textarea = element.querySelector('textarea');
    if (!textarea) return;

    const handler = (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnEnterPressed');
        } else if (e.key === 'Escape') {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnEscapePressed');
        }
    };

    textarea.addEventListener('keydown', handler);
    element._chatInputHandler = handler;
    element._chatInputTextarea = textarea;
};

window.chatInputTeardown = (element) => {
    if (element && element._chatInputHandler && element._chatInputTextarea) {
        element._chatInputTextarea.removeEventListener('keydown', element._chatInputHandler);
        delete element._chatInputHandler;
        delete element._chatInputTextarea;
    }
};

// Analytics chat input (AI-982): handle ONLY the action keys (Enter submit, ↑/↓ history recall)
// client-side so caret/selection keys (Home, End, Left, Right, Shift+*) stay fully native — the
// single-line MudTextField previously routed every keydown through a Blazor Server round-trip whose
// post-handler re-render reset the caret. The listener is on the STABLE wrapper (keydown bubbles) so
// it survives the inner <input> being re-rendered when the field toggles `disabled` during a
// request; the input is resolved per-event so a swapped node is still matched. Returns whether it
// attached so the component never records "wired" on a silent no-op.
window.analyticsInputSetup = (element, dotnetRef) => {
    if (!element) return false;
    if (element._analyticsInputHandler)                  // idempotent: drop any prior handler
        element.removeEventListener('keydown', element._analyticsInputHandler);

    const handler = (e) => {
        const input = element.querySelector('input');    // per-event → robust to node swap, scoped
        if (!input || e.target !== input) return;        // only the analytics text field
        if (e.isComposing || e.keyCode === 229) return;  // never act mid-IME-composition

        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnAsk', input.value);        // exact DOM value → no stale bind
        } else if (e.key === 'ArrowUp' && !e.shiftKey && !e.altKey && !e.metaKey && !e.ctrlKey) {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnRecallPrev', input.value); // value → correct draft stash
        } else if (e.key === 'ArrowDown' && !e.shiftKey && !e.altKey && !e.metaKey && !e.ctrlKey) {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnRecallNext', input.value);
        } else if (e.key === 'Home' && !e.shiftKey && !e.altKey && !e.metaKey && !e.ctrlKey) {
            // WebKit/Safari doesn't move the input caret on plain Home/End — do it explicitly so the
            // box behaves like a normal text box in every engine. No value change → no Blazor round-trip.
            e.preventDefault();
            input.setSelectionRange(0, 0);
        } else if (e.key === 'End' && !e.shiftKey && !e.altKey && !e.metaKey && !e.ctrlKey) {
            e.preventDefault();
            input.setSelectionRange(input.value.length, input.value.length);
        }
        // Left / Right / Shift+* / Ctrl+* / Cmd+* / typing → untouched → native.
    };

    element.addEventListener('keydown', handler);
    element._analyticsInputHandler = handler;
    return true;
};

window.analyticsInputTeardown = (element) => {
    if (element && element._analyticsInputHandler) {
        element.removeEventListener('keydown', element._analyticsInputHandler);
        delete element._analyticsInputHandler;
    }
};

window.chatAttachmentSetup = (wrapper, fileInputId, dotnetRef) => {
    if (!wrapper) return;
    const fileInput = document.getElementById(fileInputId);

    // Paste handler — intercept image paste on textarea
    const textarea = wrapper.querySelector('textarea');
    if (textarea) {
        const pasteHandler = (e) => {
            const items = e.clipboardData?.items;
            if (!items) return;
            for (const item of items) {
                if (item.kind === 'file') {
                    e.preventDefault();
                    const file = item.getAsFile();
                    if (file && fileInput) {
                        const dt = new DataTransfer();
                        dt.items.add(file);
                        fileInput.files = dt.files;
                        fileInput.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                    return;
                }
            }
        };
        textarea.addEventListener('paste', pasteHandler);
        wrapper._chatPasteHandler = pasteHandler;
        wrapper._chatPasteTextarea = textarea;
    }

    // Drag-and-drop handler
    const dragOverHandler = (e) => {
        e.preventDefault();
        wrapper.classList.add('chat-drag-over');
    };
    const dragLeaveHandler = (e) => {
        e.preventDefault();
        wrapper.classList.remove('chat-drag-over');
    };
    const dropHandler = (e) => {
        e.preventDefault();
        wrapper.classList.remove('chat-drag-over');
        if (e.dataTransfer?.files?.length > 0 && fileInput) {
            fileInput.files = e.dataTransfer.files;
            fileInput.dispatchEvent(new Event('change', { bubbles: true }));
        }
    };

    wrapper.addEventListener('dragover', dragOverHandler);
    wrapper.addEventListener('dragleave', dragLeaveHandler);
    wrapper.addEventListener('drop', dropHandler);
    wrapper._chatDragHandlers = { dragOverHandler, dragLeaveHandler, dropHandler };
};

window.chatAttachmentTeardown = (wrapper) => {
    if (!wrapper) return;
    if (wrapper._chatPasteHandler && wrapper._chatPasteTextarea) {
        wrapper._chatPasteTextarea.removeEventListener('paste', wrapper._chatPasteHandler);
        delete wrapper._chatPasteHandler;
        delete wrapper._chatPasteTextarea;
    }
    if (wrapper._chatDragHandlers) {
        wrapper.removeEventListener('dragover', wrapper._chatDragHandlers.dragOverHandler);
        wrapper.removeEventListener('dragleave', wrapper._chatDragHandlers.dragLeaveHandler);
        wrapper.removeEventListener('drop', wrapper._chatDragHandlers.dropHandler);
        delete wrapper._chatDragHandlers;
    }
};

window.chatAttachmentSetupById = (wrapperId, fileInputId, dotnetRef) => {
    const wrapper = document.getElementById(wrapperId);
    if (wrapper) chatAttachmentSetup(wrapper, fileInputId, dotnetRef);
    return !!wrapper;
};

window.chatAttachmentTeardownById = (wrapperId) => {
    const wrapper = document.getElementById(wrapperId);
    if (wrapper) chatAttachmentTeardown(wrapper);
};

window.chatClickElement = (id) => {
    const el = document.getElementById(id);
    if (el) el.click();
};

window.chatCreateObjectUrl = (dotnetStreamRef) => {
    return new Promise(async (resolve) => {
        const arrayBuffer = await dotnetStreamRef.arrayBuffer();
        const blob = new Blob([arrayBuffer]);
        resolve(URL.createObjectURL(blob));
    });
};

window.chatRevokeObjectUrl = (url) => {
    if (url) URL.revokeObjectURL(url);
};

// AI-1685 — deck download: read the whole .NET stream, hand the browser a typed Blob via a
// synthetic anchor click. The returned Promise resolves ONLY after read + click + revoke, so the
// .NET `await` (which owns the DotNetStreamReference's `using` scope) can't dispose the stream
// mid-read, and any failure surfaces to the caller's catch. Anchor downloads don't need transient
// user activation, so this is exempt from the AI-1455/AI-1487 Safari clipboard-gesture constraint.
window.kcapDownloadFile = async (fileName, contentType, dotnetStreamRef) => {
    const arrayBuffer = await dotnetStreamRef.arrayBuffer();
    const url = URL.createObjectURL(new Blob([arrayBuffer], { type: contentType }));
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    // Defer cleanup: revoking the object URL synchronously right after click() can truncate or
    // cancel the download in some browsers before the navigation has started. The stream has already
    // been fully read into arrayBuffer above, so the awaiting .NET side may safely dispose the
    // DotNetStreamReference once this resolves — only the Blob URL must outlive the click.
    setTimeout(() => { a.remove(); URL.revokeObjectURL(url); }, 1000);
};

window.taskListScrollToBottom = (el) => {
    if (el) el.scrollTop = el.scrollHeight;
};

window.chatFocusPermission = () => {
    // Focus the permission panel so keyboard shortcuts work immediately
    const panel = document.querySelector('[tabindex="0"].pa-3');
    if (panel) panel.focus();
};

// ---------------------------------------------------------------------------
// Client-side clipboard copy (AI-931 / AI-765 / AI-1455).
//
// In Blazor Server an @onclick handler round-trips to the server over SignalR, so any IJSRuntime
// clipboard call runs in a later task — OUTSIDE the click's transient user activation — and
// navigator.clipboard then rejects with NotAllowedError. The copy must therefore happen on the
// client, synchronously inside the click event. Copy buttons carry a data-kcap-copy (text) or
// data-kcap-copy-image (chart) attribute; the delegated listener below performs the write within
// the gesture. Blazor's @onclick still drives the ✓ feedback (optimistic in Server mode).
//
// AI-1455: Safari additionally DENIES the async navigator.clipboard.write() ClipboardItem API on our
// origin (text/html AND image/png), while writeText() and document.execCommand('copy') work. So rich
// text/table copy goes through the synchronous execCommand+setData path (kcapExecCopyRich) in every
// browser, and the chart uses clipboard.write on non-WebKit but a pre-rasterized <img data:> via
// execCommand on WebKit (Google Docs embeds it; Slack gets the title/caption text).
// ---------------------------------------------------------------------------

function kcapExecCopyFallback(text) {
    try {
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.style.cssText = 'position:fixed;top:0;left:0;width:1px;height:1px;opacity:0';
        document.body.appendChild(ta);
        ta.focus();
        ta.select();
        document.execCommand('copy');
        document.body.removeChild(ta);
    } catch { /* nothing more we can do */ }
}

function kcapCopyText(text) {
    // Runs within the user gesture, so the async API is permitted; fall back to execCommand.
    if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).catch(() => kcapExecCopyFallback(text));
    } else {
        kcapExecCopyFallback(text);
    }
}

// Safari denies the async navigator.clipboard.write() API (ClipboardItem — text/html AND image/png)
// on our origin (NotAllowedError) even inside a user gesture, while writeText() and the synchronous
// document.execCommand('copy') both work (AI-1455). This narrow predicate matches WebKit engines
// (desktop + iOS Safari and all iOS shells, which share WebKit's clipboard restrictions) and EXCLUDES
// Blink (desktop/Android Chrome, Chromium, Edge). The async-write denial is not feature-detectable
// (the API exists and only rejects at call time, async, after the gesture is gone), so we can't
// try/fallback within one gesture — we branch up front. Used ONLY for the chart path (text/tables use
// one uniform execCommand path either way), so a mis-classification can affect only the chart.
function kcapIsWebKitClipboardDenied() {
    const ua = navigator.userAgent || '';
    return /AppleWebKit/.test(ua) && !/(Chrome|Chromium|Android)/i.test(ua);
}

// Rich copy that works in EVERY browser (incl. Safari, which denies clipboard.write): synchronously
// set text/html + text/plain via a copy-event handler driven by document.execCommand('copy'), inside
// the click gesture. We set the flavors ourselves (never a selection serialization, which in Safari
// captured the dark-theme computed white text → invisible on Docs). Strict contract: a non-empty
// off-screen selection so the copy event fires; explicit catch so a throw still falls back; cleanup +
// selection/focus restore in finally; on any failure a synchronous kcapCopyText(plain) in the SAME
// task (so activation is still valid). Returns whether the rich write succeeded.
function kcapExecCopyRich(html, plain) {
    const sel = window.getSelection ? window.getSelection() : null;
    const savedRanges = [];
    if (sel) for (let i = 0; i < sel.rangeCount; i++) savedRanges.push(sel.getRangeAt(i).cloneRange());
    const savedActive = document.activeElement;

    let fired = false, ok = false;
    const handler = (e) => {
        if (!e.clipboardData) return;
        e.preventDefault();
        e.clipboardData.setData('text/html', html);
        e.clipboardData.setData('text/plain', plain);
        fired = true;
    };

    const node = document.createElement('div');
    node.textContent = ' ';                 // non-empty so the range isn't collapsed
    node.setAttribute('aria-hidden', 'true');
    node.style.cssText = 'position:fixed;left:-9999px;top:0;white-space:pre;user-select:text';
    document.body.appendChild(node);
    document.addEventListener('copy', handler, true);
    try {
        if (sel) {
            const range = document.createRange();
            range.selectNodeContents(node);
            sel.removeAllRanges();
            sel.addRange(range);
        }
        ok = document.execCommand('copy');       // returns false OR throws on failure
    } catch {
        ok = false;
    } finally {
        document.removeEventListener('copy', handler, true);
        node.remove();
        if (sel) {
            sel.removeAllRanges();
            for (const r of savedRanges) sel.addRange(r);
        }
        if (savedActive && typeof savedActive.focus === 'function') {
            try { savedActive.focus({ preventScroll: true }); } catch { /* not focusable */ }
        }
    }

    const success = ok && fired;
    if (!success) kcapCopyText(plain);           // synchronous fallback, same click task
    return success;
}

// Whitelist of semantic tags kept in the portable HTML; everything else is unwrapped (children kept).
const KCAP_ALLOWED_TAGS = new Set(['H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'P', 'BR', 'STRONG', 'B',
    'EM', 'I', 'CODE', 'PRE', 'UL', 'OL', 'LI', 'TABLE', 'THEAD', 'TBODY', 'TR', 'TH', 'TD', 'A',
    'BLOCKQUOTE']);
const KCAP_SAFE_HREF = /^(https?|mailto):/i;

// Conservative CSV/formula-injection guard for a cell's text (CWE-1236): a leading = + - @ can execute
// on paste into Sheets. The answer's rendered DOM carries no cell-type metadata (unlike the Razor
// table path, which exempts real numbers), so we quote ANY dangerous prefix — a numeric-looking -5
// becomes '-5. Acceptable: answer tables are prose, rarely computed in Sheets.
function kcapCellGuard(text) {
    return /^[=+\-@]/.test(text) ? "'" + text : text;
}

// A cell/heading's direct inline text (whitespace-collapsed), excluding nested block containers so a
// <li>/<td> that holds a nested list or table isn't duplicated when the walker recurses into them.
function kcapDirectText(el) {
    let s = '';
    for (const c of el.childNodes) {
        if (c.nodeType === Node.TEXT_NODE) s += c.nodeValue;
        else if (c.nodeType === Node.ELEMENT_NODE && !/^(UL|OL|TABLE|PRE)$/.test(c.tagName)) s += kcapDirectText(c);
    }
    return s.replace(/\s+/g, ' ').trim();
}

// Clone a rendered Markdown subtree into clean, self-contained, portable HTML: whitelist tags, unwrap
// everything else (drops MudBlazor wrapper div/span + classes), drop every attribute except a
// scheme-allowlisted href on <a> (SafeMarkdown's DisableHtml blocks raw HTML but NOT link schemes, so
// [x](javascript:…) would otherwise round-trip), add minimal table borders, NO theme colors (targets
// apply their default → readable). Built by cloning nodes (never string concat), so text stays
// escaped. Returns a detached container; use .innerHTML.
function kcapSanitizeToPortableHtml(node) {
    const out = document.createElement('div');
    const walk = (src, dest) => {
        for (const child of src.childNodes) {
            if (child.nodeType === Node.TEXT_NODE) {
                dest.appendChild(document.createTextNode(child.nodeValue));
            } else if (child.nodeType === Node.ELEMENT_NODE) {
                if (child.matches && child.matches('[data-kcap-copy],[data-kcap-copy-image],button')) continue;
                const tag = child.tagName;
                if (tag === 'A') {
                    const href = (child.getAttribute('href') || '').trim();
                    if (KCAP_SAFE_HREF.test(href)) {
                        const a = document.createElement('a');
                        a.setAttribute('href', href);
                        walk(child, a);
                        dest.appendChild(a);
                    } else {
                        walk(child, dest);          // unsafe scheme → drop the <a>, keep its text
                    }
                } else if (KCAP_ALLOWED_TAGS.has(tag)) {
                    const el = document.createElement(tag.toLowerCase());
                    if (tag === 'TABLE') el.setAttribute('style', 'border-collapse:collapse');
                    else if (tag === 'TH' || tag === 'TD') el.setAttribute('style', 'border:1px solid #999;padding:2px 6px');
                    walk(child, el);
                    dest.appendChild(el);
                } else {
                    walk(child, dest);              // unwrap: keep children, drop the element
                }
            }
        }
    };
    walk(node, out);
    // Conservative formula guard on the HTML flavor's cells — test the SAME normalized (trimmed,
    // whitespace-collapsed) text the plain path uses (kcapDirectText), so a leading-whitespace value
    // like "   =2+3" (which Sheets still evaluates as a formula on paste) is guarded in BOTH flavors,
    // not just plain.
    out.querySelectorAll('td, th').forEach((cell) => {
        if (/^[=+\-@]/.test(kcapDirectText(cell))) cell.insertBefore(document.createTextNode("'"), cell.firstChild);
    });
    return out;
}

// A table's rows as space-aligned columns (pad each cell to its column's max width, two-space gutter),
// with the conservative cell guard. Never emits Markdown pipes.
function kcapTableToAlignedLines(table) {
    const rows = [];
    table.querySelectorAll('tr').forEach((tr) => {
        const cells = Array.from(tr.children)
            .filter((c) => /^(TD|TH)$/.test(c.tagName))
            .map((c) => kcapCellGuard(kcapDirectText(c)));
        if (cells.length) rows.push(cells);
    });
    if (!rows.length) return [];
    const cols = Math.max(...rows.map((r) => r.length));
    const widths = [];
    for (let i = 0; i < cols; i++) widths[i] = Math.max(0, ...rows.map((r) => (r[i] || '').length));
    return rows.map((r) => {
        const padded = [];
        for (let i = 0; i < cols; i++) padded.push((r[i] || '').padEnd(widths[i]));
        return padded.join('  ').replace(/\s+$/, '');
    });
}

// A <ul>/<ol> as plain lines: "- " / "1. 2. …" markers, two-space indent per nesting depth, only each
// <li>'s DIRECT content on its line (nested lists are emitted by the recursion, not duplicated).
function kcapListToLines(list, depth, ordered, out) {
    let i = 1;
    Array.from(list.children).filter((c) => c.tagName === 'LI').forEach((li) => {
        const marker = ordered ? (i++) + '. ' : '- ';
        const pad = '  '.repeat(depth);
        out.push(pad + marker + kcapDirectText(li));
        // Emit block content nested directly in the item — nested lists, code blocks, tables, quotes —
        // in document order, indented under the item (kcapDirectText excluded these, so without this
        // they'd be dropped from the plain flavor).
        const childPad = pad + '  ';
        for (const child of li.children) {
            switch (child.tagName) {
                case 'UL': kcapListToLines(child, depth + 1, false, out); break;
                case 'OL': kcapListToLines(child, depth + 1, true, out); break;
                case 'PRE':
                    child.textContent.replace(/\n+$/, '').split('\n').forEach((l) => out.push(childPad + l));
                    break;
                case 'TABLE':
                    kcapTableToAlignedLines(child).forEach((l) => out.push(childPad + l));
                    break;
                case 'BLOCKQUOTE': {
                    const q = kcapDirectText(child);
                    if (q) out.push(childPad + q);
                    break;
                }
            }
        }
    });
}

// Plain-text rendering of a rendered Markdown subtree (already Markdown-syntax-free): tables → aligned
// columns, lists → recursive markers, <pre>/<code> → verbatim whitespace, paragraphs/headings → text
// lines. Never emits | / ** / #. This is the Slack-facing flavor (Slack takes text/plain from Safari).
function kcapDomToAlignedText(node) {
    const out = [];
    const walk = (el) => {
        for (const child of el.childNodes) {
            if (child.nodeType === Node.TEXT_NODE) {
                const t = child.nodeValue.replace(/\s+/g, ' ').trim();
                if (t) out.push(t);
            } else if (child.nodeType === Node.ELEMENT_NODE) {
                if (child.matches && child.matches('[data-kcap-copy],[data-kcap-copy-image],button')) continue;
                const tag = child.tagName;
                if (tag === 'TABLE') { out.push(...kcapTableToAlignedLines(child)); out.push(''); }
                else if (tag === 'UL') { kcapListToLines(child, 0, false, out); out.push(''); }
                else if (tag === 'OL') { kcapListToLines(child, 0, true, out); out.push(''); }
                else if (tag === 'PRE') { out.push(child.textContent.replace(/\n+$/, '')); out.push(''); }
                else if (/^(H[1-6]|P|BLOCKQUOTE)$/.test(tag)) { const t = kcapDirectText(child); if (t) out.push(t); out.push(''); }
                else { walk(child); }               // unwrap containers (div/span/…)
            }
        }
    };
    walk(node);
    // Collapse consecutive blank SEPARATORS at the array level (not a global \n{3,} regex), so blank
    // lines INSIDE a <pre> block — a single array element — survive verbatim per the design; then drop
    // trailing blank separators.
    const lines = [];
    for (const l of out) {
        if (l === '' && lines[lines.length - 1] === '') continue;
        lines.push(l);
    }
    while (lines.length && lines[lines.length - 1] === '') lines.pop();
    return lines.join('\n');
}

// Copy the computed paint/text styles from the live SVG onto the detached clone so the
// rasterized PNG keeps the theme's colours. MudChart's axis text colour comes from CSS, not
// inline attributes, so a bare clone renders text as default black. Walks live + clone in lockstep.
function kcapInlineSvgStyles(liveSvg, cloneSvg) {
    const props = ['fill', 'fill-opacity', 'stroke', 'stroke-width', 'stroke-opacity',
        'stroke-dasharray', 'stroke-linecap', 'stroke-linejoin', 'color', 'opacity',
        'font-family', 'font-size', 'font-weight', 'font-style', 'text-anchor', 'dominant-baseline'];
    const live = [liveSvg, ...liveSvg.querySelectorAll('*')];
    const clone = [cloneSvg, ...cloneSvg.querySelectorAll('*')];
    for (let i = 0; i < live.length && i < clone.length; i++) {
        const cs = getComputedStyle(live[i]);
        for (const p of props) {
            const v = cs.getPropertyValue(p);
            if (v) clone[i].style.setProperty(p, v);
        }
    }
}

// The chart's effective background = nearest ancestor with a non-transparent background, so the
// PNG matches what's behind the chart on screen (e.g. the dark answer bubble) rather than a
// mismatched surface colour.
function kcapEffectiveBackground(el) {
    for (let n = el; n && n.nodeType === 1; n = n.parentElement) {
        const bg = getComputedStyle(n).backgroundColor;
        if (bg && bg !== 'transparent' && bg !== 'rgba(0, 0, 0, 0)') return bg;
    }
    return getComputedStyle(document.body).backgroundColor || '#ffffff';
}

// Read the text colour + font shorthand from a live element, for redrawing onto the canvas.
function kcapTextStyle(el, fallbackColor) {
    if (!el) return { color: fallbackColor, font: '' };
    const cs = getComputedStyle(el);
    return { color: cs.color || fallbackColor, font: cs.font };
}

// Copy the chart inside `element` (an .an-chart wrapper) to the clipboard as a PNG. The SVG holds
// the bars + axis labels, but the title (a sibling MudText before .an-chart) and the legend
// (MudChart's .mud-chart-legend HTML beside the svg) are NOT in the svg — so we rasterize the svg
// and redraw the title/legend/caption around it onto a composed canvas. Everything is read from
// the live, styled DOM synchronously (within the gesture); navigator.clipboard.write is then
// called synchronously with a Promise-backed ClipboardItem so the async work keeps the activation.
// Greedy word-wrap for canvas text: split into lines that fit maxWidth.
function kcapWrapText(ctx, text, maxWidth) {
    const words = text.split(/\s+/);
    const lines = [];
    let line = '';
    for (const w of words) {
        const test = line ? line + ' ' + w : w;
        if (line && ctx.measureText(test).width > maxWidth) { lines.push(line); line = w; }
        else line = test;
    }
    if (line) lines.push(line);
    return lines;
}

// Chart → PNG (AI-931). Split (AI-1455) so the raster inputs can be fingerprinted synchronously (for
// the Safari prime path) and the async raster reused by both branches:
//  - kcapBuildChartRasterModel(element): SYNCHRONOUS. Reads every input that affects the pixels
//    (styled SVG, dims, dpr, background, title/x-axis/caption/legend text+styles) and exposes a
//    canonical JSON fingerprint over all of them — so a theme toggle / responsive resize / style
//    change is detected at click time even when the raw SVG markup is unchanged.
//  - kcapComposeChartCanvas(model): async (Image.onload + canvas compose).
function kcapBuildChartRasterModel(element) {
    const svg = element && element.querySelector('svg');
    if (!svg) return null;

    const rect = svg.getBoundingClientRect();
    const chartW = Math.max(1, Math.round(rect.width));
    const chartH = Math.max(1, Math.round(rect.height));
    const background = kcapEffectiveBackground(element);

    // Title, x-axis title and caption are siblings inside the .an-card around the chart.
    const card = element.closest('.an-card');
    const titleEl = card && card.querySelector('.an-card-title');
    const title = titleEl ? titleEl.textContent.trim() : '';
    const titleStyle = kcapTextStyle(titleEl, '#ffffff');
    const xTitleEl = card && card.querySelector('.an-axis-x-title');
    const xTitle = xTitleEl ? xTitleEl.textContent.trim() : '';
    const xTitleStyle = kcapTextStyle(xTitleEl, '#cccccc');
    const captionEl = card && card.querySelector('.an-card-caption');
    const caption = captionEl ? captionEl.textContent.trim() : '';
    const captionStyle = kcapTextStyle(captionEl, '#cccccc');

    // Legend items: coloured marker + label, rendered as HTML beside the svg.
    const legend = Array.from(element.querySelectorAll('.mud-chart-legend-item')).map((it) => {
        const marker = it.querySelector('.mud-chart-legend-marker');
        const label = it.querySelector('.mud-typography') || it;
        return {
            color: marker ? getComputedStyle(marker).backgroundColor : '#888888',
            label: (label.textContent || '').trim(),
            style: kcapTextStyle(label, '#cccccc'),
        };
    });

    // Clone the svg, inline its computed paint/text styles, stamp dimensions, serialize.
    const clone = svg.cloneNode(true);
    kcapInlineSvgStyles(svg, clone);
    clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
    clone.setAttribute('width', chartW);
    clone.setAttribute('height', chartH);
    const xml = new XMLSerializer().serializeToString(clone);
    const dpr = window.devicePixelRatio || 2;

    const model = { xml, chartW, chartH, background, dpr, title, titleStyle, xTitle, xTitleStyle, caption, captionStyle, legend };
    // Canonical fingerprint over EVERY pixel-affecting input — named fixed-shape fields (not
    // delimiter-free concatenation, so attacker-controlled title/legend text can't forge a boundary),
    // compared later by string equality (no lossy hash → no collision).
    model.fingerprint = JSON.stringify({
        xml, chartW, chartH, background, dpr, title, titleStyle, xTitle, xTitleStyle, caption, captionStyle,
        legend: legend.map((l) => ({ color: l.color, label: l.label, style: l.style })),
    });
    return model;
}

function kcapComposeChartCanvas(model) {
    const { xml, chartW, chartH, background, dpr, title, titleStyle, xTitle, xTitleStyle, caption, captionStyle, legend } = model;
    const svgUrl = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(xml);

    // Stacked layout: [title] [chart] [legend] [x-axis title] [caption]. Pack legend items into rows.
    const padX = 8, rowH = 22, sw = 12, swGap = 6, itemGap = 18;
    const titleH = title ? 30 : 0;
    const xTitleH = xTitle ? 24 : 0;
    const totalW = chartW;

    const legendRows = [];
    if (legend.length) {
        const mctx = document.createElement('canvas').getContext('2d');
        mctx.font = legend[0].style.font || '12px sans-serif';
        const maxRowW = Math.max(1, totalW - 2 * padX);
        let row = [], rowW = 0;
        for (const item of legend) {
            const w = sw + swGap + mctx.measureText(item.label).width;
            if (row.length && rowW + itemGap + w > maxRowW) { legendRows.push(row); row = []; rowW = 0; }
            row.push({ ...item, w });
            rowW += (row.length > 1 ? itemGap : 0) + w;
        }
        if (row.length) legendRows.push(row);
    }
    const legendH = legendRows.length * rowH;

    let captionLines = [];
    if (caption) {
        const cctx = document.createElement('canvas').getContext('2d');
        cctx.font = captionStyle.font || '12px sans-serif';
        captionLines = kcapWrapText(cctx, caption, totalW - 2 * padX);
    }
    const captionLineH = 16;
    const captionH = captionLines.length ? captionLines.length * captionLineH + 6 : 0;
    const totalH = titleH + chartH + legendH + xTitleH + captionH;

    return (async () => {
        const img = new Image();
        await new Promise((resolve, reject) => { img.onload = resolve; img.onerror = reject; img.src = svgUrl; });

        const canvas = document.createElement('canvas');
        canvas.width = totalW * dpr;
        canvas.height = totalH * dpr;
        const ctx = canvas.getContext('2d');
        ctx.scale(dpr, dpr);

        ctx.fillStyle = background;
        ctx.fillRect(0, 0, totalW, totalH);
        ctx.textBaseline = 'middle';

        let y = 0;
        if (title) {
            ctx.fillStyle = titleStyle.color;
            ctx.font = titleStyle.font || '600 14px sans-serif';
            ctx.textAlign = 'center';
            ctx.fillText(title, totalW / 2, y + titleH / 2);
            y += titleH;
        }

        ctx.textAlign = 'left';
        ctx.drawImage(img, 0, y, chartW, chartH);
        y += chartH;

        legendRows.forEach((row, ri) => {
            const rowW = row.reduce((a, it) => a + it.w, 0) + itemGap * (row.length - 1);
            let lx = Math.max(padX, (totalW - rowW) / 2);
            const ly = y + ri * rowH + rowH / 2;
            row.forEach((it) => {
                ctx.fillStyle = it.color;
                ctx.fillRect(lx, ly - sw / 2, sw, sw);
                ctx.fillStyle = it.style.color;
                ctx.font = it.style.font || '12px sans-serif';
                ctx.textAlign = 'left';
                ctx.fillText(it.label, lx + sw + swGap, ly);
                lx += it.w + itemGap;
            });
        });
        y += legendH;

        if (xTitle) {
            ctx.fillStyle = xTitleStyle.color;
            ctx.font = xTitleStyle.font || '12px sans-serif';
            ctx.textAlign = 'center';
            ctx.fillText(xTitle, totalW / 2, y + xTitleH / 2);
            y += xTitleH;
        }

        if (captionLines.length) {
            ctx.fillStyle = captionStyle.color;
            ctx.font = captionStyle.font || '12px sans-serif';
            ctx.textAlign = 'left';
            y += 4;
            captionLines.forEach((line, li) => ctx.fillText(line, padX, y + li * captionLineH + captionLineH / 2));
            y += captionLines.length * captionLineH;
        }

        return canvas;
    })();
}

function kcapRasterizeModelToBlob(model) {
    return kcapComposeChartCanvas(model).then((canvas) => new Promise((resolve, reject) => {
        canvas.toBlob((b) => b ? resolve(b) : reject(new Error('chart toBlob returned null')), 'image/png');
    }));
}

function kcapRasterizeModelToDataUrl(model) {
    return kcapComposeChartCanvas(model).then((canvas) => canvas.toDataURL('image/png'));
}

// The chart's title + caption as plain text — the Safari Slack fallback (Slack can't embed an
// <img data:>) and the touch/keyboard/not-primed fallback.
function kcapChartTitleCaption(element) {
    const card = element.closest('.an-card');
    const t = card && card.querySelector('.an-card-title');
    const c = card && card.querySelector('.an-card-caption');
    return [t ? t.textContent.trim() : '', c ? c.textContent.trim() : ''].filter(Boolean).join('\n');
}

// Non-WebKit chart copy: full PNG onto the clipboard via the async ClipboardItem (works in Chrome).
function kcapChartWriteImage(element) {
    if (!navigator.clipboard || !window.ClipboardItem) return;
    const model = kcapBuildChartRasterModel(element);
    if (!model) return;
    navigator.clipboard.write([new ClipboardItem({ 'image/png': kcapRasterizeModelToBlob(model) })]).catch(() => { });
}

// WebKit-only chart prime: the async raster must be ready BEFORE the click (execCommand can't await).
// Direct per-element wiring — pointerenter/pointerleave do NOT bubble, so this can't be delegated —
// guarded by a generation counter so a leave/re-enter discards a late-resolving raster. Idempotent
// (a re-call on an already-wired element is a no-op). VisualRenderer owns the setup/teardown lifecycle.
window.kcapChartPrimeSetup = (element) => {
    if (!element || element._kcapPrimeWired) return;
    element._kcapPrimeWired = true;
    if (!kcapIsWebKitClipboardDenied()) return;   // non-WebKit copies via clipboard.write at click time
    element._kcapPrimeGen = 0;
    const rasterize = () => {
        const g = ++element._kcapPrimeGen;
        const model = kcapBuildChartRasterModel(element);
        if (!model) return;
        kcapRasterizeModelToDataUrl(model).then((png) => {
            if (g === element._kcapPrimeGen) element._kcapChartPng = { png, fingerprint: model.fingerprint };
        }).catch(() => { /* leave unprimed → title/caption text fallback at click */ });
    };

    // Re-prime while the pointer is over the chart. MudChart re-renders the svg IN RESPONSE to the
    // hover (tooltip markup + `overflow: visible` on the root; measured 12.8 KB → 16.8 KB), and that
    // render lands AFTER pointerenter — so a raster primed only on enter is already stale when
    // kcapChartCopyClick re-derives the model, and every hover-then-click copy silently degraded to
    // title text instead of the image. Observing the subtree and re-rastering once mutations stop
    // keeps the primed fingerprint matching what the click sees. Priming never mutates the live
    // chart (it rasterizes a clone), so this cannot feed itself.
    let debounce = 0;
    const observer = typeof MutationObserver === 'function'
        ? new MutationObserver(() => {
            clearTimeout(debounce);
            debounce = setTimeout(rasterize, 100);
        })
        : null;

    const prime = () => {
        rasterize();
        if (observer) observer.observe(element, { subtree: true, childList: true, attributes: true, characterData: true });
    };
    const clear = () => {
        clearTimeout(debounce);
        if (observer) observer.disconnect();
        element._kcapPrimeGen++;
        element._kcapChartPng = null;
    };
    element._kcapPrimeHandlers = { prime, clear };
    element.addEventListener('pointerenter', prime);
    element.addEventListener('focusin', prime);
    element.addEventListener('pointerleave', clear);
    element.addEventListener('focusout', clear);
};

window.kcapChartPrimeTeardown = (element) => {
    if (!element) return;
    const h = element._kcapPrimeHandlers;
    if (h) {
        h.clear();   // also disconnects the re-prime MutationObserver and cancels its debounce
        element.removeEventListener('pointerenter', h.prime);
        element.removeEventListener('focusin', h.prime);
        element.removeEventListener('pointerleave', h.clear);
        element.removeEventListener('focusout', h.clear);
        delete element._kcapPrimeHandlers;
    }
    element._kcapChartPng = null;
    element._kcapPrimeWired = false;
};

// Chart copy click. WebKit: use the primed PNG as an <img data:> (Google Docs embeds it) only if its
// fingerprint still matches the current chart; otherwise (touch/keyboard/not-primed, or a theme/resize
// change since priming) copy title+caption text — plain only, never an empty text/html flavor an
// HTML-preferring target could pick. Non-WebKit: the full image via clipboard.write.
function kcapChartCopyClick(element) {
    if (!element) return;
    if (!kcapIsWebKitClipboardDenied()) { kcapChartWriteImage(element); return; }
    const primed = element._kcapChartPng;
    const titleCaption = kcapChartTitleCaption(element);
    const current = kcapBuildChartRasterModel(element);
    if (primed && current && primed.fingerprint === current.fingerprint) {
        kcapExecCopyRich('<img src="' + primed.png + '">', titleCaption);
    } else {
        kcapCopyText(titleCaption);
    }
}

// Delegated, capture-phase listener so the copy runs inside the user gesture for every copy button
// (current and future) without per-element wiring. Guarded against double-registration. (Chart
// PRIMING is wired per-element by VisualRenderer — pointerenter doesn't bubble — but the CLICK is
// delegated here since clicks bubble.)
if (!window.__kcapCopyWired) {
    window.__kcapCopyWired = true;
    document.addEventListener('click', (e) => {
        const target = e.target;
        if (!target || !target.closest) return;

        const textBtn = target.closest('[data-kcap-copy]');
        if (textBtn) {
            // Server-composed rich payload (AI-1454 Overview/Evaluation copy buttons): its own clean
            // text/html on data-kcap-copy-html, written alongside the plain flavor via the synchronous
            // execCommand path — works in Safari too, which denies the async clipboard.write (AI-1487).
            const richHtml = textBtn.getAttribute('data-kcap-copy-html');
            if (richHtml) {
                kcapExecCopyRich(richHtml, textBtn.getAttribute('data-kcap-copy') || '');
                return;
            }
            // Analytics answer (.copyable-markdown inside .an-chat): sanitized clean HTML (Docs) +
            // aligned plain text (Slack), via the synchronous execCommand path — Safari denies the
            // async clipboard.write (AI-1455).
            const md = textBtn.closest('.copyable-markdown');
            if (md && md.closest('.an-chat')) {
                const html = kcapSanitizeToPortableHtml(md).innerHTML.trim();
                const plain = kcapDomToAlignedText(md);
                if (html) kcapExecCopyRich(html, plain);
                else kcapCopyText(plain || textBtn.getAttribute('data-kcap-copy') || '');
                return;
            }
            // Standalone analytics table: Razor-emitted, type-aware guarded + HTML-encoded flavors on
            // the .an-table wrapper (preserves the AI-956 numeric exemption) — read verbatim.
            const tableWrap = textBtn.closest('.an-table');
            if (tableWrap && tableWrap.hasAttribute('data-kcap-html')) {
                kcapExecCopyRich(tableWrap.getAttribute('data-kcap-html') || '',
                    tableWrap.getAttribute('data-kcap-plain') || '');
                return;
            }
            // Everything else (prompts, scalar chips, Markdown elsewhere): plain text (writeText works
            // in Safari).
            kcapCopyText(textBtn.getAttribute('data-kcap-copy') || '');
            return;
        }

        const imageBtn = target.closest('[data-kcap-copy-image]');
        if (imageBtn) {
            const container = imageBtn.closest('.an-chart') || imageBtn.parentElement;
            if (container) kcapChartCopyClick(container);
        }
    }, true);
}

// --- Custom MudMenu activators (analytics favourite-questions pill) --------------------------
//
// MudMenu wraps ActivatorContent in its own <div role="button" aria-haspopup="true" tabindex="0">
// styled `display: contents`. It contributes no box and no tab stop, but it IS exposed in the
// accessibility tree, so every custom activator is announced twice — a duplicate nested control for
// screen-reader and virtual-cursor users. No MudMenu parameter reaches that element (AriaLabel
// applies only when ActivatorContent is unset), so strip its control semantics after render and let
// the real <button> inside carry them. The wrapper's own handlers are keyboard/hover ones that a
// display:contents element can never receive focus for anyway.
window.kcapNormalizeMenuActivators = (root) => {
    if (!root) return 0;

    const wrappers = root.querySelectorAll('.mud-menu-activator');
    wrappers.forEach(w => {
        w.removeAttribute('role');
        w.removeAttribute('tabindex');
        w.removeAttribute('aria-haspopup');
        w.removeAttribute('aria-expanded');
        // Keep it out of the a11y tree entirely; the real button is its only meaningful content.
        w.setAttribute('data-kcap-normalized', '');
    });

    return wrappers.length;
};

// Focus `el` only if focus was dropped on the body (or lost outright) — never steal focus the user
// has placed somewhere real. Check and focus happen in ONE browser call: as two interop round trips
// they race a user who focuses something else in between, and the second call would then steal it.
// Used both after a pointer-open (WebKit does not focus a button on click, so Escape would otherwise
// have no target) and to recover focus a dismissed popover left stranded.
window.kcapFocusIfDropped = (elementId) => {
    const el = elementId ? document.getElementById(elementId) : null;
    if (!el) return false;

    const active = document.activeElement;
    if (active && active !== document.body) return false;

    el.focus();

    return document.activeElement === el;
};
