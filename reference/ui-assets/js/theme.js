// The document-global pieces of the light/dark theme that sit outside the MudBlazor palette: the
// data-kcap-theme attribute, the Prism stylesheets, and the Monaco editor theme.
//
// This does NOT decide the theme on load — the SSR response already carries it. Everything here runs after
// first paint: applying a change the user just made, the one-time localStorage migration, and answering for
// the MAUI hosts, which have no request to carry a cookie.
window.kcapTheme = window.kcapTheme || {};

// Same name the old localStorage key used, so the setting is recognisable as one thing across the cookie,
// the old key, and the server-side reader.
window.kcapTheme.LEGACY_STORAGE_KEY = 'kcap-theme';

window.kcapTheme.readCookie = function () {
    try {
        var match = document.cookie.match(/(?:^|;\s*)kcap-theme=(dark|light)(?:;|$)/);
        return match ? match[1] : null;
    } catch (e) {
        return null;
    }
};

window.kcapTheme.writeCookie = function (isDark) {
    try {
        // One year: a theme choice is not something to re-ask about, and there is deliberately no route
        // back to auto (see ThemeCookie) so nothing is gained by letting it lapse.
        var oneYear = 60 * 60 * 24 * 365;
        document.cookie = 'kcap-theme=' + (isDark ? 'dark' : 'light') +
            '; Path=/; Max-Age=' + oneYear + '; SameSite=Lax' +
            (location.protocol === 'https:' ? '; Secure' : '');
    } catch (e) { /* ignore — a failed write just means the choice is not remembered */ }
};

// The STORED choice: 'dark', 'light', or null for "never decided". Distinct from resolvedIsDark below,
// and the distinction is load-bearing — a caller that only gets a boolean cannot tell a real choice from
// the system's answer, which is how the MAUI hosts ended up treating a stored light preference as auto and
// painting light MudBlazor over dark register CSS.
window.kcapTheme.storedChoice = function () {
    return window.kcapTheme.readCookie();
};

// One-time move of a pre-cookie preference into the cookie. Returns the migrated mode, or null when there
// was nothing to migrate — including when a cookie already exists, since a live choice outranks a stale key.
//
// Presence of the old key IS an active choice: the toggle only ever wrote on click, and the loader defaulted
// to dark WITHOUT writing. So this cannot invent a preference for someone who never chose.
//
// The key is dropped only once the cookie is observably present — where cookies are blocked, removing it
// first would destroy the preference outright.
window.kcapTheme.migrateLegacyPreference = function () {
    try {
        var stored = localStorage.getItem(window.kcapTheme.LEGACY_STORAGE_KEY);
        if (stored !== 'dark' && stored !== 'light') return null;

        if (window.kcapTheme.readCookie()) {
            localStorage.removeItem(window.kcapTheme.LEGACY_STORAGE_KEY);

            return null;
        }

        window.kcapTheme.writeCookie(stored === 'dark');

        if (window.kcapTheme.readCookie()) localStorage.removeItem(window.kcapTheme.LEGACY_STORAGE_KEY);

        return stored === 'dark';
    } catch (e) {
        return null;
    }
};

// The resolved mode for callers that need a boolean rather than CSS — Monaco's initial theme, and the
// layouts once interop is available.
//
// Reads the cookie first, then falls back to the system. It deliberately does NOT read data-kcap-theme:
// under auto that attribute is ABSENT on purpose so CSS can resolve the system preference, which means the
// attribute cannot answer this question in the one case where the answer is not already known server-side.
window.kcapTheme.resolvedIsDark = function () {
    var chosen = window.kcapTheme.readCookie();
    if (chosen) return chosen === 'dark';

    return window.kcapTheme.systemIsDark();
};

window.kcapTheme.systemIsDark = function () {
    try {
        return window.matchMedia('(prefers-color-scheme: dark)').matches;
    } catch (e) {
        return true; // the app's historical default, and the SSR fallback for Mud's C#-side palette
    }
};

// Monaco only. Split out because it is the one theme consumer with no declarative form — setTheme is an
// API, not a stylesheet — so it is the only thing the auto path still has to drive by hand.
window.kcapTheme.applyMonaco = function (isDark) {
    try {
        // Global across every editor instance on the page. Editors created later pick up their initial
        // theme from ConstructionOptions; this keeps live ones in sync.
        if (window.monaco && window.monaco.editor) {
            window.monaco.editor.setTheme(isDark ? 'vs-dark' : 'vs');
        }
    } catch (e) { /* ignore */ }
};

// Applies an EXPLICIT choice — i.e. one the user just made, or one already in the cookie.
//
// Never call this while following the system: stamping data-kcap-theme is precisely what stops
// `:root:not([data-kcap-theme])` matching, so it would disable the CSS that makes auto work AND freeze a
// snapshot that ignores every later OS change. The auto path is initAuto below.
window.kcapTheme.apply = function (isDark) {
    try {
        document.documentElement.setAttribute('data-kcap-theme', isDark ? 'dark' : 'light');
    } catch (e) { /* ignore */ }

    window.kcapTheme.applyPrism(isDark ? 'all' : 'not all', isDark ? 'not all' : 'all');
    window.kcapTheme.applyMonaco(isDark);
};

// Prism: flip which of the two stylesheets applies. Neither is re-fetched, so the switch is instant — the
// previous version swapped one link's href and re-downloaded from a CDN, leaving code blocks on the wrong
// syntax theme until it arrived.
window.kcapTheme.applyPrism = function (darkMedia, lightMedia) {
    try {
        var dark = document.getElementById('prism-theme-dark');
        var light = document.getElementById('prism-theme-light');
        if (dark) dark.media = darkMedia;
        if (light) light.media = lightMedia;
    } catch (e) { /* ignore */ }
};

// The auto path: everything CSS cannot cover on its own.
//
// The attribute stays absent and the Prism links keep their media queries, so both follow the system with
// no help. Only Monaco needs driving, and it needs to keep following — hence the listener, registered once
// per document.
//
// A choice made in ANOTHER tab is not picked up until this one reloads. That is deliberate: this tab is
// still rendering auto in every other respect, so following the system remains internally consistent,
// whereas pinning the document from JS would leave MudBlazor's C#-side palette behind on the old mode.
window.kcapTheme.initAuto = function () {
    window.kcapTheme.applyMonaco(window.kcapTheme.resolvedIsDark());

    if (window.kcapTheme._watchingSystem) return;

    try {
        window.matchMedia('(prefers-color-scheme: dark)')
            .addEventListener('change', function (e) {
                // Gated on THIS document being unstamped, not on the cookie: after a toggle here the
                // attribute is set and the choice must hold, but a cookie written in another tab leaves this
                // one on auto, where Monaco has to keep following the system like everything else.
                if (document.documentElement.hasAttribute('data-kcap-theme')) return;

                window.kcapTheme.applyMonaco(e.matches);
            });

        window.kcapTheme._watchingSystem = true;
    } catch (e) { /* ignore — Monaco simply keeps its construction theme */ }
};
