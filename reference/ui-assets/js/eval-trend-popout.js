// AI-1492 — document-level outside-dismiss for the pinned trend popout.
// The ONE JS-interop carve-out the spec allows. No backdrop: listeners never
// consume the event, so the outside click also activates its target.
(function () {
    const registrations = new Map(); // handleId -> { root, dotNetRef, listener }

    window.kcapTrendPopoutRegister = function (handleId, root, dotNetRef) {
        window.kcapTrendPopoutUnregister(handleId); // idempotent: replace
        const listener = function (e) {
            if (root.isConnected && root.contains(e.target)) return;
            dotNetRef.invokeMethodAsync('OnOutsidePointerDown').catch(function () {
                // Disposed circuit/component — defensively self-unregister so a
                // stale listener can never fire again or duplicate on remount.
                window.kcapTrendPopoutUnregister(handleId);
            });
        };
        document.addEventListener('pointerdown', listener, true);
        registrations.set(handleId, { root, dotNetRef, listener });
    };

    window.kcapTrendPopoutUnregister = function (handleId) {
        const reg = registrations.get(handleId);
        if (!reg) return;
        document.removeEventListener('pointerdown', reg.listener, true);
        registrations.delete(handleId);
    };
})();
