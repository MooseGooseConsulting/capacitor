(function (root) {
    var TAB_KEY   = 'kapacitor.dashboard.lastTab.v1';
    var REPO_KEY  = 'kapacitor.dashboard.lastRepoHash.v1';
    var OWNER_KEY = 'kapacitor.dashboard.lastOwner.v1';
    // 'analytics' is the legacy saved value for the Insights tab (renamed 2026-08); both
    // spellings restore to the canonical /insights route.
    var TABS      = ['agents', 'sessions', 'insights', 'analytics', 'facts', 'curation', 'knowledge', 'flows', 'home'];
    // Repo hashes are 16-char lowercase hex (see Capacitor.RepoHashHelper). Reject any
    // other shape so a bogus saved value (or a URL like /repo/sessions) can't trigger
    // a redirect.
    var REPO_HASH = /^[0-9a-f]{16}$/;
    var REPO_ROOT = /^\/repo\/([0-9a-f]{16})\/?$/;

    function buildTargetUrl(tab, repo) {
        switch (tab) {
            case 'agents':    return '/repo/' + repo + '/agents';
            case 'sessions':  return '/repo/' + repo;
            case 'insights':
            case 'analytics': return '/repo/' + repo + '/insights';
            case 'facts':     return '/repo/' + repo + '/facts';
            case 'curation':  return '/repo/' + repo + '/curation';
            case 'knowledge': return '/repo/' + repo + '/knowledge';   // AI-1920
            case 'flows':     return '/repo/' + repo + '/flows';
            // AI-1972 — Home is org-only: it has no repo-scoped form, so a saved "home" tab is
            // never restored into a repo context. It collapses to the repo's Sessions default
            // (same URL as `default` below), which equals the "/repo/{hash}" entry path and so
            // yields no redirect (computeTarget returns null when target === current path).
            case 'home':      return '/repo/' + repo;
            default:          return '/repo/' + repo;
        }
    }

    // AI-1317: the root "/" is the Home landing page now — this script no longer restores
    // a saved tab there (redirecting a "/" load away from Home would contradict the
    // acceptance criterion that "/" renders Home). "/repo/{hash}" is unaffected — it keeps
    // meaning that repo's Sessions by default, and restoring the repo-scoped tab the user
    // was last on is still correct, so that behavior is retained.
    //
    // AI-1972: Home is now org-only — it has no repo-scoped form. A saved "home" tab landing
    // on "/repo/{hash}" therefore collapses to that repo's Sessions default (see
    // buildTargetUrl's 'home' arm), i.e. it stays on the repo the user opened rather than
    // restoring a retired "/repo/{hash}/home" route. `homeCapable` still distinguishes the
    // 'home' tab value on a non-capable host (MAUI, which loads this same script but never
    // sets `window.__kapacitorHomeCapable`) — the two branches converge on the repo's
    // Sessions default either way, but the global remains a real host-capability signal
    // asserted elsewhere. Defaults to `true` when omitted so existing call sites (incl. this
    // file's own tests) are unaffected.
    function computeTarget(path, savedTab, savedRepo, homeCapable) {
        if (!savedTab || TABS.indexOf(savedTab) === -1) return null;
        if (homeCapable == null) homeCapable = true;

        // Facts/Curation require a repo. Fall back to the default tab (sessions) so a
        // stale facts/curation entry without a usable saved repo doesn't force a redirect.
        var tab = savedTab;
        var repo = savedRepo && REPO_HASH.test(savedRepo) ? savedRepo : null;
        if ((tab === 'facts' || tab === 'curation') && !repo) tab = 'sessions';
        if (tab === 'home' && !homeCapable) tab = 'sessions';

        var p = (path || '/').toLowerCase();
        var m = p.match(REPO_ROOT);

        if (!m) return null;

        var target = buildTargetUrl(tab, m[1]);  // URL repo wins

        if (target === p) return null;  // Already at target.
        return target;
    }

    root.__kapacitorDashboardRestoreFor = computeTarget;

    // AI-835: merge the saved owner filter into the query string. An owner already
    // present in the URL wins (explicit link beats restored preference).
    function mergeOwnerIntoSearch(search, owner) {
        if (!owner || /[?&]owner=/.test(search)) return search;
        return (search ? search + '&' : '?') + 'owner=' + encodeURIComponent(owner);
    }

    root.__kapacitorDashboardMergeOwner = mergeOwnerIntoSearch;

    // Browser-only redirect side effect.
    if (typeof window === 'undefined' || typeof localStorage === 'undefined') return;
    try {
        var tab         = localStorage.getItem(TAB_KEY);
        var repo        = localStorage.getItem(REPO_KEY);
        var owner       = localStorage.getItem(OWNER_KEY);
        var homeCapable = !!window.__kapacitorHomeCapable;
        var target      = computeTarget(window.location.pathname, tab, repo, homeCapable);

        // AI-835/AI-1317: owner restore applies only on the same repo-scoped entry paths
        // tab restore does — never on "/", which stays untouched (no redirect at all) so
        // Home always renders on load.
        var p          = (window.location.pathname || '/').toLowerCase();
        var restorable = REPO_ROOT.test(p);
        var search     = restorable ? mergeOwnerIntoSearch(window.location.search, owner) : window.location.search;

        if (target == null && search !== window.location.search) target = window.location.pathname;
        if (target) window.location.replace(target + search + window.location.hash);
    } catch (e) {
        // Private browsing / SecurityError / parse error — fall through.
    }
})(typeof globalThis !== 'undefined' ? globalThis : this);
