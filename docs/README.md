# Capacitor documentation

This directory is the current documentation authority for the Capacitor
derivative in this repository. It describes the system we are building, not
the inherited hosted Kurrent product.

| Document | Authority |
| --- | --- |
| [Architecture](architecture.md) | Runtime boundaries, PostgreSQL test topology, and non-deployment status |
| [Dashboard](dashboard.md) | The web Sessions surface and its data-first delivery boundary |
| [Ingestion](ingestion.md) | Transcript capture, normalization, persistence, and replay invariants |
| [Recovery](recovery.md) | How the closed schema-wave PRs are being recovered without merging them wholesale |
| [Testing](testing.md) | Remote-only validation and test-database rules |

## Current state

Capacitor has no deployed alpha service and no production backend. The backend
being recovered lives in this monorepo. Its designated integration target is
the isolated `capacitor_test` database on Blood Arrow's `pg18-core-recovery`
PostgreSQL cluster. That database is for recovery and test work only; it is
not a production surface or production data store.

The first delivery is the captured web **Sessions** experience backed by real,
enriched transcript data. Agent launch, terminal control, Flows, and Work
Items are not part of that data-first milestone.

## Historical material and captured evidence

`history/pre-recovery/` preserves every document that was previously under
`docs/`, at the same relative path. It is provenance and may contain useful
research, but it is not current architecture or a delivery commitment.

[`reference/`](../reference/README.md) is read-only captured evidence. In
particular, `reference/SURFACE.md` and `reference/ui-assets/` define the
observed console target; they do not define our runtime, brand, or deployment
status.
