# Capture and data

Capacitor's purpose is a durable, queryable corpus of agent work: not merely a dashboard and not merely a log receiver. Every machine that runs an agent should be able to contribute its sessions, transcripts, tool activity, and enrichment to one corpus without losing data while it is offline.

This page synthesizes the observed product surface, the inherited client's capture contracts, the measured cross-repository data, and the archived schema work. The source material remains useful evidence; it is not a substitute for this current contract.

## What is true now, and what is the target

| Area | Present in `main` | Required future state |
| --- | --- | --- |
| Capture clients | The inherited CLI, watchers, hooks, daemon, spool/drain path, and import sources are substantial existing code. | Preserve their delivery and offline-durability behavior while pointing them at Capacitor's server. |
| Server-domain code | `Capacitor.Server.Data`, `Capacitor.Server.Ingest`, `Capacitor.Server.Normalizers`, and `Capacitor.Server.Analytics` are library projects with SQLite-oriented tests. | A networked ASP.NET Core host, PostgreSQL repositories, real authorization, and an observable data-only hub. |
| Runtime | There is no API host project on `main`, no deployed Capacitor service, and no alpha environment. | A separately deployed, health-checked data service. The recovery PostgreSQL database is test infrastructure, not that service. |
| Event identity | The current SQLite migration has a `logical_seq` column but keys events only by `(session_id, agent_id, line_number)`. That drops all but one derived event from a source line. | A four-part identity: `(session_id, agent_id, line_number, logical_seq)`. |
| Vendor support | A catalog/discovery path is not proof of an end-to-end normalizer. The current libraries contain partial normalizers. | Each claimed vendor has a real fixture, an accepted normalizer, PostgreSQL replay coverage, and a rendered dashboard result. |

The PostgreSQL choice is settled for Capacitor's server and remote integration testing. SQLite remains useful for tightly scoped library tests and for reading vendor-local stores; it is not the Capacitor data-service backend and must not stand in for PostgreSQL integration behavior.

## Where the future state comes from

Future-state requirements come first from explicit operator decisions,
[`PROMPT.md`](../PROMPT.md), and [Fleet](../reference/FLEET.md). Fleet wins when
an inherited one-machine assumption conflicts: machine identity, offline
delivery, and headless enrollment are data-plane requirements, not optional
extensions. The inherited wire contract and measured KCap behavior in [the
mapped surface](../reference/SURFACE.md) are the compatibility oracle for an
endpoint unless that higher decision deliberately overrides them. [Cross-repository
sessions](../reference/CROSS-REPO-SESSIONS.md) constrains repository attribution
with measurement. Historical schema and wire designs, including [the canonical
schema](history/pre-recovery/schema/CANONICAL-SCHEMA-SPEC.md), [wire
mapping](history/pre-recovery/schema/WIRECRAFT-MAPPING.md), and [analytics-view
inventory](history/pre-recovery/schema/ANALYTICS-VIEWS-SPEC.md), remain research
and must be revalidated rather than promoted by age or detail.

The result is deliberately data-first: transcript fidelity and useful enrichment precede agent launching, terminals, flows, and other control-plane features.

## Capture flow

```text
vendor-local history or live hook
  -> discovery and explicit import selection
  -> lifecycle envelope + ordered transcript source lines
  -> durable local spool when the server is unavailable
  -> authenticated server ingestion
  -> raw-source retention + vendor normalization
  -> canonical event stream and projections in PostgreSQL
  -> session, transcript, event, trace, evaluation, and analytics reads
```

The producer identifies a session, optional subagent stream, vendor, ordered source lines, repository evidence, and whether the material originated from a live hook or a historical import. A server may receive transcript data before its session-start lifecycle event. It must create an owner-scoped placeholder and reconcile it when the lifecycle record arrives; arrival order is not permission to discard evidence.

Historical import is not a bulk-upload default. It must select an explicit scope, show what will be sent in an interactive path, retain the configured visibility, and preserve the original source and position information. A non-interactive import must require an explicit scope rather than silently broadening it. The archived [history-import scope design](history/pre-recovery/superpowers/specs/2026-05-13-ai-613-history-import-scope-design.md) contains the detailed behavior that led to these constraints.

## Canonical corpus model

### Source lines and derived events

One vendor source line can contain visible assistant text, thinking, several tool calls, and usage. It is therefore not safe to make one transcript line equal one event. The canonical event identity is:

```text
(session_id, agent_id, line_number, logical_seq)
```

- `session_id` is normalized to its dashless form; a dashed copy/paste form maps to the same record.
- `agent_id` is empty only for the parent stream. A subagent has its own stream and its own watermark.
- The canonical display relationship is flat: every subagent is a direct child of
  the session's parent stream, one level deep. If a source exposes deeper
  nesting, preserve that raw relationship for provenance but do not manufacture
  a recursive canonical tree.
- `line_number` is the source coordinate supplied by the producer. Normalization never renumbers it.
- `logical_seq` is a stable, zero-based order among the events emitted from that one source line. It is part of the key, not decorative metadata.

Each canonical event keeps the source vendor, original raw payload, timestamp, event type, model, token/cache/cost deltas, tool input/output and exit status, error state, content, and the finest available cwd/repository evidence. Raw evidence permits later improvement of a normalizer without pretending that a new interpretation was captured originally.

A source-line delivery record is also required. Some source lines legitimately normalize to no user-facing event, such as metadata or filtered side-chain records. Such a line may advance the receipt frontier only when the original source line and its accepted disposition are durable. A malformed or rejected line is not silently converted into a successful empty event.

### Watermarks, replay, and projections

The watermark belongs to `(session_id, agent_id)`. It is the greatest **contiguous** accepted source-line prefix, not the maximum line number observed. If lines 0 and 2 arrive, the watermark is 0; no later line is implicitly acknowledged across the gap. Replaying an already committed event key changes neither events nor rollups.

The write transaction must make the source-line disposition, all derived events, session rollup, and watermark agree. If any required write fails, a client must be able to retry without duplicates or a falsely advanced cursor. The read-back watermark distinguishes:

- unknown session;
- known session with no accepted source line for that stream; and
- a concrete last accepted source coordinate, from which the client resumes.

The session header is a projection over this evidence: lifecycle state, title, ownership, visibility, model, session chain, primary repository, machine/daemon, counts, totals, and recency. Transcript, Events, Trace, evaluation, and dashboard cards must all derive from the same persisted stream. A dashboard must show unavailable or unreported data rather than invent zeroes.

### Repository and machine dimensions

`sessions.repo_hash` remains a primary-repository compatibility field, but it cannot be the only repository model. Per-event cwd/repository evidence and a many-to-many `session_repositories` projection are required:

```text
session_repositories(
  session_id, repo_hash, is_primary, first_seen_event, event_count
)
```

Primary is derived from observed event weight, not assumed from the launch cwd. Repository evidence is nullable: filling a missing event-level repo from the session header would manufacture attribution. This matters especially for Codex, where measured tool workdirs span repositories frequently.

The same rule applies to machines. `machine_id` is first-class session metadata, with a machine registry and daemon records for health and capability. A path is machine-local; stable repository identity and a recorded cwd are not interchangeable. Node-specific remapping belongs at ingestion/import time, never as a hidden rewrite of raw history.

## Enrichment and normalizer acceptance

The target vendor inventory is Claude, Codex, Cursor, Copilot, Gemini, Kiro, Kimi, Pi, OpenCode, and Antigravity. The checked-in inherited importer registry does not include Kimi; its importer is recovery work to port, not an existing capability. The inventory states sources worth supporting, not a claim that every payload from one of them already works.

A vendor normalizer is accepted only when all of the following are true:

1. It is selected explicitly for that vendor; unknown vendors fail visibly rather than falling through a generic parser.
2. A real, scrubbed source fixture preserves the source format and one expected canonical output per source line, including zero and multiple derived events.
3. The fixture verifies event order, raw payload, content/thinking/tool/error fields, timestamps, model, and token/cache/cost accounting. Usage from one source item is counted exactly once.
4. A replay into the remote PostgreSQL test database is idempotent and advances only the contiguous source-line watermark.
5. The resulting session is readable through Transcript, Events, Trace, and session-summary APIs. A parser that succeeds but cannot produce the intended corpus views is incomplete.

The behavioral expectation should be taken from observed sessions where an inherited product behavior is being matched. Where that product lacks a dimension Capacitor needs (machine identity and multiple repositories are the known examples), the measured data and fleet requirement deliberately override it.

## Privacy and retention boundaries

Transcript content, tool arguments/results, raw payloads, local paths, configuration snapshots, and repository metadata can be sensitive. The corpus must not hide this fact behind an "anonymous telemetry" label.

- Capture scope and per-session visibility are explicit user choices. A repository cannot be inferred private or safe merely from its name; absence of a repo is not consent to broaden import scope.
- Raw source evidence is access-controlled with the session. Analytics and dashboard projections do not create a less-restricted copy of transcript content.
- Secrets and credentials are never written to logs, database connection strings, fixtures, documentation, or command arguments. If configuration/memory snapshots are introduced, they require structure-aware redaction before persistence; string replacement over a serialized record is not adequate.
- Telemetry sent to the inherited hosted service is outside the Capacitor corpus and is intentionally cut. The fleet's own authentication and observability must be designed independently.

The exact retention, deletion, sharing, and access-control policy has not been selected. Until it is, no component may claim that raw transcript retention is harmless or that a testing database is a production data store.
