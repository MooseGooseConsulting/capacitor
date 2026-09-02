# Architecture

## Boundaries

The backend is ASP.NET Core code in this Capacitor monorepo. It receives
session lifecycle and transcript-hook traffic from the existing CLI, watchers,
and daemons; normalizes vendor source records; persists canonical records in
PostgreSQL; and exposes read APIs and a session-update hub to the web console.
The desktop Avalonia application remains an existing local-client surface. It
is not a substitute for the captured web dashboard.

There is no deployed alpha service, public ingress, or production backend.
Nothing in this document authorizes treating a recovery environment as a
customer-facing or production surface.

## Recovery database

The selected integration target is Blood Arrow's CloudNativePG cluster
`data-platform/pg18-core-recovery`, reached through its read-write service
`pg18-core-recovery-rw`. Capacitor uses its own PostgreSQL role and the
isolated `capacitor_test` database; it must not reuse the shared `app` owner
or another application's database.

The password source is Doppler `homelab/dev`, secret name
`CAPACITOR_TEST_DB_PASSWORD`. Kubernetes receives it only as the
`data-platform/capacitor-test-db-credentials` basic-auth Secret. Source values
are never committed, printed, copied into documentation, or placed in command
arguments. The database, role, and secret are test infrastructure, not a
production credential design.

## Data path

```text
CLI / watcher / daemon
  -> lifecycle and transcript hooks
  -> vendor normalizer
  -> canonical event and session projection
  -> PostgreSQL event store + atomic watermark
  -> session/read APIs and session-update hub
  -> Blazor + MudBlazor web Sessions surface
```

One source transcript line may yield more than one canonical event. Canonical
event identity therefore includes `(session_id, agent_id, line_number,
logical_seq)`. Event insertion and contiguous watermark advancement are one
transaction: a failed or partial batch cannot claim a line as processed.

## Interfaces in the data-first slice

The backend must make the ingestion routes already used by the clients work,
including session start/end, subagent start/stop, transcript ingestion, and
last-line watermark reads. It also owns the read model required by the web
Sessions view: list/search, overview, transcript, raw events, trace,
evaluation, details, and live session changes.

`/hubs/sessions` is the durable session-data hub boundary. Its initial job is
to publish ingest-driven session/list/detail changes. Agent-launch, terminal,
ACP runtime, and Flow hub methods are separate future work; their absence must
fail explicitly rather than be simulated.
