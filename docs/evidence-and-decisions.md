# Evidence and decisions

## Why this document exists

Capacitor inherited a large amount of source code, planning material, probes,
and captured product behavior. These are useful only if their status is clear.
An old plan can explain why code exists without deciding what we build next; a
live captured behavior can establish a fidelity target without importing that
service's branding or deployment model.

This page names the sources of future state and the rule for turning evidence
into a durable implementation decision.

## Source hierarchy

| Source | What it establishes | How to use it |
| --- | --- | --- |
| [`PROMPT.md`](../PROMPT.md) | The overarching standalone-stack objective, fidelity method, licensing boundary, and success conditions. | Product brief; read with the fleet brief, not as a release-status report. |
| [`reference/FLEET.md`](../reference/FLEET.md) | The fleet objective and the requirements it changes: network service, headless identity, machine attribution, and second-node proof. | Wins where older single-machine planning conflicts. |
| [`reference/SURFACE.md`](../reference/SURFACE.md) | Observed console behavior, client/server wire expectations, canonical event/trace behavior, and captured hosted-service surface. | Fidelity oracle and contract evidence; not our deployment plan or brand. |
| [`reference/CROSS-REPO-SESSIONS.md`](../reference/CROSS-REPO-SESSIONS.md) | Measured multi-repository session behavior from vendor stores. | Correctness evidence for event-level attribution and many-repository projections. |
| [`reference/EVAL-DIRECTION.md`](../reference/EVAL-DIRECTION.md) | The explicit decision to retain configurable, evidence-grounded evaluation. | Keeps the capability, not the inherited evaluator taxonomy or provider. |
| [`reference/WAVES.md`](../reference/WAVES.md) and [`reference/BACKLOG.md`](../reference/BACKLOG.md) | A proposed decomposition and a broad captured feature inventory. | Planning input only. Re-plan from evidence and do not treat their sequence or cut as settled. |
| Client and desktop source | Implemented capture, daemon, local IPC, and desktop behavior; exact route/payload expectations. | Inspect before changing a contract; code is not proof a replacement service exists. |
| Recovery server and web branches | Candidate PostgreSQL/API/dashboard implementation and remote test results. | Verify current branch, test target, and merge state before calling a capability implemented. |
| Historical archive | Previous designs, probes, schema drafts, and change notes. | Provenance and research leads only; revalidate facts and decisions. |

The historical archive is under `docs/history/pre-recovery/`. It is retained so
the reasoning, probe results, and source references are available, not so a
future reader has to accept every prior conclusion. In particular, historical
references to a hosted Kurrent service, tenant provisioning, SaaS signup,
telemetry, or production status do not transfer to Capacitor.

## Current decisions

| Area | Decision | Status and consequence |
| --- | --- | --- |
| Product objective | One shared corpus across the operator's fleet. | **Decided.** A single-machine build is not the finish line. |
| Backend technology | PostgreSQL is the selected shared persistence target. | **Decided.** SQLite libraries remain implementation/recovery material, not the shared backend choice. |
| Test database | Blood Arrow recovery CloudNativePG hosts an isolated `capacitor_test` database. | **Implemented for remote integration tests.** It is not production. |
| Current deployment | No alpha service, API host, public ingress, or production backend exists. | **Current fact.** Do not imply otherwise in UI, docs, or tests. |
| First delivery | Enriched session and transcript data plus the web Sessions experience. | **Decided priority.** Do not let launch/terminal/Flow work displace it. |
| Fidelity method | Compare observed source inputs and KCap outputs through executable conformance tests. | **Decided method.** “Builds” or HTTP success is insufficient. |
| Data identity | Ordered canonical events retain raw provenance and use `logical_seq` for one-to-many source-line output. | **Target invariant.** Migrations and API behavior must preserve it. |
| Fleet identity | Sessions need machine attribution and headless nodes need an auth path. | **Decided target.** KCap's user-only model is inadequate. |
| Repository model | A session can involve several repositories; attribution belongs at event granularity when evidence exists. | **Decided target from measurement.** Do not force an uncertain event into a session's primary repository. |
| Evaluation | Preserve configurable, grounded evaluation over stored/replayed sessions. | **Decided.** Taxonomy, aggregation, and backend remain open. |
| Kurrent assets/branding | Captures are internal evidence; protected marks and assets are replaced before external use. | **Decided boundary.** |

## Important open decisions

These are not gaps to paper over with a document or a mock. They need an
explicit choice, followed by working validation in the selected environment.

- Production API hosting, private/public network access, TLS/ingress, and
  ownership of the service.
- Production PostgreSQL operations: credentials, backup/restore, retention,
  monitoring, scaling, and cost ownership.
- Headless credential issuer, bearer-token semantics, authorization, audit,
  and machine/daemon registry behavior.
- The exact production schema/read-model path for multi-repository sessions,
  migrations from recovery schemas, and compatibility with clients.
- Fleet distribution/update mechanism for client and daemon binaries.
- Multi-user, project, visibility, and privacy semantics beyond the current
  one-operator fleet objective.
- The evaluation catalog, execution topology, and preferred local/free judge
  backends.
- Interactive agent execution, terminal, review, and Flow scope, including
  process ownership, authorization, consent, containment, and audit.

## How evidence becomes implementation

1. **Start with the strongest available source.** Inspect the client contract,
   raw vendor data, captured KCap output, or live target environment before
   inferring behavior from an old plan.
2. **Classify the result.** Record whether it is observed behavior, implemented
   code, a decision, an assumption, or an open question. Do not use “current”
   to blur those states.
3. **Encode behavior in a test.** For ingestion, a fixture and conformance test
   should establish the input, canonical events, order, accounting, replay,
   and projections. For operations, run the actual selected transport and
   database path. For a UI, render persisted data rather than a convenience
   mock.
4. **Make the narrowest durable change.** Preserve raw data, avoid inventing
   defaults where the source is absent, and fail visibly when an unsupported
   contract is reached.
5. **Update the current topic documentation.** State the new decision, its
   source, scope, and remaining open edge. Keep detailed raw artifacts in
   `reference/` or the historical archive rather than turning current docs into
   a chronological dump.

## What the historical corpus contributes

The archived corpus is not discarded subject matter. Its major topics are
carried into the current documentation as follows:

| Archived material | Durable content retained in current docs |
| --- | --- |
| `schema/` drafts and wire mappings | Canonical identity, raw provenance, watermarks, PostgreSQL direction, fleet fields, and read-model questions in [Architecture](architecture.md), [Ingestion](ingestion.md), and [Fleet and operations](fleet-and-operations.md). |
| `superpowers/plans/` and `superpowers/specs/` for hooks, import, profiles, spools, local IPC, and daemon lifecycle | Capture and supervision boundaries in [Agents and daemons](agents-and-daemons.md), plus fleet constraints. |
| ACP, reviewer, and vendor probes | The evidence/safety boundary for future interactive control in [Agents and daemons](agents-and-daemons.md); the raw protocol details remain archived for implementation work. |
| Desktop shell, consent, onboarding, terminal, chat, and rail plans | The actual desktop role and its data/control boundaries in [Desktop](desktop.md). |
| Change notes and dated implementation plans | Implemented-client context only; they do not determine a replacement backend or deployment. |
| Import/vendor observations and test plans | Fixture-first normalizer proof and remote PostgreSQL validation in [Ingestion](ingestion.md) and [Testing](testing.md). |
| Historical evaluation notes | The retained evaluation capability and open design space in [reference/EVAL-DIRECTION.md](../reference/EVAL-DIRECTION.md). |

This mapping is deliberately topical rather than a one-file summary of 197
documents. Detailed protocol transcripts, raw probe output, dated alternatives,
and superseded implementation mechanics remain available in the archive where
they can be rechecked instead of being silently paraphrased into false current
facts.

Their contribution is evidence and implementation context. Their dates,
service names, “shipped” statements, hosted URLs, and proposed sequencing may
be stale. When a historical document conflicts with a current decision above,
the current decision wins; when it conflicts with direct measurement, the
measurement wins until a new decision is recorded.

## Documentation maintenance rule

Current documentation is organized by stable system concern: vision, data
architecture/ingestion, web dashboard, fleet operations, agents/daemons,
desktop, testing, and decisions. It should explain the whole target shape and
where each claim came from—not merely the feature currently being edited.

Detailed observations belong in `reference/`; replaced plans and dated probes
belong in the historical archive. Neither should be silently discarded. When
a fact needs rechecking, say so directly and link to the source that made the
claim. This keeps the next implementation plan anchored in the actual future
state rather than in a previous agent's context.
