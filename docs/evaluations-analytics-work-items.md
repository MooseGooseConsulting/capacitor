# Evaluations, analytics, and work items

## Purpose

Capacitor is intended to learn from its captured session corpus, not merely store transcripts. Evaluation, analytics, and work-item correlation are three different lenses over the same evidence:

- **evaluation** runs a versioned question, scorer, or comparison over a session/replay and persists a grounded observation;
- **analytics** exposes curated, governed read models for aggregate questions; and
- **work items** connect sessions and evidence to delivery structure without pretending attribution is always automatic or singular.

They depend on canonical events and provenance. None invents facts missing from events, bypasses repository/machine scope, or makes static documentation appear to be a live analysis result.

## Evaluation is retained core capability

The decision is to keep the evaluation execution primitive. Capacitor needs to run evaluators over captured or replayed sessions, ground them in recorded evidence, persist observations, and compare them later for corpus learning and counterfactual work.

What is deliberately **not** fixed is the inherited product's question taxonomy, aggregate score, evaluator catalog, orchestration, vendor, or paid-model dependency. Historic "13 questions" and a five-point aggregate are useful compatibility evidence, not the design target.

| Shape | Use |
| --- | --- |
| Single-purpose evaluator | One narrow question per fresh invocation, avoiding cross-objective distraction. |
| Multi-question evaluator | A bounded group of related questions where shared context materially helps. |
| Deterministic scorer/check | A measured property with no LLM required. |
| Pairwise/blind comparison | Compare replay arms without exposing treatment identity. |
| Specialized evaluator pack | Investigation-specific, versioned questions and scorers. |
| Multiple models/repeated judges | Calibration, disagreement, robustness, or regression comparison. |

Every run retains evaluator definition/version, question/scorer, input session/replay identity, evidence scope, backend/model identity, execution time, result/status, per-question findings/verdicts, and aggregation method where one exists. Unavailable or failed runs remain distinguishable from clean results and from work never scheduled.

### Evidence access and safety

An evaluator can receive a compact trace when it fits, or use a read-only session-scoped evidence interface when it does not. That interface exposes only the needed artifacts—summary, search, transcript, turns, errors, recap, captured tool results—not filesystem, network, or unrelated corpus authority. Evaluators do not automatically run over every session, and a result is never written without preserving enough evidence/configuration to interpret it later.

Free hosted and local models are first-class intended judge paths; a paid provider is optional configuration, not an architectural dependency. Record cost/usage when supplied, but lack of vendor cost data does not invalidate an evaluation.

## Governed analytics

Analytics is a read-only, query-governed interface to curated views. It serves the web console, an Insights assistant, and agent-facing tools through the same scope and allowlist rules:

- `SELECT` only, against allowlisted views and columns;
- repository scope by default and explicit global scope only where authorized;
- row caps and bounded result shapes;
- explicit validation errors rather than ungoverned SQL fallback;
- schema discovery before a client writes a query; and
- query/result provenance sufficient to explain an Insight's numbers.

The historic surface inventories 32 conceptual views. This inventory is a roadmap, not permission to create empty names. A view is introduced only when source data exists and its grain/semantics are defined.

| Domain | Target view vocabulary |
| --- | --- |
| Sessions | `v_an_sessions`, `v_an_session_steps`, `v_an_context`, `v_an_cost`, `v_an_token_usage_by_model`, `v_an_tool_usage`, `v_an_skill_usage`, `v_an_subagent_runs`, `v_an_memory_ops`, `v_an_incident_signals` |
| Code | `v_an_code_changes`, `v_an_file_changes`, `v_an_commits` |
| Pull requests | `v_an_prs`, `v_an_pr_sessions`, `v_an_pr_churn`, `v_an_pr_churn_summary`, `v_an_pr_test_runs` |
| Work | `v_an_work_items`, `v_an_work_item_sessions`, `v_an_work_item_links`, `v_an_work_item_milestones` |
| Evaluations | `v_an_eval_scores`, `v_an_eval_summaries` |
| Deployments | `v_an_deployments`, `v_an_deployment_coverage`, `v_an_deployment_status_uncertainties`, `v_an_release_publications` |
| Organization/repository | `v_an_users`, `v_an_repositories`, `v_an_team_memberships`, `v_an_user_primary_team` |

Start with grounded session/event views, then add domains with their base tables and ingestion. Columns need documented grain: a per-session tool view is not interchangeable with a repository-wide tool rollup. A view promising latency or deployment coverage without recorded source data is a documentation defect, not a feature.

### Canonical corrections

Two inherited assumptions are rejected:

1. **Machine is not optional.** `machine_id` belongs on a session and in relevant analytics views. A fleet corpus cannot reconstruct it later.
2. **One repository is not enough.** Events/turns carry the finest repository/cwd evidence available; `session_repositories` gives plural association with derived primary. `v_an_sessions.repo_hash` may remain a primary compatibility field, but per-repository cost, tokens, errors, PR attribution, and memory scope respect the plural model.

Missing source data remains null. Filling it from a session's primary repository manufactures false attribution, particularly for sources whose tools work across checkouts.

## MCP corpus query and durable memory

`PROMPT.md` requires an MCP **query** surface over the corpus. That makes
read-only, scope-governed session query a target capability after the canonical
read models exist; it does not select every observed MCP tool or authorize a
mutating control plane. The first contract must expose only documented schema
and bounded reads over sessions, transcript, turns, errors, recap, evaluation,
and governed analytics. Its caller identity, repository/machine scope, result
provenance, and rate/shape limits are part of the contract, not optional
middleware.

Durable user or agent memory is different. The inherited surface's memory
routes and `v_an_memory_ops` view are evidence that such a feature existed,
not a Capacitor requirement that has already been selected. Until the feature
cut decides its retention, consent, scope, provenance, and deletion behavior,
the corpus stores only captured source evidence and does not invent a separate
memory store or API. MCP writes, memory writes, and control actions remain out
of scope for the initial query surface.

## Work-item model

Work items are durable correlation records, not a replacement for every external tracker. A work item may attach to a session/continuation chain by issue key, PR number, existing ID, or explicitly created title. The record preserves external reference, repository, title/status snapshot, link provenance, and timestamp rather than resolving a mutable external system on every read.

The target topology supports:

- session-to-work-item links, many on both sides;
- parent-to-parts breakdown, with at most one parent per part;
- explicit `blocks` and `blocked_by` relations between items in the same repository;
- provenance-preserving retraction of a declared breakdown/relation;
- milestones and observed status; and
- queryable work-item/session/link/milestone read models.

These are distinct relations. A session link is not a dependency; a parent/part edge is not a `blocks` edge; a PR association is not proof a work item is complete. Manual declaration is first-class for structure an importer cannot know. Inference may be useful later, but UI identifies it as inferred and allows a durable explicit correction.

The Work Items console view shows evidence links, sessions, parts, dependency graph, milestones, and uncertainty. It does not mark completion because a session ended or a Flow returned `clean`.

## Cross-surface behavior

| Surface | Evaluation | Analytics | Work items |
| --- | --- | --- | --- |
| Session detail | Render persisted runs/verdicts/evidence; state unavailable honestly. | Show metrics derived from recorded events. | Show attached items, not speculative ownership. |
| Insights | Explain quality/trends with query and scope. | Query governed views and label time/repository/machine scope. | Aggregate topology only with link provenance. |
| Agent-facing MCP/API | Start only through explicit execution contract; read retained results. | Retrieve schema, then issue governed read-only queries. | Declare/read/retract typed links and topology. |
| Flows | May attach review evidence but stays distinct from evaluator run. | May report modeled Flow status/cost. | Uses stable target without mutating completion implicitly. |

## Delivery order

1. Complete canonical session/event persistence, logical ordering, provenance, machine identity, and plural repository evidence.
2. Publish sessions/turns/evaluation API and a read-first console that renders absence correctly.
3. Define and implement analytics views only where data exists; validate scope/governance/query semantics against the actual backend.
4. Add explicit work-item declaration/topology and read models.
5. Add evaluator execution, packs, replay/comparison work, and Insights over the resulting data.
6. Integrate Flow/review results as additional provenance, never as a shortcut around the corpus model.

This order prevents disconnected product shells with no reliable evidence; it does not reduce the importance of evaluation or work items.

## Sources and decision record

The maintained evaluation decision is [evaluation direction](../reference/EVAL-DIRECTION.md). Supporting material is retained in the [analytics views specification](history/pre-recovery/schema/ANALYTICS-VIEWS-SPEC.md), [surface map](../reference/SURFACE.md), [fleet objective](../reference/FLEET.md), [cross-repository measurement](../reference/CROSS-REPO-SESSIONS.md), and [historical work-item/analytics interface](../reference/VENDOR-README.md). These are provenance, not claims about a live Capacitor service.

When a decision changes an evaluator pack, analytics view, work-item relation, or retention policy, record it alongside implementation contract and tests. Do not replace this synthesis with an unexamined copy of a historic hosted schema.
