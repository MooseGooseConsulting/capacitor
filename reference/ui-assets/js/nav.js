// Replaces the current history entry's URL while PRESERVING history.state — Blazor's
// navigation runtime stores its history index / user state there, and replacing it with
// null would make the entry foreign to the router's back/forward (popstate) handling.
// Used by SessionsTab's tab-click URL sync (AI-1316), which deliberately avoids a Blazor
// navigation so tab clicks don't re-run the dashboard's parameter loads.
window.kcapNav = window.kcapNav || {};

window.kcapNav.replaceUrl = function (url) {
    history.replaceState(history.state, '', url);
};
