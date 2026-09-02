# Schema-wave recovery ledger

Closed PRs #14 through #17 are preserved source material, not merge candidates.
They form a stale stack with incompatible SQLite, PostgreSQL, route, and
SignalR assumptions. Recovery means extracting validated behavior into the
selected in-repo PostgreSQL implementation, with a focused commit for each
durable unit; it does not mean force-merging or deleting the original branches.
Until that recovery code lands in `main`, PostgreSQL is the target rather than
the current merged backend.

| PR | Original focus | Recovery decision |
| --- | --- | --- |
| #14 | HTTP gateway and API contracts | Reuse route and client-contract research, identifier handling, and useful evaluation context shape. Rewrite persistence-facing routes against the selected PostgreSQL interfaces; do not revive the SQLite gateway. |
| #15 | PostgreSQL stack | Reuse the needed relational/event-store and analytics behavior only after review. The recovered PostgreSQL branch is the replacement baseline; invalid migration and merge assumptions are not imported wholesale. |
| #16 | SignalR hub | Reuse the need for real-time session events. Rebuild it at `/hubs/sessions` around persisted session changes, not the incompatible `/hub/capacitor` prototype. Agent-control methods are deferred. |
| #17 | MCP gateway and work items | Preserve route-contract research where it matches current clients. Reject its in-memory work-item storage and SQLite implementation. Session/analytics reads belong on the PostgreSQL read model; work-item mutation is later scope. |

## Acceptance rule

Each recovered behavior must compile with the current solution and pass its
remote test fixture against `capacitor_test`. A historical commit is not proof
of correctness, deployment, or compatibility. The original refs remain
reachable for provenance until the owner intentionally retires them.

## What is deliberately deferred

The data-first milestone does not implement launch routing, vendor process
execution, terminal input, Flow execution, or work-item mutation. Those
features depend on registered daemons and different hub contracts; they cannot
be treated as finished because a schema-wave prototype once mentioned them.
