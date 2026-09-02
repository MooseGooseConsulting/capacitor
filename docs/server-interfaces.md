# Server interfaces

This is the durable boundary between the inherited capture clients, Capacitor's planned data service, and the Sessions dashboard. It documents compatibility obligations and explicitly separates them from the server runtime that does not yet exist on `main`.

## Current state and target

The repository contains server-domain libraries and SQLite-based unit tests. It does not contain an executable API-host project on `main`, a deployed Capacitor API, an ingress, or an alpha service. Consequently, source references to a historical `Capacitor.Server` or to a hosted Kurrent endpoint are evidence about an interface, not evidence of a running Capacitor backend.

The target is an ASP.NET Core host backed by PostgreSQL. It receives capture data from CLI imports, watchers, and daemons; normalizes and persists that data; publishes data-plane changes; and serves the dashboard's session views. The Blood Arrow recovery cluster is the integration-test PostgreSQL host, not a deployed application target.

The current SQLite schema is useful evidence but has an important known mismatch: its event primary key omits `logical_seq`, despite the event record carrying it. It cannot be used as proof that a multi-event source line is preserved. The target PostgreSQL schema must use the four-part key described in [Capture and data](capture-and-data.md).

## Compatibility sources

The actual client request construction and the observed interface in [the mapped surface](../reference/SURFACE.md) determine compatibility. The archived [wire mapping](history/pre-recovery/schema/WIRECRAFT-MAPPING.md) and client models are supporting evidence. A server change is not compatible because a Markdown route list says it is; it is compatible when the relevant client flow and a remote PostgreSQL integration test complete correctly.

The server does not need to recreate the whole inherited product before it can become useful. The first contract is deliberately the data plane:

| Boundary | Contract | Target responsibility |
| --- | --- | --- |
| Lifecycle | Session start/end, title/recap, subagent start/stop | Establish or reconcile session and subagent metadata without losing earlier transcript evidence. |
| Transcript delivery | `POST /hooks/transcript` and `GET /api/sessions/{id}/last-line` | Persist source lines and canonical events idempotently; return an honest delivery cursor. |
| Dashboard reads | session search/list and per-session overview, transcript, events, trace, evaluation, and details | Read persisted data only; do not fabricate session-card or tab values. |
| Data updates | `/hubs/sessions` | Publish ingest-derived session/list/detail changes. |
| Fleet enrollment | client credentials, machine registry, daemon registry | Enroll headless nodes and expose their health/capabilities without treating them as agent-control commands. |
| Analytics and evaluation | governed analytics reads and versioned evaluation persistence | Operate on the corpus with scoped, read-only analytics and durable per-question findings. |

Agent launch, terminal I/O, ACP hosted-agent runtime, and Flows share some inherited hub terminology but are a separate control plane. They are not dependencies of the session data slice and must not be represented by fake endpoints or simulated dashboard state.

## Ingestion HTTP contract

### Lifecycle

The client has parameterized session lifecycle hooks, including `POST /hooks/session-start/{vendor}` and `POST /hooks/session-end/{vendor}`. The vendor segment is a value, not a finite route list. Supporting a new normalizer should not require inventing a new endpoint.

A lifecycle payload carries the session identifier, model and times when available, repository evidence, origin (live capture or historical import), and appropriate owner, visibility, machine, and daemon context. A transcript may arrive first. The server creates an owner-scoped placeholder, then updates the same dashless session ID when a start event arrives. A missing session end must not return a final success if that would make a spooling client discard the end event forever.

Subagent start is an acknowledgement boundary: the producer may not stream a child until the server has accepted its direct relationship to the session parent. The canonical model and console render subagents one level deep, not as a recursive tree. A deeper source relationship remains in retained raw evidence but does not change that flat read model or its acknowledgement boundary.

### Transcript batches

The transcript endpoint accepts a batch shaped like this:

```json
{
  "session_id": "...",
  "agent_id": "...",
  "lines": ["raw vendor source record"],
  "line_numbers": [0],
  "vendor": "codex",
  "strict": true,
  "repository": { "owner": "...", "repo_name": "..." }
}
```

`lines` are raw vendor records. `line_numbers`, when supplied, identify their source coordinates and must match `lines` one-for-one without duplicates. The server preserves that coordinate; it does not renumber in a normalizer. Compatibility defaults for a client which omits coordinates must be tested against that client, not guessed from another vendor's transcript convention.

`strict` governs error reporting, not permission to lie about persistence. A malformed or unrecognized source line must have a visible line-level outcome. In strict mode the batch is not acknowledged as fully successful when any line fails. In non-strict mode the response reports failures, and the watermark never advances through a rejected line. A line intentionally filtered by a normalizer is distinguishable from a failed line and is only acknowledged after its raw source/disposition is durable.

`GET /api/sessions/{id}/last-line` is the delivery truth for one session and agent stream:

| Result | Meaning |
| --- | --- |
| `404` | The session is unknown. |
| `204` | The session exists but this stream has no accepted source-line watermark. |
| `200` with `last_line_number` | The last contiguous accepted source coordinate; resume after it in the same coordinate system. |

There is no success response that may claim a highest observed line while a prior source line is absent. Replays are normal: duplicate four-part event keys and source-line receipts are no-ops, while a previously missing line can close a gap and move the cursor.

## Read contract for the Sessions surface

The dashboard is a server reader. Its typed client must call a configured API base URL and show an unavailable/error state when that server is absent or rejects a request. Sample sessions, silent zero totals, and client-side transformations that replace missing server fields are not a fallback.

The server read surface needs to support:

- paged session search with query, repository, vendor, status, total count, and a stable ordering;
- one coherent session detail document containing the header, canonical events, trace, and latest evaluation, plus tab-specific reads for bounded loading;
- transcript filtering/windowing that preserves canonical order and makes thinking inclusion explicit;
- raw Events rows with source position, agent identity, raw-payload provenance, typed tool data, usage, and error state;
- a Trace composed from the same events: turn rollups interleaved with non-turn entries;
- versioned evaluation run and per-question verdict data; and
- machine and repository dimensions sufficiently exposed to avoid false one-repository or one-machine attribution.

The session list can only display fields supplied by its projection. If context occupancy, file diffs, or a specific aggregate has not been persisted, the API should omit it or mark it unavailable. It must not encode an invented `0` that becomes a product claim.

## Hub and fleet boundary

`/hubs/sessions` initially exists for session data: a watcher connection, batch acknowledgements, and server-to-client notifications that a session was added, changed, or removed. Delivery acknowledgements carry the same contiguous-prefix semantics as the HTTP watermark. The hub's session-data methods have integration tests independent of dashboard rendering.

The target fleet is many independently running machines feeding one corpus. It therefore needs a client-credentials exchange for headless nodes, a machine registry, and daemon registration/heartbeat endpoints. Machine credentials are for data-plane identity; they do not grant a node an implicit right to launch agents or execute terminal commands. Network authorization, machine enrollment, and user/session visibility must remain separate decisions.

Machine IDs, daemon IDs, host capabilities, and heartbeats are stored with enough history to answer which node produced a session or has stopped reporting. A local path is retained as provenance only; cross-machine correlation uses repository identity and event-level evidence as described in [Capture and data](capture-and-data.md).

## Database and transaction boundary

PostgreSQL owns durable server state. A single ingestion transaction must leave these components mutually consistent:

1. source-line receipt/disposition and any dead-letter diagnostic;
2. all canonical events keyed by `(session_id, agent_id, line_number, logical_seq)`;
3. session/subagent metadata and monotonic rollups; and
4. the stream's contiguous watermark.

The system may optimize or project after a durable commit, but it must never make a delivery cursor visible before the events that justify it. A projection is rebuildable; raw source and canonical events are the evidence.

Analytics is constrained to governed, read-only views and repository scope. Existing historical material identifies a 32-view inventory, but view names are not a license to invent a body for data that has no backing table. Add a view only with its documented source tables, grain, scope behavior, and remote PostgreSQL test.
