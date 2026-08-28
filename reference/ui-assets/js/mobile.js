window.Capacitor = window.Capacitor || {};

window.Capacitor.isApplePlatform = function () {
    var s = (navigator.platform || navigator.userAgent || '');
    return /Mac|iPhone|iPad|iPod/.test(s);
};

// Responsive layout (the _isMobile signal) is driven by MudBlazor's
// IBrowserViewportService in MainLayout — the same service that drives MudDrawer —
// so there is no hand-rolled window.resize → .NET round-trip here anymore (AI-834).

// AI-868: logout must be a POST carrying an antiforgery token (a state-changing GET
// is forced-logout CSRF). MainLayout resolves the request token server-side and calls
// this to submit a one-shot hidden form, which leaves the interactive circuit and does
// a real HTTP POST so the server can clear the auth cookie and render the signed-out page.
window.Capacitor.submitLogout = function (action, fieldName, token) {
    var form = document.createElement('form');
    form.method = 'post';
    form.action = action;
    form.style.display = 'none';

    var input = document.createElement('input');
    input.type = 'hidden';
    input.name = fieldName;
    input.value = token;
    form.appendChild(input);

    document.body.appendChild(form);
    form.submit();
};

window.Capacitor.registerSearchHotkeys = function (dotNetRef) {
    if (window.Capacitor._searchHotkeyHandler) {
        document.removeEventListener('keydown', window.Capacitor._searchHotkeyHandler);
    }

    function isEditable(el) {
        if (!el) return false;
        var tag = el.tagName;
        return tag === 'INPUT' || tag === 'TEXTAREA' || el.isContentEditable;
    }

    function invokeOpenPalette() {
        return dotNetRef.invokeMethodAsync('OpenSearchPalette').catch(function () {
            // Circuit disconnected or .NET ref disposed — detach so we don't keep firing.
            if (window.Capacitor._searchHotkeyHandler) {
                document.removeEventListener('keydown', window.Capacitor._searchHotkeyHandler);
                window.Capacitor._searchHotkeyHandler = null;
            }
        });
    }

    window.Capacitor._searchHotkeyHandler = function (e) {
        var cmdOrCtrl = e.metaKey || e.ctrlKey;
        if (cmdOrCtrl && (e.key === 'k' || e.key === 'K')) {
            e.preventDefault();
            invokeOpenPalette();
            return;
        }
        if (e.key === '/' && !cmdOrCtrl && !isEditable(document.activeElement)) {
            e.preventDefault();
            invokeOpenPalette();
        }
    };

    document.addEventListener('keydown', window.Capacitor._searchHotkeyHandler);
};
