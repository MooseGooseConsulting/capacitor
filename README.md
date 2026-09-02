# Capacitor (working name)

Capacitor is the system we are building to own the full coding-agent session
stack: capture on every fleet node, normalize and preserve the evidence in one
corpus, query it, and make it useful through desktop and web products. The
source-available client is inherited; the server, store, fleet control plane,
and console are ours to build.

The objective is not a single-machine session viewer or a PostgreSQL test
database. It is a standalone, fleet-aware system in which a second enrolled
machine can capture into the same corpus, its records stay attributable to
that machine, and a new vendor can be added without a closed-server gate.

## How to read this repository

The documents below distinguish four kinds of statement:

- **Observed** — evidence captured from the inherited client, the live Kurrent
  instance, raw vendor data, or a bounded probe.
- **Implemented** — behavior in this repository's current code. Unmerged work
  and a passing test environment are not a deployed system.
- **Target** — intended Capacitor behavior, derived from the brief and
  evidence. It is not a claim that the behavior works today.
- **Open** — a decision or measurement still required before implementation can
  honestly commit to a shape.

Authority is ordered deliberately:

1. Explicit operator decisions, [PROMPT.md](PROMPT.md), and the fleet objective
   in [reference/FLEET.md](reference/FLEET.md) decide future-state requirements.
   FLEET wins when an older single-machine assumption conflicts.
2. [reference/SURFACE.md](reference/SURFACE.md),
   [reference/CROSS-REPO-SESSIONS.md](reference/CROSS-REPO-SESSIONS.md), and the
   inherited wire/client behavior are the compatibility and measurement oracle,
   unless an explicit future-state decision deliberately overrides them.
3. Current source and runnable tests say what exists here now; they do not
   silently choose the target.
4. [reference/WAVES.md](reference/WAVES.md) is a proposed sequencing hypothesis
   and feature-cut method, not a mandate or an approved product cut.
5. The historical plans, specs, and probes under
   [docs/history/pre-recovery/](docs/history/pre-recovery/provenance.md) are
   detailed source material. They must be reconciled with the higher sources
   before becoming current Capacitor commitments.

## System documentation

| Document | Covers |
| --- | --- |
| [Vision and roadmap](docs/vision.md) | Whole-stack goal, authority, gates, and the path from current work to fleet acceptance |
| [Fleet and operations](docs/fleet-and-operations.md) | Machine identity, daemon/service ownership, networked deployment, configuration, and recovery/test boundaries |
| [Capture and data](docs/capture-and-data.md) | Vendor discovery, hooks, import, canonical evidence, ordering, replay, privacy, and normalizers |
| [Server interfaces](docs/server-interfaces.md) | Client/server contract ownership, lifecycle/ingest/read APIs, hubs, and compatibility rules |
| [Agents and daemons](docs/agents-and-daemons.md) | Registered execution, consent, worktrees, terminal ownership, and lifecycle safety |
| [ACP and harnesses](docs/acp-and-harnesses.md) | Capability-negotiated harness integration and vendor-specific evidence |
| [Desktop client](docs/desktop.md) | Local Avalonia supervision, onboarding, activity, session/workspace rail, terminal, and chat |
| [Web console](docs/web-console.md) | The server-backed console: Sessions, Agents, Insights, Flows, Work Items, and captured UI evidence |
| [Flows and review](docs/flows-and-review.md) | Flow lifecycle, reviewers, participant protection, MCP result channels, and containment |
| [Evaluations, analytics, and work items](docs/evaluations-analytics-work-items.md) | Persisted grounded evaluation, governed analytics, and work-item topology |
| [Testing](docs/testing.md) | Conformance, vendor fixtures, remote PostgreSQL validation, and fleet-safe test rules |
| [Evidence and decisions](docs/evidence-and-decisions.md) | Measured findings, material contradictions, decision status, and revalidation rules |

## Present implementation boundary

There is no deployed Capacitor alpha, public ingress, production backend, or
production data store. PostgreSQL on Blood Arrow is an isolated recovery/test
target, not evidence of a deployed service. A data-first Sessions/API slice is
being recovered and tested against that database, but it is not the complete
product and it does not establish the target web, desktop, daemon, or fleet
features as current capability.

## Provenance and captured material

`reference/` contains the observed inherited surface, fleet evidence, vendor
findings, and captured UI assets. It is evidence, not our runtime
configuration or brand. See [reference/evidence.md](reference/evidence.md)
and [NOTICE.md](NOTICE.md) before reusing captured names, fonts, marks, or
assets outside internal work.

The pre-recovery corpus remains available at
[`docs/history/pre-recovery/`](docs/history/pre-recovery/provenance.md). It is
there to support traceability and deeper research, not to substitute for the
organized documents above.
