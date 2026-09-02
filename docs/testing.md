# Testing the data plane

Capacitor's acceptance test is a working capture path backed by real PostgreSQL, not a successful local mock or a document that says a migration ought to work. Local broad runs can invoke interactive `allow` flows and fail for reasons unrelated to the software under test, so they are not the primary acceptance path.

## Test environment

Remote integration testing uses the isolated `capacitor_test` database and `capacitor_test` role in Blood Arrow's recovery CloudNativePG cluster:

```text
namespace: data-platform
cluster:   pg18-core-recovery
database:  capacitor_test
role:      capacitor_test
```

This is a dedicated test data plane. It is not a production Capacitor service, an alpha deployment, or permission to alter another application's schema or data. Tests connect through the cluster's read-write service from the remote execution environment. A short-lived loopback port-forward may be used for the test process; it is transport plumbing, not a claim that the backend is local.

Credentials are injected remotely from the Kubernetes basic-auth Secret `data-platform/capacitor-test-db-credentials`. Doppler's `homelab/dev` `CAPACITOR_TEST_DB_PASSWORD` is the provisioning source. Secret values and complete connection strings must not appear in source, test fixtures, shell arguments, logs, or documentation.

## What runs where

| Test kind | Preferred execution | What it proves | What it cannot prove |
| --- | --- | --- | --- |
| Pure parser/normalizer fixture | Remote runner by default; a narrow local run is acceptable when it cannot reach hooks or interactive approval. | A source record maps to its expected canonical records. | PostgreSQL semantics, network contract, or dashboard data fidelity. |
| PostgreSQL storage contract | Remote only, against `capacitor_test`. | Migrations, four-part event identity, transactions, rollups, concurrency, and watermark behavior. | A client can actually send the expected wire payload. |
| API integration | Remote only, with the real API process and remote PostgreSQL. | Lifecycle, transcript, watermark, search/detail, evaluation, and governed-query routes. | Browser rendering or an inherited-product match. |
| Capture path | Remote only. | A real CLI/import or watcher path can deliver, retry, drain a spool, and resume. | That an event/trace presentation matches the behavioral oracle. |
| Dashboard path | Remote API plus remote PostgreSQL data. | The Sessions list, Transcript, Events, Trace, Evaluation, and Details views read persisted data. | Unimplemented control-plane features. |
| Behavioral conformance | Remote PostgreSQL end to end. | Capacitor reproduces the relevant observed behavior for a known source session. | Behavior the reference product never exposed or that deliberately differs under the fleet model. |

The repository uses TUnit test executables. Use the test project's configured runner and check that tests were actually discovered and run; a `dotnet test` invocation that reports zero tests is neither a pass nor evidence of a backend failure. The remote runner is the default for focused tests too when it is available.

SQLite can remain in fast unit tests for local vendor-store readers and isolated library logic. It must never be substituted for a PostgreSQL integration claim, an API test, or a database migration result. Likewise, a temporary mock database cannot prove the production dialect, transaction, constraint, or connection behavior that Capacitor depends on.

## Required data-plane scenarios

Every PostgreSQL/API integration run creates a unique run identifier and uses only rows owned by that run. Cleanup, where needed, removes only those rows; it does not drop, reset, or recreate the shared `capacitor_test` database.

The test suite must cover these behaviors:

1. **Lifecycle ordering.** Transcript before session start creates a reconcilable placeholder. A later start fills metadata without a second session. A missing end is not acknowledged as delivered.
2. **Four-part event identity.** One source line yielding text, thinking, and multiple tool calls persists every logical event under distinct `logical_seq` values. Replay produces no duplicates and does not inflate totals.
3. **Contiguous watermark.** Out-of-order or gapped lines never advance the cursor across the gap. Inserting the missing line advances the cursor only through the resulting contiguous prefix. Parent and subagent streams have separate watermarks.
4. **Failure semantics.** Invalid, unknown, or rejected source lines have a visible outcome. Strict batches are not fully acknowledged; non-strict behavior reports the failures and does not conceal them by advancing a delivery cursor across them.
5. **Transaction atomicity.** An injected failure cannot leave events without the matching source disposition/rollup, or a watermark that claims events absent. Concurrent batch attempts preserve the same invariant.
6. **Repository and machine truth.** Event-level repository/cwd information may be absent and may differ within a session. Tests cover multiple repositories and distinct machines without backfilling false event attribution from a session header.
7. **Read projections.** Search pagination, detail, transcript windows, Events, Trace, evaluation, and analytics views all derive the expected data from one persisted event stream. Missing measurements stay missing rather than becoming zero.
8. **Isolation and parallelism.** Test-owned environment variables, ports, data scopes, and cleanup cannot leak between parallel tests. A test must not depend on or overwrite a developer's local configuration.

## Behavioral oracle

The inherited Kurrent surface is a behavioral reference, not a schema generator. For a selected real source session, retain a scrubbed fixture containing both the original source records and the observed expectation: Events order, turn boundaries, token/cache accounting, tool counts, transcript rendering inputs, and relevant analytics totals.

An oracle test imports that fixture through Capacitor's real remote PostgreSQL path, then reads it through the same API the dashboard uses. It asserts the expected behavior rather than only a row count. At minimum it checks:

- canonical event sequence and all multi-event source lines;
- event model, timestamp, content/thinking, tool input/output, error, and usage fields;
- token/cache/cost totals counted once at their documented source granularity;
- turn count, non-turn interleaving, duration and tool-count trace summaries;
- idempotent second import and contiguous cursor behavior; and
- list/detail/dashboard values derived from the stored result.

Use one fixture per represented source dialect, including a subagent case. Fixtures must be scrubbed with structure-aware replacements that retain record shape and positions; a fixture that destroys the behavior under test is not an oracle. When the observed product is silent or demonstrably wrong for the fleet—machine attribution and cross-repository sessions are known examples—the explicit Capacitor data contract takes precedence and the test names the deliberate divergence.

## Vendor acceptance gate

A vendor is not supported because it appears in a discovery catalog, an enum, or a UI chip. Its acceptance gate is:

1. a real scrubbed fixture and explicit normalizer selection;
2. a line-to-canonical-event oracle that covers zero, one, and multiple emitted events where the format permits them;
3. remote PostgreSQL replay/idempotency/watermark coverage;
4. a remote API read of the resulting Transcript, Events, Trace, and session summary; and
5. a remote dashboard-path check using that persisted result.

Unknown vendor input must be rejected or explicitly recorded as unsupported. It may not pass through a generic normalizer and be reported as a successful import merely because an HTTP request returned 2xx.

## Reporting a test result

Report the exercised surface, runner location, test count, and database target—not just a green process exit. For example: "remote API integration: 12 passed against `data-platform/pg18-core-recovery`, database `capacitor_test`". Do not report a SQLite unit test as a PostgreSQL test, a port-forward as a deployed service, or a mock response as a dashboard/backend integration test.
