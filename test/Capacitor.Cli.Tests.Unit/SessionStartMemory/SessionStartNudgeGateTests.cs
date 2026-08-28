using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>
/// The nudge gate's contract: on a harness whose SessionStart callback fires every turn, the nudges
/// composed at the output layer are emitted once per conversation and never again — without touching
/// the memory/guidelines lease, and without affecting a harness whose callback cannot repeat.
/// </summary>
public class SessionStartNudgeGateTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Nudge = "## Work items\nnudge body";

    // A real GUID: Antigravity is on the fail-closed identity arm, so a non-GUID id normalizes to
    // null and would suppress before the claim is ever consulted, passing these tests vacuously.
    const string ConversationId = "e80c33bfc10f4d2fb626b0043f488fc0";

    static SessionMemoryLifecycle NonRepeating(string sessionId) =>
        GeminiHookCommand.LifecycleFor(sessionId, "startup");

    [Test]
    public async Task A_repeating_callback_emits_the_nudge_once_then_never_again() {
        var resolved = 0;
        string? Resolve() { resolved++; return Nudge; }

        var lifecycle = AntigravityHookCommand.LifecycleFor(ConversationId);
        var first  = SessionStartNudgeGate.Once(Config.Root, lifecycle, Resolve);
        var second = SessionStartNudgeGate.Once(Config.Root, lifecycle, Resolve);
        var third  = SessionStartNudgeGate.Once(Config.Root, lifecycle, Resolve);

        await Assert.That(first).IsEqualTo(Nudge);
        await Assert.That(second).IsNull();
        await Assert.That(third).IsNull();

        // Not merely "no output": the resolvers behind this probe the harness config and stamp the
        // harness-offer ledger, so a later firing must not run them at all.
        await Assert.That(resolved).IsEqualTo(1);
    }

    /// <summary>The gate takes a lifecycle and nothing else, and the lifecycle each repeating harness
    /// builds is a function of its session id alone — so no per-callback field (Antigravity's payload
    /// carries <c>invocationNum</c>) can reach the claim key and mint a fresh claim per firing.</summary>
    [Test]
    public async Task The_claim_is_keyed_on_the_harness_and_the_session_id_alone() {
        SessionStartNudgeGate.Once(Config.Root, AntigravityHookCommand.LifecycleFor(ConversationId), () => Nudge);

        var expected = SessionStartMemoryIdentity.Create(
            SessionStartHarness.Antigravity, ConversationId, lifecycleInstanceId: null) + ".claim";
        await Assert.That(MemoryStoreProbe.NudgeClaims(Config.Root)).IsEquivalentTo([expected]);
    }

    [Test]
    public async Task Two_harnesses_sharing_a_session_id_claim_separately() {
        var antigravity = SessionStartNudgeGate.Once(
            Config.Root, AntigravityHookCommand.LifecycleFor(ConversationId), () => Nudge);
        var kiro = SessionStartNudgeGate.Once(
            Config.Root, KiroHookCommand.LifecycleFor(ConversationId), () => Nudge);

        await Assert.That(antigravity).IsEqualTo(Nudge);
        await Assert.That(kiro).IsEqualTo(Nudge);
        await Assert.That(MemoryStoreProbe.NudgeClaims(Config.Root).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Each_conversation_gets_its_own_first_emission() {
        const string other = "3f2504e04f8911d39a0c0305e82c3301";

        await Assert.That(SessionStartNudgeGate.Once(
            Config.Root, AntigravityHookCommand.LifecycleFor(ConversationId), () => Nudge)).IsEqualTo(Nudge);
        await Assert.That(SessionStartNudgeGate.Once(
            Config.Root, AntigravityHookCommand.LifecycleFor(other), () => Nudge)).IsEqualTo(Nudge);
        await Assert.That(SessionStartNudgeGate.Once(
            Config.Root, AntigravityHookCommand.LifecycleFor(other), () => Nudge)).IsNull();
    }

    /// <summary>A harness whose callback fires once per session already emits once; gating it would add
    /// a store write and a failure mode for nothing, and could only ever take an emission away.</summary>
    [Test]
    public async Task A_non_repeating_harness_emits_every_firing_and_writes_no_claim() {
        for (var i = 0; i < 3; i++)
            await Assert.That(SessionStartNudgeGate.Once(Config.Root, NonRepeating(ConversationId), () => Nudge))
                .IsEqualTo(Nudge);

        await Assert.That(MemoryStoreProbe.NudgeClaims(Config.Root)).IsEmpty();
    }

    /// <summary>The claim is spent on an emission, not on a firing — the harness nudge can be absent on
    /// the first turn (nothing detected, or its own throttle held) and present on a later one.</summary>
    [Test]
    public async Task A_firing_with_nothing_to_say_leaves_the_claim_open() {
        var lifecycle = AntigravityHookCommand.LifecycleFor(ConversationId);

        await Assert.That(SessionStartNudgeGate.Once(Config.Root, lifecycle, () => null)).IsNull();
        await Assert.That(SessionStartNudgeGate.Once(Config.Root, lifecycle, () => "   ")).IsNull();
        await Assert.That(MemoryStoreProbe.NudgeClaims(Config.Root)).IsEmpty();

        await Assert.That(SessionStartNudgeGate.Once(Config.Root, lifecycle, () => Nudge)).IsEqualTo(Nudge);
        await Assert.That(SessionStartNudgeGate.Once(Config.Root, lifecycle, () => Nudge)).IsNull();
    }

    /// <summary>A store that cannot be written must suppress. Both directions are silent to the hook;
    /// a nudge withheld is guidance the next session restates, a nudge repeated is the defect.</summary>
    [Test]
    public async Task An_unwritable_store_suppresses_rather_than_emitting() {
        MemoryStoreProbe.PoisonNudgeGate(Config.Root);

        await Assert.That(SessionStartNudgeGate.Once(
            Config.Root, AntigravityHookCommand.LifecycleFor(ConversationId), () => Nudge)).IsNull();
    }

    [Test]
    public async Task A_session_id_with_no_stable_identity_suppresses() {
        // Antigravity's normalizer rejects a non-GUID id; with no key there is nothing to fence.
        await Assert.That(SessionStartNudgeGate.Once(
            Config.Root, AntigravityHookCommand.LifecycleFor("not-a-guid"), () => Nudge)).IsNull();
    }

    [Test]
    public async Task A_throwing_resolver_suppresses_rather_than_faulting_the_hook() {
        await Assert.That(SessionStartNudgeGate.Once(
            Config.Root, AntigravityHookCommand.LifecycleFor(ConversationId),
            () => throw new InvalidOperationException("resolver"))).IsNull();

        // Fail-closed: the claim is already taken, so a later firing stays silent.
        await Assert.That(SessionStartNudgeGate.Once(
            Config.Root, AntigravityHookCommand.LifecycleFor(ConversationId), () => Nudge)).IsNull();
        await Assert.That(MemoryStoreProbe.NudgeClaims(Config.Root)).IsNotEmpty();
    }

    /// <summary>Concurrent hook processes fire as separate short-lived processes, so the claim has to be
    /// won by exactly one of them — the marker is created exclusively, not checked then written.</summary>
    [Test]
    public async Task Concurrent_firings_yield_exactly_one_emission() {
        var lifecycle = AntigravityHookCommand.LifecycleFor(ConversationId);
        var start = new TaskCompletionSource();

        var firings = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => {
            await start.Task;
            return SessionStartNudgeGate.Once(Config.Root, lifecycle, () => Nudge);
        })).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(firings);

        await Assert.That(results.Count(r => r == Nudge)).IsEqualTo(1);
    }

    /// <summary>The claim outlives the process, so a second hook process must see the first one's
    /// decision — a lifecycle rebuilt from scratch, as every firing does, resolves the same claim.</summary>
    [Test]
    public async Task A_claim_survives_into_a_freshly_built_lifecycle() {
        SessionStartNudgeGate.Once(Config.Root, AntigravityHookCommand.LifecycleFor(ConversationId), () => Nudge);

        await Assert.That(SessionStartNudgeGate.Once(
            Config.Root, AntigravityHookCommand.LifecycleFor(ConversationId), () => Nudge)).IsNull();
    }

    /// <summary>Pi's lifecycle carries the session FILE, not the dashless id its nudge text renders, and
    /// the reason varies across firings for one file — neither may split the claim.</summary>
    [Test]
    public async Task Pi_claims_per_session_file_regardless_of_the_reported_reason() {
        var file = Config.PathTo("sessions", "20260812_e80c33bf-c10f-4d2f-b626-b0043f488fc0.jsonl");

        await Assert.That(SessionStartNudgeGate.Once(
            Config.Root, PiHookCommand.LifecycleFor(file, "startup"), () => Nudge)).IsEqualTo(Nudge);
        await Assert.That(SessionStartNudgeGate.Once(
            Config.Root, PiHookCommand.LifecycleFor(file, "resume"), () => Nudge)).IsNull();
    }

    [Test]
    public async Task OpenCode_claims_per_session_across_restarts() {
        const string sessionId = "ses023575b3cffetNkaAklu6CAtNp";

        await Assert.That(SessionStartNudgeGate.Once(
            Config.Root, OpenCodeHookCommand.LifecycleFor(sessionId), () => Nudge)).IsEqualTo(Nudge);
        await Assert.That(SessionStartNudgeGate.Once(
            Config.Root, OpenCodeHookCommand.LifecycleFor(sessionId), () => Nudge)).IsNull();
    }

    /// <summary>The gate keeps its own directory: a claim must never be counted against the lease
    /// store's entry caps, nor be left behind by a sweep that admits only record and temp names.</summary>
    [Test]
    public async Task Claims_live_outside_the_lease_store_root() {
        SessionStartNudgeGate.Once(Config.Root, AntigravityHookCommand.LifecycleFor(ConversationId), () => Nudge);

        await Assert.That(SessionStartMemoryStorePaths.NudgeGateRoot(Config.Root))
            .IsNotEqualTo(SessionStartMemoryStorePaths.DefaultRoot(Config.Root));
        await Assert.That(MemoryStoreProbe.WasBuilt(Config.Root)).IsFalse();
    }
}
