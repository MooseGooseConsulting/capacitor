# Documentation map

This is an index of the documentation corpus that was present when Capacitor's
standalone work was restarted. It is deliberately a map, not a replacement
summary: existing files stay at their paths and keep their evidence, decisions, and
implementation history. The repository has one general README at its root; this
index is the documentation entrypoint rather than another directory README.

## Reading order and authority

Follow the entry order in the root [`README.md`](../README.md): **Fleet first, then
Surface and its UI capture, then Waves, then Prompt.** Reading order and authority
are distinct: `PROMPT.md` defines the outcome, while `FLEET.md` overrides conflicting
product references.

The authority relationship is:

```text
PROMPT (outcome)
  └─ FLEET (overrides conflicting product references)
       ├─ SURFACE + ui-assets (observed console and visual/read-model target)
       ├─ measured reference findings (facts that correct designs)
       ├─ EVAL-DIRECTION (evaluation capability retained)
       └─ WAVES (method and gates; sequence is a hypothesis)
            └─ schema and implementation material (derived, never ahead of evidence)
                 └─ REPLICATION-MAP-LANDED (current briefing) then 2026-09-04 ledger
```

The initial implementation contract is
[`SESSION-VERTICAL-SLICE.md`](SESSION-VERTICAL-SLICE.md). It makes the session
list and detail surface drive the data model instead of treating data ingestion as a
separate milestone. [`REPLICATION-MAP-LANDED.md`](../reference/REPLICATION-MAP-LANDED.md) is the briefing.
The route ledger is [`REPLICATION-MAP-2026-09-04.md`](../reference/REPLICATION-MAP-2026-09-04.md). The 2026-08-29
original is [`REPLICATION-MAP-2026-08-29.md`](../reference/REPLICATION-MAP-2026-08-29.md).

## Corpus inventory

This directory map covers the complete pre-existing Markdown corpus, including hidden
repository directories. A directory-level entry covers every Markdown file below that
path unless individual files are called out. Re-inventory the repository when the
corpus changes rather than treating a historical count as an authority claim.

| Location | What it contains | How to use it |
|---|---|---|
| repository root | Brief, inherited client guidance, license/provenance, release notes | `PROMPT.md` is the standalone brief. `CLAUDE.md` preserves client invariants. `LICENSE.md` and `NOTICE.md` bind. `RELEASING.md` is inherited release process, not a deployment decision. |
| `.github/` | Pull-request authoring template | Contribution-format guidance, not product or delivery authority. |
| `.sdd/` | Delegated ACP implementation briefs and completion reports | Historical work artifacts for the inherited daemon and a separate server worktree. They neither establish a standalone backend nor define the web console. |
| `reference/` | Product evidence, measured corrections, and the retained eval boundary | Primary product material. `FLEET.md` wins conflicts. `SURFACE.md` and its assets define the observed console. `REPLICATION-MAP-LANDED.md` is the current briefing. `REPLICATION-MAP-2026-09-04.md` is the route ledger. The 2026-08-29 original is `REPLICATION-MAP-2026-08-29.md`. `CROSS-REPO-SESSIONS.md` and `REPEATING-CALLBACK-AUDIT.md` are measurements, not proposals. |
| `reference/ui-assets/` | Captured console tokens, component CSS, icon mapping, and screenshots | Visual acceptance evidence. Do not substitute default MudBlazor styling or invent a different layout without recording a deliberate divergence. |
| `docs/` | ACP/harness findings, historical design and change rationale | Useful source-specific constraints and evidence. They do not define the standalone web product. |
| `docs/eval/` | Legacy evaluation acceptance harness material | Read alongside `reference/EVAL-DIRECTION.md`; the capability stays while inherited taxonomy may change. |
| `docs/probes/` | Measured vendor and protocol probes | Each file is authoritative only for the fact its probe established. Preserve dates, conditions, and limits when relying on it. |
| `docs/schema/` | Candidate wire, canonical-schema, and analytics-view designs | Derived design. It must be reconciled with the surface contract and measured reference findings before a migration or endpoint is written. |
| `docs/superpowers/specs/` | Issue-specific inherited CLI, daemon, desktop, and harness designs | Implementation constraints for the named inherited area. They are not requirements for the Blazor web console. |
| `docs/superpowers/plans/` | Historical implementation plans paired with the inherited specs | History and rationale; never substitute an old plan for a current product decision. |
| `kcap/` | Shipped vendor plugin and skill documentation | Client behavior and operational reference. |
| `npm/kcap/` | Inherited npm package guide | Packaging reference; it does not decide fleet distribution. |
| `scripts/cursor-hook-probe/` | One-off probe guide | Operational reference for that probe only. |

## Schema and surface reconciliation

The three files under `docs/schema/` preserve useful work, but they are not an
approved database migration or a description of a running backend. In particular,
the older candidate DDL has a singular session repository and defers `logical_seq`;
the measured cross-repository evidence and the analytics projection require a
many-repository read model and stable ordering within a source line. The current
resolution is documented in [`SESSION-VERTICAL-SLICE.md`](SESSION-VERTICAL-SLICE.md).
Any schema change must trace each displayed field back to that contract and its cited
evidence.

## Maintenance rules

- Preserve source documents in place. Do not relocate a corpus into an archive or
  replace it with a synopsis merely to introduce a new organization.
- Add a measured finding to `reference/` only with its source, date, conditions, and
  limits. Mark a decision or assumption as such; do not present it as an observed
  fact.
- Keep product navigation in this root README and this index. Do not add recursive
  README files to every documentation area.
- A documentation claim about current runtime state needs a live check. A branch,
  pull request, fixture, or mock response is not proof of deployment or integration.
