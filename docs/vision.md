# Capacitor vision and delivery direction

Capacitor is a self-contained system for collecting AI coding-agent work from
every machine in the operator's fleet, turning it into an evidence-preserving
session corpus, and making that corpus useful through a web console, query
interfaces, and local tools. The client code was inherited from Kurrent's
source-available CLI; the server that interprets and serves the data is ours
to build. The reason to own that server is concrete: a client can discover a
new agent's transcript format, but without an owned normalizer and store it
cannot make that data usable.

This document is the durable product direction. It does not claim that every
part of the direction exists today. For the evidence and decisions behind it,
see [Evidence and decisions](evidence-and-decisions.md).

## Status language

The repository contains three different kinds of material. They must not be
confused when planning or describing the system.

| Label | Meaning |
| --- | --- |
| **Observed** | Captured behavior, raw data, or a measured finding. It is a reference point, not automatically a product requirement. |
| **Implemented** | Source code in this repository. It still needs an appropriate test before it is called working in a target environment. |
| **Target** | A committed direction for the system we are building. It is not a claim of a deployed service. |
| **Open** | A design or operational choice that still needs an explicit decision or a measurement. |

There is currently no deployed alpha service, public ingress, production API
host, or production data store. Blood Arrow's PostgreSQL recovery cluster is
an integration-test target, not an alpha environment or an implied production
deployment.

## The target system

The durable shape is a single corpus shared by many machines rather than a
personal session viewer tied to one laptop.

```text
agent files and live hooks on each machine
  -> inherited CLI / watcher / daemon capture and local durable spools
  -> authenticated network ingestion service
  -> raw records + normalized canonical events in PostgreSQL
  -> durable session, repository, fleet, and evaluation projections
  -> web Sessions console, MCP/query interfaces, and local desktop tools
```

The first useful path is intentionally narrower than the whole product:

1. Capture historical and live transcripts without losing source evidence.
2. Normalize them into ordered events and projections with correct replay
   behavior.
3. Persist and read that data from PostgreSQL.
4. Render real session, transcript, event, trace, and evaluation data in the
   web Sessions experience.

That ordering reflects the operator's current priority: backend enrichment and
the session/transcript experience come before remote agent launching, terminal
control, Flows, and Work Items. Those are not deleted from the future shape;
they simply must not displace a functioning corpus.

## Non-negotiable outcomes

### An evidence-preserving corpus

The system stores raw vendor material alongside normalized facts. A normalized
event can therefore be traced to its vendor, session, agent, source line, and
raw payload. One source line may produce several canonical events, so the
event identity is `(session_id, agent_id, line_number, logical_seq)`. Replaying
the same data must neither duplicate events nor inflate session totals.

The watermark describes a contiguous processed prefix, not merely the highest
line number observed. Persisting events and advancing that watermark is one
transaction. A transcript that arrives before session-start creates a
reconcilable placeholder rather than being dropped.

### A fleet corpus, not a laptop corpus

Every participating machine must be able to record to one corpus, including
headless nodes. Sessions need a first-class `machine_id`; paths, clock values,
vendor availability, and daemon identity are all node-specific. Offline
capture must retain data locally and drain it later rather than treating a
brief network outage as data loss.

### A data-backed console

The target web console is a Blazor and MudBlazor Sessions experience grounded
in the captured [surface reference](../reference/SURFACE.md). Its list and six
detail views—Overview, Transcript, Events, Trace, Evaluation, and Details—read
from persisted projections. A live update is visible only after persistence;
the browser does not become a second, inconsistent event store.

### An extensible normalizer plane

The durable acceptance criterion is not a hard-coded list of inherited
vendors. It is that a new agent format can be added through discovery,
normalization, ingestion, and rendering without a closed-server dependency.
Each claimed vendor needs a real fixture and an idempotent end-to-end test;
mere registration in a catalog is not support.

### Evaluation remains a core capability

Capacitor must be able to run versioned, evidence-grounded evaluators over
captured or replayed sessions and persist their findings. The inherited
taxonomy, aggregate score, model provider, and execution topology are not
frozen. Free hosted and local backends are first-class candidates; paid judges
are optional. The retained capability is described in
[the evaluation direction](../reference/EVAL-DIRECTION.md).

## What the source already provides

The inherited client is substantial working material, not a blank starting
point. It contains vendor discovery and import sources, live hook integration,
spooling and replay logic, a long-running daemon, local control IPC, MCP
surfaces, and an Avalonia desktop application. It also exposes the wire shape
the missing service must honor. The source is a contract and an implementation
asset; it is not proof that a replacement server is available.

The repository also has implemented SQLite-oriented server libraries,
canonical records, migrations, and normalizer code. PostgreSQL persistence and
the API host are recovery work being validated separately before they can be
called the current runtime. SQLite in those libraries is not the selected
shared backend.

## Detaching the inherited hosted service

The inherited client contains hosted-service couplings that cannot become
Capacitor defaults by accident. Their target dispositions are explicit:

| Coupling | Disposition |
| --- | --- |
| Kurrent telemetry / PostHog endpoint | **Cut from the target.** Capacitor does not report to the inherited hosted service. Its own observability is a separate, privacy-scoped decision. |
| Bare profile expansion to a hosted Kurrent URL and tenant/signup provisioning | **Cut from the target.** No Capacitor profile may silently expand to the inherited host or provision its tenant. |
| WorkOS user authentication and the unauthenticated profile escape hatch | **Replacement required.** Fleet machine credentials and interactive-user authorization are different concerns; neither inherited path decides the replacement. |
| Inherited npm update check/channel | **Cut as target transport.** Fleet distribution is required, but its secure update/distribution mechanism is open and must not poll the inherited channel. |
| Hosted feedback submission | **Cut from the target pending an explicitly selected replacement.** It must not send corpus or operator data to the inherited service. |

This detachment does not remove the corresponding product questions. It makes
their authority and operating model ours to decide under the feature-cut and
fleet requirements, rather than inheriting them through a configuration default.

## Delivery direction

The work should progress through demonstrable vertical slices, revising the
sequence when the corpus disproves an assumption:

1. **Data foundation.** Prove real source records can be accepted, normalized,
   stored, replayed, and read from the isolated PostgreSQL test database.
2. **Sessions experience.** Make the web Sessions list and all six detail
   tabs accurate for persisted data, with Transcript, Events, and Trace
   carrying the strongest fidelity burden.
3. **Fleet foundation.** Add network reachability, headless credentials,
   machine and daemon registration/health, and a second node recording into
   the same corpus.
4. **Corpus breadth.** Verify multiple existing vendor formats against
   observed source/output pairs, then add a new vendor to prove normalizers are
   truly extensible.
5. **Analysis and evaluation.** Build governed read models, query surfaces,
   and configurable evaluation over the retained evidence.
6. **Interactive operations.** Decide and implement launch, terminal, hosted
   agent, review-flow, Flow, and Work Item capabilities only when their server
   contracts, safety boundaries, and operational value are established.

This is a direction, not a promise that each numbered item is complete or that
the sequence cannot change. The proposed historical waves remain useful
context, but their ordering was explicitly a hypothesis, not authority.
The complete feature-cut procedure and the present disposition of MCP, memory,
analytics, Work Items, Flows, and interactive control are in [Evidence and
decisions](evidence-and-decisions.md); their presence in this direction is not
an approval to build them yet.

## Boundaries and exclusions

- Capacitor does not depend on Kurrent's hosted service for its target stack.
- The captured Kurrent UI, marks, favicons, Solina typeface, and hosted-service
  behavior are evidence only. MudBlazor is separately reusable; protected
  branding must be replaced before external display.
- The recovery PostgreSQL database is only for integration testing. It is not
  a production deployment decision.
- Dashboard controls must not imply a server mutation that has not been
  implemented. The same rule applies to desktop controls.
- No historical design document becomes a present commitment merely because it
  was preserved. The sources of authority and how to resolve a conflict are in
  [Evidence and decisions](evidence-and-decisions.md).
