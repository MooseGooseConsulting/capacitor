# Web console

## Purpose and status

The web console is the shared, read-first view of the Capacitor corpus. Its primary job is to let a person move from a repository or work item to captured session evidence: transcript, typed events, turns, evaluation findings, and provenance.

This document records the **target product surface**, not a claim that a hosted console, agent launcher, Flow service, or work-item service exists today. The recovered server and web code are implementation work in progress; current availability must be established from the checkout, deployment manifests, and a live health check. The historic Kurrent tenant and screenshots are evidence for product behavior, not a Capacitor runtime to depend on.

The durable delivery order is capture and persistence first; a navigable web evidence surface second; analysis, evaluation, work-item correlation, and fleet views third; and mutation-heavy controls such as interactive launch, terminals, and Flow control last.

The captured UI is a Blazor + MudBlazor reference. MudBlazor supplies generic tabs, chips, cards, drawers, tables, and dialogs; Capacitor owns the data contracts and product semantics. Do not copy Kurrent branding, proprietary typefaces, or marks into a public Capacitor surface. The captured asset inventory identifies replacements that will be needed.

## Desktop and web are different surfaces

| Surface | Owns | Does not own |
| --- | --- | --- |
| **Desktop app** | A local daemon's status, local repository/harness choice, locally attached interactive sessions, and any local terminal/permission interaction. | The organization-wide corpus or the authoritative data model. |
| **Web console** | Persisted sessions across repositories and machines; links to work, evaluations, fleet health, and eventually Flow state. | An implicit local terminal or direct control of a user's machine. |

The web console must remain useful when a producer machine is offline, when a session was imported rather than watched live, or when no interactive daemon is connected. A local control must never be rendered as a corpus-wide capability merely because a desktop action has a similar name.

## Information architecture

The target navigation is **Sessions**, **Agents**, **Insights**, **Flows**, and **Work Items**, plus global search, help, account identity, onboarding, and version state.

| Area | User question | Backing data / status |
| --- | --- | --- |
| **Sessions** | What happened in this session, repository, time window, or continuation chain? | First delivery priority. Persisted session, event, turn, and evaluation data. |
| **Agents** | Which hosted or managed agents are active, and where? | Target fleet/daemon view. Distinguish reported status from a controllable local process. |
| **Insights** | What patterns, cost, quality, or delivery questions does the corpus answer? | Target analysis experience over governed read models; never fabricated summaries. |
| **Flows** | What review or multi-participant work is running, waiting, completed, or failed? | Target orchestration read model. Rendering state is separate from launching or mutating a Flow. |
| **Work Items** | Which sessions, parts, dependencies, PRs, and milestones belong to this work? | Target correlation/topology view, backed by explicit records. |

Global search must identify its matching field and scope. Captured behavior indicates title/transcript search is not a vendor filter. The left rail's **All**, **My projects**, and **Other repos** grouping is a target convention. "My" requires a real viewer/repository ownership model; it must not be inferred from session owner or a Git remote. Until such a model exists, list reachable repositories honestly and say viewer-specific grouping is unavailable.

## Sessions

### List and filtering

The sessions page is the primary entry point. It needs repository selection and cards that show, when recorded: status and time; title and work item; repository and PR; vendor and model; token/cache/cost flow; context occupancy; diff, tool, error, and marker counts.

Those fields have different provenance. A normalizer preserves a missing vendor field as missing; the UI must not render zero values as if the vendor reported them. A card is a summary of the event stream, not a second source of truth.

Filters need restorable URL or equivalent state, pagination, and cancellation/versioning so stale concurrent searches cannot overwrite newer results. Repository filtering must respect the plural session model in [cross-repository sessions](../reference/CROSS-REPO-SESSIONS.md): a session that materially touched a repository must not disappear because another repository is primary attribution.

### Detail: six evidence tabs

Every session detail has exactly these six conceptual tabs. Tabs may be query-addressable so a shared link retains the selected evidence.

| Tab | Reader purpose | Required source |
| --- | --- | --- |
| **Overview** | Outcome/status, repositories, time bounds, owner/machine, vendor/model, links, and labelled rollups. | Session record and clearly identified derived data. |
| **Transcript** | Ordered conversation, model, token/cache/cost, time, duration, and captured tool input/output. | Ordered canonical events; preserve subagent identity. |
| **Events** | Typed append-only record rather than a presentation summary. | Event type, agent, logical sequence, timestamp, payload/attributes, and allowed provenance. |
| **Trace** | Turn number, start, duration, token flow, tool count, with non-turn lifecycle/message rows interleaved. | Composed read model over the same events. |
| **Evaluation** | Runs, verdicts, findings/evidence, model/backend, unavailable/failed state. | Persisted evaluation records; absence is explicit. |
| **Details** | Import/live origin, chain links, visibility, repositories, machine, and source identifiers. | Session/provenance metadata. |

Header actions can copy/share a link and refresh. Deletion is not part of a read-first recovery build: it requires retention policy, authorization, and audit before it is enabled. All timestamps need an explicit timezone; server-local time is not enough for a fleet corpus.

### Canonical evidence rules

- Event identity includes session, agent, and logical sequence; a line number alone is not globally unique.
- Turns are a read model. Session start, user messages, and background-command completion remain first-class events.
- Session chains, subagent hierarchy, and Flow participants are distinct relationships.
- Event-level repository/cwd evidence is more accurate than one session path. A session has one derived primary repository and may have many associated repositories.
- Machine identity is first-class. The inherited analytics model lacked it; Capacitor must not repeat that omission.

## Insights

Insights is analysis over governed persisted data. Its target categories are Adoption & Utilization, Cost/Attribution/Allocation, Productivity & Impact, Agent Behavior & Observability, Delivery & SDLC, and Evaluation & Quality. The exact charts, assistant, and taxonomy are not fixed by the captured UI.

The non-negotiable contract is that an answer is grounded in a recorded query against curated read models, identifies scope and time range, and reports when the corpus cannot answer. An Insights assistant, if introduced, is a client of the same governed query surface as the charts; it never receives unrestricted database access.

## Agents

Agents is a fleet and daemon status view, not a promise that the web page can launch or type into any process. Target rows include daemon/machine identity, connection and last-seen state, active agent/session, vendor, repository/worktree context, transport family, and whether the row is observed, locally controllable, or unavailable.

The desktop app remains the natural home for a local terminal/attach. A future web action must declare its command path, authorization, permission behavior, target daemon, and worktree policy. It must fail visibly rather than silently switching machine or vendor.

## Flows and Work Items

Flows render persisted review and multi-participant records: definition, target, participants and roles, workspace mode, vendor selection, rounds, messages, results, budget state, and terminal status. Their full contract is in [flows and review](flows-and-review.md). The console must render a Flow created elsewhere and must not assume a browser tab owns its lifecycle.

Work Items joins evidence to delivery structure: issue/PR/external key, sessions and chains, parent/part breakdown, `blocks`/`blocked_by` relations, milestones, and status. The page distinguishes declared from inferred links. The durable model is in [evaluations, analytics, and work items](evaluations-analytics-work-items.md).

## Data boundary and sources

```text
vendor transcript / lifecycle
  -> normalizer and idempotent event store
  -> session, turn, repository, machine, evaluation, and work-item read models
  -> API contract
  -> web console and desktop status surfaces
```

Live updates improve freshness but do not replace persisted state. Initial load, reconnect, pagination, and replay must converge on the same stored facts.

This synthesis is grounded in the [surface map](../reference/SURFACE.md), [captured assets](../reference/ui-assets/), [fleet objective](../reference/FLEET.md), [cross-repository findings](../reference/CROSS-REPO-SESSIONS.md), and the retained [historical designs](history/pre-recovery/). Archived material is provenance, not a declaration that those plans shipped. When this document changes, update the underlying decision/contract and then the relevant API/UI tests; screenshots are visual acceptance aids, never proof of backend behavior.
