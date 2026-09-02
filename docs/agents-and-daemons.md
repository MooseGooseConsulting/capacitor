# Agents and daemons

## Purpose

Capacitor observes AI coding-agent work. Agent and daemon code exists in the
repository today, but it is important to separate three things that historical
material often blended together:

1. **Capture** — discover, import, hook, spool, normalize, and serve session
   data.
2. **Local supervision** — keep long-running capture and local IPC healthy on
   a machine.
3. **Interactive control** — launch an agent, relay terminal I/O, host ACP,
   run reviewers, or coordinate Flows.

The first is the current delivery priority. The second supports it. The third
is retained source and future scope, not a substitute for a data-backed
corpus or a claim that a remote control service exists.

## Implemented client capabilities

The inherited CLI and daemon contain real implementation for the following
client-side functions:

- discovery and historical import for multiple coding-agent stores;
- vendor hook installation and live transcript/lifecycle posting;
- per-session and cross-session lifecycle/transcript spools;
- replay/drain behavior and watermark-based resume;
- named daemon instances, local IPC, and SignalR client connectivity;
- local process supervision, reconnect/heartbeat behavior, and cleanup;
- MCP configuration and agent-skill installation; and
- local agent, terminal, ACP, review, and Flow-related code paths.

The source inventory does not prove that every listed vendor or every command
is supported by the new backend. A vendor is end-to-end supported only after
its real data is normalized, persisted, replayed idempotently, and rendered
through the new service. Similarly, a command that targets the former hosted
server remains a client expectation until Capacitor implements and validates
its server counterpart.

## Capture path

Capture begins at the agent's own data and hook surfaces. The client posts
session lifecycle records and bounded transcript batches, tagged with vendor,
session, agent, line number, repository evidence, and origin. Historical
import and live capture must share the same normalizer and persistence rules
so the same source record cannot mean different things depending on how it
arrived.

The server-side target is documented in [Capture and data](capture-and-data.md). Important
capture boundaries are:

- unsupported or malformed input fails visibly; it is not silently marked as
  imported;
- a subagent needs a durable parent relationship before its own stream can be
  interpreted;
- source payloads remain recoverable alongside normalized records;
- a source acknowledgement follows durable event and watermark persistence;
- hooks should remain bounded so they do not make interactive coding agents
  wait on a slow network; and
- a restart, exit, or temporary authentication failure leaves undelivered work
  drainable rather than discarded.

The existing daemon's periodic spool-drain loop is significant because some
vendor paths end a session without another hook invocation. A daemon that only
drains when a new hook process happens to run can strand those records.

## Vendor model

The inherited client knows these capture families: Claude Code, Codex, Cursor,
Copilot, Gemini, Kiro, Kimi, Pi, OpenCode, and Antigravity. Their on-disk
formats and live-hook affordances vary materially; the catalog must not be
used as a compatibility guarantee.

Current server-library source includes normalizers for Claude, an ACP-oriented
set of dialects, and Antigravity. PostgreSQL/API integration work also includes
additional normalizer recovery, but it is not a blanket claim that all vendor
paths work. The proper proof for each vendor is a fixture whose source lines,
canonical event order, accounting, replay outcome, and rendered projections
are tested against observed behavior.

The target extension rule is simple: adding a vendor means adding its discovery
and/or hook integration, a normalizer, fixtures, and end-to-end proof. It must
not require a private selector or a special case in a closed service.

## Daemon responsibilities

A daemon is a local, named, long-running capture and supervision process. It
is not the backend.

Its responsibilities include:

- register its identity and known repositories/capabilities with the service
  when a fleet service exists;
- keep a session-update connection healthy and re-register when the server no
  longer recognizes its slot;
- periodically drain durable capture backlogs under a bounded budget;
- preserve local process identity and cleanup/reap state through restart; and
- make its health and failure reason observable to the local CLI or desktop.

The operating target treats a named daemon as a node-level fact. A server must
not report a daemon as healthy merely because an old connection is still open;
the client heartbeat exists to surface slot displacement and stalled transport.
Conversely, daemon behavior cannot make a hosted API appear available when the
service is absent.

## Interactive agents and ACP

The codebase contains an ACP runtime, hosted-agent launchers, terminal
attachment, permission bridges, review runners, and Flow-oriented commands.
The historical probes are valuable evidence about vendor behavior, especially
around reconnect, permission requests, capability negotiation, configuration
isolation, and containment. They are not a production launch policy.

Before any remote/hosted interactive-agent feature is enabled, it needs all of
the following:

- a real service contract and an explicit owner for the remote process;
- a chosen authorization and consent model for launch, input, cancellation,
  file access, terminal access, and external tools;
- a containment and credential boundary appropriate to the selected vendor;
- persistence and audit semantics for requests, decisions, and outcomes; and
- a test that uses the intended remote environment rather than a local mock.

Until then, unsupported calls must fail explicitly. A dashboard or desktop may
show historical/diagnostic information, but it must not present a fake control
plane.

## Consent and safety

Existing client code includes consent stores, local IPC frames, activity
models, permission bridges, and various vendor-specific policy helpers. These
are implementation evidence for local interactions, not universal
authorization. In particular, a vendor's local “trust” flag, an inherited
MCP configuration, or a sandbox probe does not establish a safe fleet-wide
permission boundary.

The enduring rule is that no one path can turn an observation surface into an
unapproved mutation surface. Capture is allowed to send the data necessary for
the corpus; agent execution, terminal input, filesystem access, and remote
tool admission each need their own explicit server-side policy.

## What to verify next

The next agent/daemon tests should advance the data path rather than only
exercise controls:

1. Run real historical and live data through the new PostgreSQL service for
   representative parent and subagent sessions.
2. Disconnect the service, accumulate capture work, restore connectivity, and
   verify exactly-once canonical results and contiguous watermarks.
3. Repeat from a second enrolled machine and verify machine/daemon attribution
   and node-specific vendor discovery.
4. Add a fourth, previously unsupported agent format end to end.

Only after those proofs should interactive execution be selected for a
delivery slice. The [fleet operating model](fleet-and-operations.md) explains
why these capture proofs are system-level rather than merely importer tests.
