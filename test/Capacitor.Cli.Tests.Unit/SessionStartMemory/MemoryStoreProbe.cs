using Capacitor.Cli.Core;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>
/// Reaches the SessionStart memory store the way production does, for tests that need to make its
/// construction fail or prove it never happened. Both facts are observable because
/// <see cref="SessionStartMemoryStorePaths.ValidateRoot"/> creates the directory eagerly.
/// </summary>
static class MemoryStoreProbe {
    /// <summary>Derived through production's own resolver, so a moved store root cannot leave these
    /// assertions passing against a path nothing writes to.</summary>
    static string RootOf(ConfigRoot config) => SessionStartMemoryStorePaths.DefaultRoot(config);

    /// <summary>Makes the next store construction throw: a file sits where the root's directory has
    /// to go, and no platform lets <c>CreateDirectory</c> past that.</summary>
    public static void Poison(ConfigRoot config) {
        var root = RootOf(config);
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        File.WriteAllText(root, "");
    }

    /// <summary>True once a store has been constructed against this root.</summary>
    public static bool WasBuilt(ConfigRoot config) => Directory.Exists(RootOf(config));

    static string NudgeRootOf(ConfigRoot config) => SessionStartMemoryStorePaths.NudgeGateRoot(config);

    /// <summary>Makes the next nudge claim fail, the same way <see cref="Poison"/> does.</summary>
    public static void PoisonNudgeGate(ConfigRoot config) {
        var root = NudgeRootOf(config);
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        File.WriteAllText(root, "");
    }

    /// <summary>The claim filenames the nudge gate has written, so a test can assert what the key is
    /// made of rather than only that gating happened.</summary>
    public static List<string> NudgeClaims(ConfigRoot config) =>
        Directory.Exists(NudgeRootOf(config))
            ? Directory.EnumerateFiles(NudgeRootOf(config)).Select(Path.GetFileName).OfType<string>().Order().ToList()
            : [];
}
