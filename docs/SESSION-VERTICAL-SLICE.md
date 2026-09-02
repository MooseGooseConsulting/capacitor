# Sessions vertical slice: source evidence to rendered corpus

## Purpose and boundary

Capacitor's first product slice is not an API, a parser, a database migration, or a
static dashboard. It is one real imported session whose retained source evidence is
enriched into a read model and rendered in the captured **Sessions** console. The
surface is the starting constraint because it tells us exactly which facts the system
must preserve and make legible.

This contract derives from:

- [`PROMPT.md`](../PROMPT.md): standalone stack, a real import, a rendered console,
  Transcript and Trace, retained evaluation execution, and fleet capture.
- [`reference/FLEET.md`](../reference/FLEET.md): a networked corpus, headless
  enrollment, offline durability, and a first-class machine dimension.
- [`reference/SURFACE.md`](../reference/SURFACE.md) and
  [`reference/ui-assets/`](../reference/ui-assets/): captured Sessions layout,
  card/detail fields, six URL-addressable tabs, and the visual system.
- [`reference/CROSS-REPO-SESSIONS.md`](../reference/CROSS-REPO-SESSIONS.md):
  event-level location and many-repository session attribution.
- [`reference/REPEATING-CALLBACK-AUDIT.md`](../reference/REPEATING-CALLBACK-AUDIT.md):
  idempotent lifecycle ingest and first-write-wins start facts.
- [`reference/EVAL-DIRECTION.md`](../reference/EVAL-DIRECTION.md): preserve the
  evaluation capability without freezing KCap's taxonomy.

The direct visual references are the captured
[light session list](../reference/ui-assets/screenshots/sessions-list-light.jpg),
[dark session list](../reference/ui-assets/screenshots/sessions-list-dark.jpg),
[Overview](../reference/ui-assets/screenshots/session-overview-dark.jpg),
[Transcript](../reference/ui-assets/screenshots/session-transcript-dark.jpg),
[Events](../reference/ui-assets/screenshots/session-events-dark.jpg),
[Trace](../reference/ui-assets/screenshots/session-trace-dark.jpg),
[Evaluation](../reference/ui-assets/screenshots/session-evaluation-empty-dark.jpg),
and [Details](../reference/ui-assets/screenshots/session-details-dark.jpg) screens.
The full capture also includes the other top-level navigation areas and the search
palette; their absence from the first data-backed slice is not permission to erase
them from the product shape.

`reference/WAVES.md` supplies the evidence-to-conformance method and its gates. Its
original parser-first, then-console sequence is not adopted as a product requirement:
this slice is deliberately data-to-browser, so the necessary store and normalization
work are built only in service of a visible Session.

No branch, pull request, fixture, or test database establishes a deployed Capacitor
API or web console. Before claiming an environment is live, take a fresh read-only
Blood Arrow inventory and verify the real client import/hook path plus a browser render
against the same persisted session. Blood Arrow PostgreSQL database `capacitor_test`
is the isolated recovery/test target for that proof, not a production Capacitor
service. An unmerged server or dashboard branch is not a substitute for the acceptance
path below.

## The product slice

The slice begins with a real, consented probe transcript and ends with a browser opening
that same session. It includes:

1. A durable receipt of source lines and lifecycle facts, safe to import again.
2. A normalizer that derives the displayed session, event, transcript, and trace facts
   while preserving the raw source needed to explain or reprocess them.
3. A Sessions list backed by the persisted projection, not an in-memory fixture or a
   handwritten `sessions` response.
4. A selected-session detail with its captured six-tab information architecture.
   Overview, Transcript, Events, Trace, and Details must distinguish populated facts
   from unavailable facts. Evaluation must show persisted results when they exist and
   an honest not-run/empty state when they do not; it must not imply that evaluation
   was cut.
5. Visual comparison against the captured list/rail and Overview, Transcript, Events,
   Trace, Evaluation, and Details screens. Use the captured layout, typography,
   tokens, component classes, icon meanings, light/dark behavior, and `?tab=` routes
   as the reference.

The top-level navigation — Sessions, Agents, Insights, Flows, and Work Items — remains
part of the observed product surface. This first slice makes Sessions real; it does
not silently delete, fake-complete, or redefine the other areas. Launching hosted
agents or a CLI from the web page is not in this slice and is not a proxy for corpus
capture or session enrichment.

## Console shell contract

The Sessions vertical slice lives inside the captured console shell; a bare
Sessions-only page does not satisfy the visual target. The browser acceptance must
include the following shell behavior:

- The header presents Sessions, Agents, Insights, Flows, and Work Items in the
  captured hierarchy, with their captured count/beta state only when it is backed by
  real data. A missing subsystem is visibly unavailable, not represented by a made-up
  count or silently removed navigation.
- The left repository rail, selected-session list rail, detail pane, trial/setup area,
  version footer, help/account affordances, and responsive light/dark tokens retain
  the captured structural relationship. Default MudBlazor layout is insufficient.
- Global search remains part of the shell. The observed search behavior is title and
  transcript matching, not vendor-name matching; the implementation must either
  reproduce that evidence or record a deliberate, tested divergence.
- The selected-session header includes the captured status and owner treatment plus
  share, copy-link, refresh, and delete affordances. An action not yet backed by the
  standalone system must be explicitly unavailable; it must not be inert decoration
  that looks successful.

The first data-backed route is Sessions. Agents, Insights, Flows, and Work Items may
remain unavailable while their underlying product work is unbuilt, but their absence
from this group does not cut them from the complete-surface decision required by the
brief.

## Read-model field contract

Every browser field below has a source and an honest unavailable state. A missing
source is not zero, an empty string, or invented sample data.

| Visible contract | Evidence source | Persisted/projection requirement |
|---|---|---|
| session identity, title, status, relative start | `SURFACE` session list and detail header | A stable machine-scoped recording identity; lifecycle status; first observed start; title provenance and update time. Agent-supplied session IDs are not assumed globally unique across fleet nodes. |
| work item, PR, and repository labels | `SURFACE`; wire repository metadata; cross-repo measurement | Preserve supplied associations. Project a primary repository for the captured rail by highest observed event count, but retain every observed repository and the event evidence that supports it. |
| vendor and model chips | `SURFACE`; client transcript payload | Preserve source vendor and model per event; session-level values are derived summaries, never a replacement for event values. |
| token flow, cache read/write, cost, context occupancy | `SURFACE` transcript/card/trace observations | Store values with source/provenance and roll up only when the underlying event semantics support the total. Unknown context/cost remains unavailable. |
| diff, tool count, error count, skills/badges | `SURFACE` card observations and analytics vocabulary | Keep raw inputs necessary to derive each enrichment. Expose an unavailable state until the relevant normalizer/projection exists. Do not substitute a zero count for unknown. |
| machine and owner | `FLEET` machine requirement; captured details/header | Machine is first-class and must be visible in Details and distinguishable from owner. The captured console has no machine treatment, so a machine label/filter is a deliberate Capacitor divergence rather than an invisible field. |
| transcript messages | `SURFACE` Transcript; raw vendor files | Preserve ordered source and normalized message/event linkage, including model, token/cache/cost, timestamp, and duration when supplied. |
| typed Events | `SURFACE` Events observation | Preserve event kind, content, tool input/output, error/exit context, timestamp, and the raw source reference. |
| Trace turns | `SURFACE` Trace observation | Produce a stable ordered turn projection with duration, token flow, tool count, and interleaved first-class non-turn rows. |
| evaluations | `EVAL-DIRECTION`; `SURFACE` Evaluation tab | Persist grounded evaluator runs/findings when present; show not-run when absent. The evaluator design is intentionally open, but the execution capability is not optional. |
| metadata and source provenance | `SURFACE` Details; fleet and wire references | Retain vendor, session linkage, machine, repository associations, ingest origin, timestamps, and enough raw-source linkage to explain the projection. |

### Data rules required by the contract

These are the minimum data-shape corrections that must precede a migration or public
read contract. They resolve the conflicts in the existing schema notes without
pretending an unimplemented DDL is a live system.

- **Fleet identity scopes a receipt.** An agent-supplied `session_id` is not proven
  globally unique across machines. The canonical recording identity and every
  transcript receipt therefore include `machine_id`; a receipt is idempotent at
  `(machine_id, session_id, agent_id, line_number)`. A public session route must use
  an unambiguous recording identifier, rather than silently selecting one of two
  machine collisions. Replacing this with a global-ID assumption requires a measured
  multi-machine proof and an enforced client/server invariant.
- **Receipt and normalized event are different things.** A received transcript line
  at that receipt key is the client resume contract. One line may yield zero, one, or
  several normalized/display events. The latter therefore need a stable in-line
  ordinal (`logical_seq`) and a reference back to the receipt. A watermark
  acknowledges the source receipt, not an arbitrary number of projected events.
- **Ordering is explicit.** The Events and Trace projections sort source-derived rows
  by their source position and `logical_seq`; lifecycle rows use a stable derived key
  and a documented ordering rule. Do not rely on database insertion order.
- **Repository evidence is plural.** Keep the finest available cwd/repository evidence
  on events and derive `session_repositories` plus a primary repository from it.
  Primary means highest observed event count, not launch cwd. A null source must
  remain null; it must not be backfilled from a session's launch cwd. The visible
  repository rail can use the primary projection while a session remains discoverable
  through every associated repository.
- **Lifecycle repeats are one fact, but child lifecycles are distinct.** A repeatable
  top-level session callback is identified by `(machine_id, vendor, session_id,
  lifecycle_kind)`, never by its changing timestamp or body shape. A subagent start
  or stop additionally includes `agent_id`, so siblings cannot collapse into one
  lifecycle fact. First observed start facts win; an absent value in a repeated
  callback never clears a known fact. Inventory and platform on those callbacks are
  machine facts, so they upsert the machine record by `machine_id` rather than append
  to a session. Recovery can replay hundreds of valid duplicate lifecycle posts: rate
  handling must tolerate that burst, and a 429 response must carry `Retry-After` so
  the client can spool and retry rather than loop permanently.
- **Raw source survives enrichment.** Keep enough original payload and provenance to
  compare normalization with the oracle and to reprocess after a normalizer changes.
  Derived fields carry their source/normalizer version where useful; no normalizer is
  allowed to make its output the only surviving evidence.

## Browser contract by page region

| Region | Must be backed by the slice | Deliberate limit of this slice |
|---|---|---|
| left rail | All/My projects/Other repos grouping can be projected from persisted repository associations; setup/version elements remain visibly distinct from corpus data | No invented fleet-health claim or setup completion based on fixtures. |
| Sessions list | Card title, status, associations, vendor/model, available enrichments, timestamp, and selected-session state come from the database-backed query | Do not replace unavailable enrichment with fabricated metrics. |
| Overview | Session summary and currently known rollups/provenance | An unavailable recap/classification must say so. |
| Transcript | Real normalized transcript linked to retained source evidence | No placeholder prose passed off as captured conversation. |
| Events | Real typed events, including structured tool input/output when preserved | Do not collapse Events into a transcript-only view. |
| Trace | Real ordered turn and non-turn rows | Do not derive turn numbering from render order or omit interleaved events. |
| Evaluation | Persisted evaluator results or an honest no-results state | No claim that the core capability has been excluded. |
| Details | Identity, lifecycle, vendor, machine, repository association, and ingest/source metadata | The machine placement may diverge from the capture, but the distinction must be visible. |

## Implementation and acceptance sequence

This is one vertical slice, divided into landable implementation groups rather than
separate parser, API, and dashboard achievements:

1. **Field ledger and probe.** Choose a real session for which raw source and live
   oracle output can both be inspected. Record each field in the tables above as
   observed, derived, deliberately divergent, or unavailable. Resolve ambiguity by
   measurement, not by endpoint convenience.
2. **Actual client receipt, normalization, and projection.** Use the inherited
   client's real `import` path and real hook/wire payloads against the isolated Blood
   Arrow `capacitor_test` target; never load rows by direct SQL or a bespoke importer.
   Exercise transcript receipt/resume semantics, lifecycle payloads, and the client
   contract's `last-line` behavior. Assert re-import idempotency, transcript-before-
   session-start reconciliation, lifecycle repeat behavior, explicit ordering,
   repository plurality, and machine attribution. This runs remotely against the
   target PostgreSQL service; local mock databases are not acceptance evidence.
3. **Browser-connected Sessions surface.** Serve the actual web application against
   the same read model, render the persisted probe session, and exercise list
   selection plus each detail route. Contract/fixture tests may assist development,
   but a stubbed search response or an unconnected page cannot pass this group.
4. **Remote vertical-slice gate.** In the Blood Arrow environment, verify the
   inherited client's real import/hook path through the selected database and open
   the browser view of that same session.
   Compare the list/rail and Overview, Transcript, Events, Trace, Evaluation, and
   Details routes with the captured visual evidence, including card fields, machine
   distinction, and unavailable-state handling. Preserve the output necessary to
   investigate a mismatch; do not call the slice complete because an API test or
   isolated UI test passes.

## Fleet path attached to the slice

Fleet requirements are not a later infrastructure polish item. The first visual slice
uses one machine to make the field contract inspectable; the path to the fleet
acceptance is concrete and remains attached to this work:

1. Enroll a test machine through the inherited client-credentials path, using the
   standalone token exchange rather than a Kurrent-hosted default. The server must
   carry that identity on a session and expose the required machine and daemon
   management surface (`/api/admin/machines` and `/api/daemons`) without treating it
   as agent-launch functionality. The remote contract run covers the inherited
   minimum sink as a whole: session/subagent lifecycle hooks, transcript receipt,
   `last-line` resume, auth configuration/refresh, and the client-credentials token
   exchange; it does not replace those with a bespoke import endpoint.
2. Verify the remote import/hook flow with that machine credential and the actual
   client wire contract. A browser Details view and list/rail filtering must make the
   resulting machine distinguishable from its owner and repository associations.
3. Enroll a second machine headlessly, deliberately exercise offline spool-and-drain,
   and confirm that its delayed capture lands in the same corpus without duplicate or
   lost source receipts. The browser must distinguish both machines' sessions.

The first group is not complete merely because it renders a one-machine fixture. The
third step is the `PROMPT.md` and `FLEET.md` fleet acceptance gate; no local database,
direct SQL load, or single-node browser demo can replace it.

The later complete-surface feature cut remains the operator confirmation point described
in `WAVES.md`. Until then, no inherited agent-launch, flow, analytics, work-item, or
hosted-service feature is silently promoted to the primary milestone.
