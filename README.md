# Capacitor (working name)

Capacitor is the standalone system we are building to capture AI coding-agent
sessions from the fleet, retain the raw evidence, enrich it into a usable corpus,
and render that corpus in a console we own end to end. The inherited client is
source-available; the server and web console are not inherited products.

## Start here

`PROMPT.md` directs readers to start with Fleet. Read these in this order before
changing a schema, a route, or a user interface:

1. [`reference/FLEET.md`](reference/FLEET.md) — the overriding product constraints:
   networked fleet capture, headless credentials, a machine dimension, and offline
   durability. It wins when it conflicts with another reference.
2. [`reference/SURFACE.md`](reference/SURFACE.md) and the
   [`reference/ui-assets/`](reference/ui-assets/) capture — the observed console,
   its session data, its navigation, and its visual system. The screenshots,
   tokens, CSS, icon map, and URL-addressable tabs are the acceptance reference for
   the web surface; they are not decorative inspiration.
3. [`reference/WAVES.md`](reference/WAVES.md) — its evidence method and gates are
   binding discipline; its proposed sequence is deliberately a hypothesis.
4. [`PROMPT.md`](PROMPT.md) — the outcome, acceptance criteria, and required
   feature-cut decision point.
5. [`reference/CROSS-REPO-SESSIONS.md`](reference/CROSS-REPO-SESSIONS.md) and
   [`reference/REPEATING-CALLBACK-AUDIT.md`](reference/REPEATING-CALLBACK-AUDIT.md)
   — measured corrections that prevent false repository attribution and duplicate
   lifecycle facts.
6. [`reference/EVAL-DIRECTION.md`](reference/EVAL-DIRECTION.md) — evaluation
   execution is core, while the inherited taxonomy is not.

The first integrated product contract is
[`docs/SESSION-VERTICAL-SLICE.md`](docs/SESSION-VERTICAL-SLICE.md). It starts at
the captured Sessions surface and works backward through the required read model,
normalization, and retained source evidence. It exists so that an API, schema, or
parser cannot be treated as progress unless the real data it produces is rendered
truthfully in the console.

[`docs/INDEX.md`](docs/INDEX.md) maps the complete documentation corpus without
moving, renaming, or reducing it to summaries.

## What is authoritative

The documents serve different jobs. Treat them accordingly:

| Material | Role |
|---|---|
| `LICENSE.md`, `NOTICE.md` | legal and provenance boundary |
| `PROMPT.md`, then `reference/FLEET.md` | goal, acceptance criteria, and overriding fleet constraints |
| `reference/SURFACE.md` plus `reference/ui-assets/` | observed product, client contract, and visual/read-model authority |
| measured `reference/` findings | factual corrections to any older design |
| `reference/EVAL-DIRECTION.md` | retained evaluation capability boundary |
| `reference/WAVES.md` | method and gates; not a prescribed implementation order |
| `docs/schema/` | derived design material to reconcile against the sources above before implementation |
| inherited `docs/`, `kcap/`, and `npm/` material | implementation history and client-specific constraints, not a standalone product decision |

A recovery branch, an unmerged pull request, a local stub, or a test database is
evidence of work in progress. None defines the target product or proves that the
standalone system exists.

## Present boundary

As of a read-only Blood Arrow Kubernetes inventory on 2026-09-02, context
`k3d-cnpg-recovery` has Capacitor-named test jobs but no Capacitor Deployment,
Service, or Ingress. There is therefore no deployed Capacitor API or web console and
no production Capacitor service to call. The Blood Arrow PostgreSQL target is an
isolated recovery/test database (`capacitor_test`), not a production deployment. A
real import through that target and a browser rendering of the resulting session are
required before claiming a data-to-console slice works.

The inherited client remains important: discovery, spooling, hooks, watchers,
daemon behavior, and the wire payloads are source constraints to preserve while the
standalone half is built. Vendor-hosted URLs, telemetry, tenant provisioning,
update checks, feedback, marks, and typefaces are inherited references to replace or
cut under the brief and licensing boundary.
