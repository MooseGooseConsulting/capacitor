# Flows and review

## Role in the product

Flows are the target orchestration layer for structured work such as independent spec review, code review, and multi-participant agent work. They are valuable, but are not the first Capacitor dependency: a Flow without a durable session corpus cannot ground a review, explain a finding, or retain the work it initiated.

This document separates three facts historic materials often combine:

- the inherited client contains commands, MCP registration, and daemon designs for agent/Flow interaction;
- the target product retains the option to run structured reviews and agent flows; and
- no statement here asserts a current Capacitor backend, hosted Flow service, browser launch control, or paid reviewer path is deployed.

The current product sequence remains capture and evidence first. A Flow surface can render stored state as soon as a backend models it; launching a new Flow is a separate privileged command with authorization, capacity, vendor, cost, and containment conditions.

## Flow model

A durable Flow run needs more than a chat transcript.

| Concept | Required meaning |
| --- | --- |
| **Definition** | Versioned catalog definition or explicitly captured inline definition; the initiating client cannot silently rewrite it. |
| **Target** | Kind, stable reference, title, repository context, and session/work-item/PR evidence the run concerns. |
| **Participants** | Named roles, requested/applied vendor/model, lifecycle identity, and interactive versus unattended class. |
| **Workspace decision** | Owned worktree, borrowed/snapshot context, context-only, or unknown; preserve the reason and evidence. |
| **Rounds** | Per-role prompt, round identity, timestamps, status, results, messages, and terminal cause. |
| **Governance** | Limits, budget policy, MCP allowlist, credential/permission posture, and reviewer-selection source. |
| **Result** | Structured findings/clean result, evidence links, completion/failure/close state, and retryable/transient conditions. |

Flows have an explicit lifecycle: created, running or waiting, completed or failed, then closed. A multi-participant run can exist before a participant launches; roles launch only when protocol work is sent. The UI must distinguish not-yet-started, waiting, and failed rather than collapsing them into inactive.

The Flow page is a read model over these records. It shows target, roles, rounds, workspace disclosure, result/evidence, and meaningful terminal reason. It can navigate to backing sessions, work items, and review artifacts. It does not imply a browser owns or can control a local terminal.

## Review flows

Spec and code review are reserved uses of the generic Flow model, not another incompatible orchestration system. A review records the artifact/version and repository context; reviewer vendor/model and selection source; workspace mode actually applied; visibility of uncommitted content; structured conclusion and evidence; and constraints such as unavailable vendor, unsupported model override, capacity, settlement, or timeout.

An agent can offer an independent review when a design is finalized or implementation is complete, but must ask before starting it. Ordinary self-review, a partial draft, active failing checks, or an already active/completed review are not automatic-flow triggers. The offer must not claim a distinct reviewer when it could not establish the driving vendor or available reviewer set.

No Flow silently substitutes vendor or model. A model override is resolved only by that vendor's runtime resolver; a global vendor-to-model lookup would be false authority. Dynamic definitions pin participants rather than accepting a misleading global override.

## Workspace and containment disclosure

Review quality depends on what a reviewer actually saw. Persist and display one of:

- **owned worktree** — daemon-owned checkout/worktree;
- **borrowed or snapshot context** — current tracked, dirty, or untracked content under an explicitly supported containment boundary;
- **context-only/fallback** — no local tree was supplied; or
- **unknown** — no reliable workspace decision was disclosed.

`unknown` means do not assume uncommitted work was reviewed. A fallback reason is diagnostic data, not a cosmetic label. The client must never promote best-effort fallback to equivalent review coverage.

Containment is vendor and platform-specific. Calling an agent's working directory a worktree does not make it safe. The Flow definition chooses an allowed measured mode; daemon and OS isolation policy enforce it. A remote/expensive review never becomes an unannounced fallback for a local request.

## Interaction and safety boundaries

Flows contain two agent classes:

- **interactive/default agents**, which may have a person and permission surface; and
- **unattended participants/reviewers**, whose rounds cannot wait for a human terminal or permission response.

An unattended participant gets only tools and MCP servers allowed by its definition. A Flow-starting server is never injected into the participant, preventing accidental nested paid work. Results use a structured protocol operation; a transcript phrase such as "looks good" is not proof that a review was submitted.

Local agent control recognizes Flow participants. Attach is read-only, so raw stdin cannot inject an undocumented turn and viewers cannot resize a participant PTY. Stop refuses a protected participant without explicit force; bulk stop reports skipped protected rows. This is daemon policy, not a CLI hint.

Flow-layer failure handling owns unattended reviewer death. Runtime ACP reconnect never transparently resumes a review participant in a way that hides failure from the round protocol. Retained results make failure and retry visible.

## Agent-facing API shape

The historic MCP shape is a useful target, subject to implementation and authorization.

| Operation | Contract |
| --- | --- |
| Start | Select exactly one catalog or inline definition; capture target, context, participants, limits, and requested selection; return a stable Flow run ID. |
| Send to participant | Address a declared, non-busy role with a new round; preserve round identity and message idempotency. |
| Status | Return persisted state and last result. A bounded wait is convenience, not a permanently held request. |
| Close | Close a terminal run without erasing history. |
| Submit result/message | Participants submit structured findings/clean results and optional out-of-band messages through a separately scoped capability. |

All retries need an idempotency boundary. Only server-declared transient settlement/participant errors are retried, bounded by elapsed time. A generic error is not permission to duplicate a start or round. At-least-once messages need a message-ID deduplication rule.

## Web, desktop, and CLI roles

| Surface | Appropriate Flow behavior |
| --- | --- |
| **Web console** | Inspect persisted Flow state; link results to sessions/work items/PRs; disclose workspace/vendor/budget/status; expose launch only after command path and authorization exist. |
| **Desktop app / daemon** | Determine local vendor availability, own local worktrees/processes, enforce participant protection/platform containment, and report actual runtime state. |
| **CLI / MCP** | Request a Flow through typed, consent-aware API; provide result/status tools without granting a reviewer authority to start nested work. |

The Flow page is not an agent launcher. Until a launch affordance states target daemon, worktree/context, approval/cost policy, and failure reporting, it remains disabled or absent. Rendering historical Flow records requires none of those privileges.

## Relationship to evaluation and work items

Flow results are evidence attached to sessions, PRs, or work items. They are not evaluations by default: an evaluation is a versioned evaluator run over corpus evidence, while a Flow is an orchestrated participant process. Either can produce findings; their provenance, model, scope, and repeatability remain distinct.

Work items can supply a Flow target and topology. A Flow retains the work-item reference used at start rather than resolving a mutable title later. Completing a Flow never silently marks a work item done.

## Sources and future decisions

This synthesis retains the historical Flow work: [captured Flow/MCP behavior](../reference/VENDOR-README.md), [Flow-aware agent commands](history/pre-recovery/superpowers/specs/2026-07-29-ai1557-flow-participant-aware-agent-commands-design.md), [agent command group](history/pre-recovery/superpowers/specs/2026-07-28-ai1555-agent-command-group-design.md), [review-suggestion evaluation](history/pre-recovery/eval/suggest-review-flow.md), and [ACP reconnect design](history/pre-recovery/superpowers/specs/2026-08-04-ai1325-acp-reconnect-resume-design.md). These are interface evidence, not claims about a live Capacitor service.

Before launch is implemented, settle deployment/auth, the vendor/platform containment matrix, cost/budget policy, participant credentials, and data-retention/audit behavior. None may be inferred from the old hosted product.
