using System.Runtime.CompilerServices;

namespace Capacitor.Tests.Helpers.Guards;

/// <summary>
/// Forces every guard in this assembly to be initialised before TUnit discovers a single test.
///
/// <para>TUnit finds hooks in a REFERENCED assembly only once that assembly is loaded, and .NET
/// loads lazily — so a shared-hook library can silently contribute nothing. In practice TUnit does
/// load this one during discovery (this hook running at all is the proof), which is why the
/// <c>[BeforeEvery(Assembly)]</c> guards fire without help. This exists so that stays true by
/// construction instead of by TUnit's internal load timing.</para>
///
/// <para>What it actually buys: <c>TestDiscovery</c> precedes every test, so running the guards'
/// class constructors here pins <see cref="RepoPathStoreGlobalSetup.SharedConfigDir"/> and friends —
/// and with them this assembly's <c>[ModuleInitializer]</c> — ahead of the first test that could
/// read <c>PathHelpers.ConfigDir</c> and capture the developer's real <c>~/.config/kcap</c> into a
/// <c>static readonly</c> for the rest of the process.</para>
/// </summary>
public static class GuardAssemblyLoader {
    [Before(TestDiscovery)]
    public static void EnsureGuardsInitialised() {
        RuntimeHelpers.RunClassConstructor(typeof(RepoPathStoreGlobalSetup).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(DaemonPathsGlobalSetup).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(McpMarkerGlobalSetup).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(AuthProviderCacheGlobalSetup).TypeHandle);
    }
}
