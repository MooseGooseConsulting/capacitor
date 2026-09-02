# Capacitor (working name)

A self-contained system for recording AI coding-agent sessions — client, server,
store, and console — that we own end to end.

## What this is

The client half began as `kurrent-io/kcap-cli`, which is source-available. The
server half is closed — Kurrent does not ship it, and building it is the job. See `NOTICE.md` for
provenance and licensing.

The reason for the work, in one paragraph: the client tags each batch of
transcript lines with a `vendor` string, and kcap's closed server routes that
to a per-vendor normalizer. You can write the client side of a new coding agent
in an afternoon and it goes nowhere, because you cannot write the normalizer.
Owning the server removes that gate.

## Start here

| | |
|---|---|
| `PROMPT.md` | the brief — what to build and how to judge it done |
| `docs/README.md` | current Capacitor architecture, recovery, dashboard, ingestion, and testing authority |
| `reference/SURFACE.md` | everything observable about the system, both halves, mapped |
| `reference/WAVES.md` | a proposed decomposition, explicitly offered as a hypothesis to challenge |
| `reference/ui-assets/` | console fonts, favicons, stylesheets (placeholders) |
| `reference/VENDOR-README.md` | the inherited README, kept for reference |

## Status

Recovery is in progress. There is no deployed alpha service, public ingress,
or production backend. PostgreSQL is the selected recovery/test backend, and
the first delivery is the data-first web Sessions experience backed by
persisted transcript enrichment—not agent launch, terminal control, or Flows.

Read [the current documentation](docs/README.md) before treating historical
plans, probe results, or inherited product behavior as a current commitment.
