# Repeating callbacks — work that should happen once, happening every turn

Some harness lifecycle hooks fire once per conversation; others fire once per turn. The
repeating ones declare `CallbackMayRepeat: true`, and the `SessionStartMemory` lease is the
one gate in the client that knows the difference — it makes the team-memory and guidelines
fetch once-per-conversation no matter how often the callback runs. Everything else on those
code paths is ungated. The defect class is therefore: **work that is correct exactly once per
conversation, reached from a code path that runs once per turn, with no per-conversation
gate.** It has three costs, and a finding usually has more than one: agent context is
polluted with duplicate injected text, the turn's critical path pays a latency it should have
paid once, and the server receives N copies of an event that describes one thing.

Evidence for the class, measured on this machine: Antigravity conversation
`f207a9b5-11ae-43ac-b436-b74a1ad5901b` is 490 steps long, of which **121 are
`SYSTEM_SDK`/`USER_INPUT` steps whose entire content is the work-items nudge** — a durable
fake user message, injected once per turn, ~1.1 KB each. That is 155 KB of a 679 KB
conversation, **23% of everything the model was shown**, spent restating the same paragraph.
The same transcript contains zero repeats of the memory index itself, which is the clean
confirmation that the lease works and that everything composed outside it does not.

---

## Findings

The four repeating hook commands are under concurrent edit, so findings in them are cited by
member rather than by line; everything else carries a line number.

| # | finding | where | harnesses | status | severity | cost to fix |
|---|---|---|---|---|---|---|
| 1 | Only two of the four "repeating" harnesses repeat per turn; two doc comments say otherwise | `OpenCodeHookCommand` class doc, `OpenCodeExtensionInstaller.cs:169`, `PiExtensionInstaller.cs:74` | all four | verified | informational | two comments |
| 2 | `gh pr view` — a live GitHub API call — spawned on every turn, unbudgeted | `RepositoryDetection.cs:212`, called from `AntigravityHookCommand.HandleSessionStart` / `KiroHookCommand.HandleAgentSpawn` | Antigravity, Kiro | verified | high | two call sites, two arguments each |
| 3 | Whole-machine harness inventory probed and serialized into every turn's POST | `SessionStartInventory.cs:15`, `HarnessInventory.cs:43` | all four (per-turn on two) | verified | medium | one gate, or a TTL cache |
| 4 | Kiro's model sidecar read with a write-denying open, every prompt | `KiroHookCommand.ReadKiroModel` | Kiro | verified | high on Windows | one line |
| 5 | `repository` enrichment gated per-cwd, never cleared except by Claude | `RepositoryDetection.cs:353-397`, `ClaudeHookCommand.cs:353` | all eight non-Claude | verified | medium | one call, once per conversation |
| 6 | Harness-setup nudge throttled on wall clock, not per conversation | `HarnessNudgeEmitter.CheckThrottle`, `HarnessNudge.cs:13` | all four | verified | low | a gate, or accept it |
| 7 | Outage spool retains and later replays one session-start per turn | `HookSpool.cs:53`, `HookSpool.cs:109` | all four | verified | medium | server-side |
| 8 | No client-side idempotency key exists for any lifecycle POST | absence across `src/Capacitor.Cli/Commands/Harness/` | all | verified | high, as a requirement | server-side, or a new field |

---

### 1. Two of the four repeat per turn. The other two do not.

`CallbackMayRepeat: true` is correct on all four — it protects a restart or a resume, which
genuinely re-fires for the same session id. But the *frequency* differs by two orders of
magnitude, and the comments do not say so.

Antigravity's `PreInvocation` fires on every invocation (its payload carries `invocationNum`)
and Kiro's `agentSpawn` fires on every prompt. Both genuinely run the whole dispatcher per
turn.

OpenCode and Pi do not. The OpenCode plugin's `start()` returns immediately when a
module-level `started` Set already holds the session id (`OpenCodeExtensionInstaller.cs:169`),
and `session.idle` reaches `ensureStarted` only when that Set does not; the cold-start heal
path in `chat.system.transform` is capped at `MEMORY_COLD_START_ATTEMPTS = 3`. So
`kcap hook --opencode --event session-start` runs at most four times per OpenCode process per
session, not once per idle — which is what the `OpenCodeHookCommand` class doc claims
("re-fires session-start cheaply on each session.idle"). Pi's extension binds
`kcap hook --pi --event session-start` to `pi.on("session_start")` only; the per-turn work is
`before_agent_start`, which re-appends a *cached* fragment to a system prompt Pi rebuilds each
turn, guarded by an `includes()` check.

This matters for prioritisation, not for correctness: every per-turn cost below is a cost on
Antigravity and Kiro, and a once-per-process cost on OpenCode and Pi.

### 2. A GitHub round-trip on the blocking turn path

All four dispatchers call `RepositoryDetection.EnrichWithRepositoryInfo(config, body)` — the
two-argument overload, which means **no budget and `detectPullRequest: true`**. Inside, the
cached path still spawns `git branch --show-current` fresh every call (branch is deliberately
never cached), then, when a host is known, `GitProviderRouter.ResolveAsync` and
`GitHubPrDetector.DetectAsync` — `gh pr view --json number,title,url,headRefName`, a real
network call to GitHub. The router's memo is a process static, and every hook is a fresh AOT
process, so on a GitHub Enterprise host the `gh auth status` probe spawns per turn too.

`ClaudeHookCommand` — the harness that fires *once* per session — is the one that refuses
this. It passes `budget.Remaining` and `detectPullRequest: false`, and says why at
`ClaudeHookCommand.cs:368`: "a live `gh pr view` / `glab` round-trip (~600ms to GitHub) is the
single biggest client cost on the hook path". The careful call site is on the cheap path and
the expensive call sites are on the per-turn path.

Two consequences. The latency one: Antigravity's `PreInvocation` blocks the turn, and this
enrichment is awaited before the memory fetch is even started. The git cap is 5s and the
provider cap 2s when no budget is passed, so a slow repo can spend 7s inside a hook whose
declared ceiling is 5s — the vendor kills it, and that firing's injection is lost. The volume
one: 121 turns is 121 `gh` process spawns and 121 authenticated GitHub API calls for one
conversation, against a per-hour rate limit shared with everything else the user runs.

**Minimum fix.** Give the two per-turn call sites the same arguments Claude uses:
`budget.Remaining` and `detectPullRequest: false`. The watcher runs its own
`DetectRepositoryAsync` with PR detection and backfills independently, so nothing is lost —
that is already the stated reason Claude can do it.

### 3. The machine inventory, re-probed and re-sent every turn

`SessionStartInventory.Stamp` runs `HarnessInventory.EvaluateCurrent`, which is
`AgentDetection.Detect` plus `HarnessIntegrationProbe.IsWired` for all nine vendors plus a
ledger load plus a `machine.json` read. `Detect` probes eleven binary names against PATH; on
Windows each probe is `|PATH| x |PATHEXT|` `File.Exists` calls, so a 40-entry PATH is roughly
1,700 stat calls per turn. `IsWired` opens and parses each vendor's config file.

The result is then serialized into the POST body, so the server receives the same inventory
121 times for one conversation. This is machine state, not session state, and it does not
change between two turns of one conversation.

**Minimum fix.** Stamp it only when the lifecycle is not a repeat — the same per-conversation
gate the nudge fix introduces. A short-TTL cache under the config root is the alternative and
would also help the eight non-repeating harnesses, but it is more machinery than the problem
needs.

### 4. Kiro's sidecar read locks Kiro out of its own file

`KiroHookCommand.ReadKiroModel` does `JsonNode.Parse(File.ReadAllText(path))` on
`KiroPaths.SessionJson(dashedSessionId)` — the `{id}.json` sidecar Kiro writes alongside its
transcript. `File.ReadAllText` opens `FileShare.Read`, which on Windows denies Write to every
other handle for the duration of the read. `CLAUDE.md` names this file class and this hazard
explicitly, and `WatchCommand` reads the *same file* correctly at two sites
(`WatchCommand.cs:2849` and `2886`), both carrying the comment "never lock Kiro out of its own
sidecar".

Once per session this is a narrow race. On `agentSpawn` it is a race per prompt, taken at
exactly the moment Kiro is most likely to be writing that sidecar. It is invisible on
macOS and Linux, which have no mandatory sharing.

**Minimum fix.** `WatchCommand.ReadAllTextShared(path)` in place of `File.ReadAllText(path)`.

### 5. Repository enrichment is gated on the wrong key

`EnrichWithRepositoryInfo` compares the freshly detected payload against a last-emitted record
keyed on `SHA256(cwd)` (`RepositoryDetection.cs:353`) and, when they match, returns the body
**with no `repository` node at all**. `ClearLastEmitted` exists to defeat that, and its own
doc comment says it "must be called on session-start — each session needs its own
RepositoryDetected event". It has exactly one caller: `ClaudeHookCommand.cs:353`.

So on the other eight harnesses, the first conversation started in a working directory ships
a `repository` node and every later conversation in that same directory ships none, until the
branch or PR changes. On the two per-turn harnesses the within-conversation behaviour is
actually correct by accident — turn 1 emits, turns 2..N do not, which is what you want — but
the cross-conversation hole is real and it is a hole in the corpus the server is being built
to hold.

This is the same shape as the nudge: a gate whose key is not the unit of work.

**Minimum fix.** Call `ClearLastEmitted` once per conversation, from wherever the
per-conversation gate ends up living.

### 6. A 6-hour claim and a 7-day floor, inside a conversation that may outlive both

`HarnessNudgeEmitter.ResolveFragmentForHook` is composed at the output layer alongside the
work-items nudge, so on Antigravity it lands in the same durable `USER_INPUT` channel. Its
gates are `HarnessOfferStore.TryClaimCheck` (`HarnessOfferStore.cs:99`) — a 6-hour cross-process claim on
the *evaluation* — and `HarnessNudge.ReofferFloor` (`HarnessNudge.cs:13`), 7 days per vendor on the *emission*. Both
are wall-clock. A conversation open for a week re-injects the same nudge; and every 6 hours a
turn boundary pays for the full nine-vendor detection sweep even when nothing is nudgeable.

Worth stating because it is the obvious thing to suspect: the hook path's zero-wait
`StampOffered(..., TimeSpan.Zero)` **does** persist. `ConfigFileLock.Acquire` is a named
mutex, and an uncontended `WaitOne(TimeSpan.Zero)` returns true, so the 7-day floor is
genuinely recorded from a hook. Had it not been, the nudge would repeat every 6 hours forever.

**Minimum fix.** Low priority. Either fold it behind the same per-conversation gate, or accept
twice-a-week as the ceiling and leave it.

### 7. The spool holds one copy per turn, and replays every one

`HookSpool.Append` writes a line per firing. Disk is bounded: `EnsureUnderCap`
(`HookSpool.cs:109`) holds each session's file to 1 MB, evicting oldest-first. So a 200-turn
conversation against an unreachable server leaves at most 1 MB on disk for that session, not
an unbounded file — that part is fine and does not need a cap added.

What is not bounded is the replay. `DrainFileAsync` posts every retained line, so recovery
turns one conversation's outage into several hundred `session-start` POSTs for a single
session, delivered across many 1.5-second drain passes. On these four harnesses the spool
holds nothing but session-start for the session, so every one of those POSTs describes the
same event.

The 30-second cross-process drain throttle (`AgentHookPoster.TryClaimDrainAttempt`) already
exists and its doc names Kiro and OpenCode's per-prompt firing as the reason. It throttles
drain *attempts*, not the number of spooled duplicates.

**Minimum fix.** Client-side, none is required — the fix belongs on the server, below. If the
per-conversation gate ends up suppressing the repeat POST entirely, this disappears with it.

### 8. "The server's deterministic lifecycle id collapses the repeats" is an assumption

Six comments across the harness dispatchers assert this, and it is the stated reason the
per-turn POST is considered harmless. **Nothing in the client backs it.** There is no
idempotency key, no canonical event id, and no `If-None-Match`-style field on any lifecycle
POST. The client demonstrably knows the pattern — `CursorHookCommand.cs:265` computes a
`canonical_event_id` from `(sid, gen, text)` for thoughts, and `FeedbackSubmission` carries an
explicit idempotency key — lifecycle simply does not use it.

Worse, the repeats are not byte-identical. Antigravity and OpenCode both stamp
`["started_at"] = DateTimeOffset.UtcNow.ToString("O")` on every firing, so every duplicate
carries a later start time than the last. Kiro sends no `started_at`; Pi sends the session
header's timestamp, which is stable. And per finding 5, the first firing for a cwd carries a
`repository` node the later ones do not.

This is not a client defect. It is a requirement we inherited without noticing we had.

---

## Checked, and fine

Things a reader would otherwise wonder about.

**The lease itself holds.** `SessionStartMemoryLeaseStore` is the one gate that works, and the
transcript measurement confirms it end to end: 121 nudge injections, zero memory-index
repeats. On this machine the store holds 49 records — 26 `completed`/`ready`, 5
`complete_without_context`, 2 `retry_pending`, 14 `leased` and never completed. Those 14 are
hooks killed at the vendor timeout between acquire and complete; a `leased` record whose
`lease_expires_at` has passed is re-acquirable, so they retry on a later turn, which is the
correct outcome for a firing that injected nothing.

**No lease key carries a per-callback field.** `LifecycleInstanceId` is null on all four, so
the key is `SHA256(harness_token, normalized_session_id)` and nothing else.
`invocationNum` never reaches it. Pi's key is the canonicalized session *file path*
(`PiSessionPathCanonicalizer`), not its `--reason`, so a resume dedupes and a fork does not.
The Antigravity and Kiro normalizers collapse GUID spellings, so two spellings across firings
cannot mint two leases.

**The spool drain is already throttled.** 30 seconds, cross-process, on-disk stamp, auth-gated,
1.5-second budget.

**No update or npm check runs per turn.** `hook` is in `CrashReporter.FailOpenCommands`, which
is the suppression predicate for both `UpdateNotice.FlushAsync` and
`HarnessSetupNotice.FlushAsync` in `Program.cs`'s exit `finally`. Neither touches disk or
network on this path.

**`/auth/config` is not re-fetched per turn.** `AuthProviderCache` is a 24-hour on-disk cache,
read before the in-process static that a fresh hook process can never benefit from.

**`EnsureWatcherRunning` is genuinely a no-op once live.** The fast path is a pid check plus
two heartbeat file reads; no lock, no spawn.

**`GitRepository.FindRoot` spawns nothing.** It walks parents looking for `.git`.

**The ppid walk rarely runs, and never on Windows.** `AntigravityHookCommand.AgentWorkspaceCwd`
is reached only when the payload carries no `workspacePaths` — print mode. Separately,
`ProcessHelpers.GetProcessCwd` returns null on anything that is not macOS or Linux, so on
Windows the fallback is inert whether or not it is reached. Each hop is an `OpenProcess` plus
`NtQueryInformationProcess`, not a process-table enumeration.

**The output adapter composes exactly two things outside the lease** — the work-items nudge and
the harness nudge, both through `HarnessNudgeEmitter.Combine` into
`SessionStartMemoryOutputAdapters.Render`. There is no third.

---

## Requirements this places on the server we are building

**Lifecycle ingest must be idempotent on a key the client does not send.** The client POSTs
`/hooks/session-start/{vendor}` once per turn on Antigravity and Kiro, and once per process
per session on OpenCode and Pi, with no idempotency key. The server must derive the
lifecycle event id from `(vendor, session_id, event kind)` and from nothing in the payload —
deriving it from the body would mint a new id per turn, because `started_at` varies.

**First-write-wins on start facts, not last-write-wins.** `started_at` drifts forward on every
repeat for Antigravity and OpenCode. A last-write-wins reconciliation makes a conversation's
recorded start time creep across its own lifetime. The first observation is the true one.

**Repeats must not be treated as evidence of activity.** A session that receives 121
`SessionStarted` POSTs had one start. Anything downstream that counts lifecycle events —
activity feeds, session counts, the analytics views — must count distinct lifecycle ids, not
requests.

**Fields present on the first repeat and absent from later ones must not be un-set.** Per
finding 5, `repository` rides only the first POST for a working directory, and
`harness_inventory` rides every one. A merge that treats an absent field as a clear would
delete the repository link on turn 2. Absent means "not restated", never "removed".

**A burst of several hundred identical POSTs on recovery is normal traffic, not abuse.** The
outage spool retains one entry per turn up to 1 MB per session and replays every one. Rate
limiting on this route must tolerate that shape, and a 429 must carry `Retry-After` — the
client's `SessionStartContextFetch` parses it, and `AgentHookPoster` treats 429 as transient
and re-spools, so a bare rejection converts a burst into a permanent loop.

**`harness_inventory` and `platform` arrive on every repeat and describe the machine, not the
session.** They belong on a machine record keyed by `machine_id`, upserted, not appended per
lifecycle event.
