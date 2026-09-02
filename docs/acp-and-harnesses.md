# ACP and harnesses

## Purpose

Capacitor captures work produced by many coding-agent harnesses and may eventually host some of them. Those are separate responsibilities:

- **capture** discovers/imports transcript and lifecycle data, normalizes it, and delivers it durably to the corpus;
- **hosting** starts a local child agent, maintains its transport, exposes status, and may mediate interaction;
- **orchestration** assigns hosted agents to a Flow or review round.

Capture is the priority and must not wait for a hosted-agent implementation. Hosting and orchestration are future capabilities with vendor-specific safety and lifecycle constraints; they cannot be inferred just because an importer parses that vendor's history.

## Fleet and implementation boundary

The target is a fleet corpus: each machine that runs agents records to one durable backend, survives temporary disconnection by spooling and draining, and identifies the machine that produced a session. A localhost-only assumption, unauthenticated fleet traffic, or an analytics model without `machine_id` fails that objective. See the [fleet objective](../reference/FLEET.md) for the decisions that override earlier single-machine scope notes.

The current backend recovery work is not evidence that the whole fleet-hosting plane exists. Before a harness is declared supported in a present-tense product statement, verify against the actual build and target deployment:

1. discovery/import or live-hook behavior;
2. normalizer coverage into canonical events;
3. idempotent lifecycle/transcript delivery and interruption recovery;
4. daemon/runtime availability on the relevant host platform;
5. a backend route and authentication path accepting that data; and
6. an end-to-end run visible in the session API and console.

## Harness inventory

The inherited client recognizes ten historical import sources: Claude, Codex, Cursor, Copilot, Gemini, Kiro, Kimi, Pi, OpenCode, and Antigravity. The historical live-harness catalog contains the same set except Kimi. This is source inventory, **not** a current compatibility guarantee.

| Vendor family | Historical source shape | Requirement for Capacitor |
| --- | --- | --- |
| Claude | JSONL session data, including sibling subagents | Preserve subagent linkage and per-event cwd where available. |
| Codex | Rollouts/session JSONL, turn context, tool workdirs | Do not collapse multi-repository tool work to the launch cwd. |
| Cursor | Transcript JSONL and lifecycle hooks | ACP-hostable in historic designs; subagent lifecycle must open before transcript delivery. |
| Copilot | Vendor-specific hooks/transport and limited local transcript evidence | Do not infer complete on-disk import from hook support. |
| Gemini | Vendor-specific JSONL/hook data | ACP capability advertisement needs live lifecycle validation. |
| Kiro | CLI/OpenCode-derived stores and other surfaces | Treat CLI and editor-extension data as separate formats. |
| Kimi | Two distinct on-disk layouts | Historic import-only source; no live-harness claim without a new implementation and test. |
| OpenCode | SQLite/Drizzle data model | Import and hosted ACP behavior are separate work. |
| Pi | Vendor-specific session data | Hosting remains runtime/platform dependent. |
| Antigravity | Vendor-specific data and RPC-style runtime history | Preserve plural workspace evidence where it exists. |

Every normalizer retains source vendor and raw provenance, emits the common event vocabulary, and preserves unknown/vendor-new data rather than silently dropping it. A vendor enters the supported capture matrix only after its normalizer, idempotency behavior, and recorded output pass an end-to-end test.

## Transport families

Historic desktop/harness designs grouped runtime transport as follows:

| Family | Typical members in those designs | Meaning |
| --- | --- | --- |
| **PTY** | Claude, Codex | The daemon owns a process terminal. A desktop app may attach; capture remains durable without an attachment. |
| **ACP** | Cursor, Copilot, Gemini, Kiro, OpenCode | The daemon is an Agent Client Protocol client of a locally launched agent server. Interaction and resume are vendor-specific. |
| **RPC/native** | Antigravity, Pi | The daemon uses a vendor runtime protocol rather than assuming ACP or a terminal. |

This table is a design-era vocabulary. Runtime factories and an explicit availability probe are authoritative for a real launch. An absent capability advertisement means unavailable; a missing field from an older daemon means unknown, not "nothing is supported". UI must preserve that distinction instead of hard-coding a vendor list.

## ACP contract

ACP is JSON-RPC between the Capacitor daemon (client) and a vendor agent process (server). It is neither the event store nor containment. The daemon must launch a vendor with explicit working context; initialize/create/load a supported session; translate notifications into canonical events; surface only interaction it implements; handle child death using a vendor-probed lifecycle; and keep the daemon-to-backend acknowledgement cursor independent from a child crash.

### Capability advertising is fail-closed

Do not advertise client `fs` or `terminal` capabilities that Capacitor cannot enforce. A historic Cursor probe found useful `edit`, `read`, and `execute` work occurred in the child process without agent-to-client `fs/*` or `terminal/*` requests. The recorded decision therefore advertises no client-served filesystem or terminal capability.

If an unimplemented inbound ACP method arrives, return the protocol's explicit "method not found" error. A successful `result: null` would falsely tell an agent that Capacitor performed an operation it did not. This applies to every unimplemented method, not only methods observed in a particular Cursor version.

That ACP boundary is not an OS sandbox. A local child may have the daemon's filesystem/process privileges. Passing a worktree as `cwd` is useful context, not proof the agent cannot leave it. Hard isolation requires an explicit operating-system/container design and platform validation.

### Permission, terminal, and reconnect behavior

An interactive hosted agent can request permission or elicitation only through a defined bridge with an owner, one-response rule, cancellation, and an auditable result. An unattended reviewer has no human interaction channel: an unsupported prompt or unknown interaction ends the round according to the Flow contract rather than waiting indefinitely.

PTY output and ACP tool-event output are different presentation sources. The web console renders persisted transcript/tool events; a desktop terminal is a local interactive viewport. A web event feed never implies authority to type into a process.

Protocol advertisement alone is insufficient proof that a vendor can resume after child death. The archived reconnect probe found Cursor and Copilot could reload a killed interactive session under observed versions, while Kiro's stale owner lock and Gemini's missing persisted crash session made them ineligible. Replay messages had no dependable cross-vendor deduplication key.

The resulting rule is to preserve the daemon-to-backend event stream, suppress agent-side `session/load` replay, reopen only after the protocol replay barrier, and never transparently resume an unattended Flow participant. An interrupted turn is not auto-replayed as new user work. A future vendor enablement repeats the probe; an old result is not an evergreen capability claim.

## Canonical capture and identity

- Event identity includes `session_id`, `agent_id`, and logical sequence.
- Origin distinguishes live capture from historical import.
- Session chains, subagent hierarchy, and Flow participation are independent relationships.
- Event/turn cwd and repository evidence are nullable and plural at session level; a primary repository is derived from evidence, not assumed from launch directory.
- `machine_id` travels with session start and remains queryable.
- Unavailable fields stay unavailable. A normalizer never invents token, cost, path, model, or vendor values to populate UI.

The inherited client has useful watermarking, batching, spooling, retries, and drain mechanics. They only constitute the intended system when a real server accepts and persists their acknowledgements. Test the whole route, not a mock that bypasses the backend.

## Desktop, daemon, and web responsibilities

The per-machine daemon owns process lifetime and knows vendor availability. The desktop app can show that daemon's active agents, choose a local repository/harness, attach to a PTY where supported, and own permission UI. It is not the data authority.

The web console owns cross-machine persisted visibility. It may show agent/daemon state, but launch, stop, input, or resize must name a specific daemon and use a server-authorized command contract. It must not silently substitute a remote machine, default a vendor, or claim a local worktree was used when it was not.

Local `agent` commands and web-launched default agents are different lifecycle paths. Flow/review participants need stronger protection: ordinary attach is read-only; stop requires explicit force; bulk stop reports skipped protected participants. Their messages belong in Flow rounds, not raw terminal stdin.

## Decisions still needed

- Which hosting controls belong in alpha after capture and the web evidence surface are reliable.
- The real deployment, fleet enrollment, and authentication policy.
- Required OS/container isolation per platform/vendor.
- Source-version qualification for importers and runtimes.
- Redaction and retention for configuration/memory snapshots.
- Distribution/update mechanics for multiple fleet nodes.

## Sources and verification

The source material is retained, not discarded. This synthesis draws from the [surface map](../reference/SURFACE.md), [fleet objective](../reference/FLEET.md), [ACP probe findings](history/pre-recovery/acp-probe-findings.md), [ACP filesystem/terminal capability decision](history/pre-recovery/ai-687-fs-terminal-capability-decision-design.md), [ACP reconnect design](history/pre-recovery/superpowers/specs/2026-08-04-ai1325-acp-reconnect-resume-design.md), [desktop supervisor design](history/pre-recovery/superpowers/specs/2026-07-31-desktop-supervisor-app-design.md), [app-shell design](history/pre-recovery/superpowers/specs/2026-08-04-ai1650-app-shell-design.md), and [cross-repository measurement](../reference/CROSS-REPO-SESSIONS.md).

Archived designs are prior evidence, not declarations that those plans shipped. A runtime-contract change requires source tests and a remote end-to-end capture run against the intended backend before this document can call it implemented.
