using System.Diagnostics;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>
/// The SessionStart nudges' own once-per-(harness, session) fence, independent of the
/// memory/guidelines lease.
///
/// <para>Nudges are composed at the output layer, after that lease's disposition is already decided,
/// so nothing about them can move a lease's acquire/complete/retry state. That isolation is also what
/// leaves them ungated: on a harness whose SessionStart callback fires every turn, an ungated nudge
/// rides every firing into the conversation — and a harness that injects it as a durable user message
/// accumulates one copy per turn, in the agent's context and in the recorded transcript alike. This
/// gate lives in its own directory with its own key space and keeps no lease state, so the isolation
/// holds in both directions.</para>
///
/// <para>The claim key is the harness token and the normalized session id, and nothing else — the same
/// derivation the lease key uses. Anything that varies between callbacks (Antigravity's payload carries
/// <c>invocationNum</c>) would mint a fresh claim per firing and gate nothing.</para>
/// </summary>
internal static class SessionStartNudgeGate {
    const string ClaimSuffix = ".claim";

    static readonly TimeSpan ReapBudget = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// The nudge for this firing: <paramref name="resolve"/>'s result on the first firing that has
    /// something to say, null on every later one. A harness whose callback cannot repeat is passed
    /// straight through and never touches this store.
    ///
    /// <para><paramref name="resolve"/> runs only after this firing has won the exclusive claim, so
    /// the config probes and the harness-offer ledger stamp behind it cost once per session rather
    /// than once per concurrent hook process. A claim that resolves to nothing is released, so a
    /// session that had nothing to say on its first firing can still speak on a later one. A crash
    /// or a throwing resolver after the claim is taken is fail-closed: the marker stays, and later
    /// firings stay silent.</para>
    ///
    /// <para>Every fault suppresses the nudge instead of emitting it. Both directions are silent to the
    /// hook; they differ in what a broken gate costs. One nudge too few loses standing guidance the
    /// next session restates anyway, while one too many is the defect this exists to stop.</para>
    /// </summary>
    public static string? Once(ConfigRoot config, SessionMemoryLifecycle lifecycle, Func<string?> resolve) {
        try {
            if (!lifecycle.CallbackMayRepeat) return resolve();

            var root  = SessionStartMemoryStorePaths.NudgeGateRoot(config);
            var claim = Path.Combine(root, SessionStartMemoryIdentity.Create(
                lifecycle.Harness, lifecycle.SessionId, lifecycleInstanceId: null) + ClaimSuffix);
            if (File.Exists(claim)) return null;

            SessionStartMemoryStorePaths.ValidateRoot(root);
            // CreateNew is the whole concurrency story: the winner of a race between two hook
            // processes gets the handle, everyone else gets an IOException and stays silent. The
            // exclusive claim is taken BEFORE resolve(), so only the winner stamps the offer ledger.
            using (new FileStream(claim, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

            var nudge = resolve();
            if (string.IsNullOrWhiteSpace(nudge)) {
                TryRelease(claim);
                return null;
            }

            Reap(root);
            return nudge;
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    static void TryRelease(string claim) {
        try {
            File.Delete(claim);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
    }

    /// <summary>Drops claims past the store's retention. Nothing else sweeps this directory, and this
    /// runs only after a fresh claim — at most once per session, never once per turn.</summary>
    static void Reap(string root) {
        try {
            var cutoff  = DateTime.UtcNow - SessionStartMemoryConstants.Retention;
            var started = Stopwatch.GetTimestamp();
            foreach (var file in Directory.EnumerateFiles(root, "*" + ClaimSuffix)) {
                if (Stopwatch.GetElapsedTime(started) >= ReapBudget) return;
                try {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
            }
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
    }
}
