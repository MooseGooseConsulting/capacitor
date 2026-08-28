# Fleet capture is the objective

Read this before `SURFACE.md` and `WAVES.md`. It reframes both.

**The goal is not "record my sessions." It is: every place an agent runs, anywhere in
the fleet, records into one corpus.** This laptop is node 1 of N, not the system.

That single sentence changes the architecture, the auth story, the deployment, and the
canonical model. Several things marked "org scaffolding — probably cut" elsewhere in
this repo are, under this objective, load-bearing. Where these documents disagree, this
one wins.

---

## What it changes

### 1. The server is networked, not local

A localhost server records one machine. Every node must reach the same server, which
makes deployment a real decision rather than an afterthought: where it lives, how nodes
resolve it, and what happens to a node that can't reach it.

The client already handles the last part well — undelivered lifecycle events and
transcript content spool to disk and drain later. **A fleet node that is offline for a
day must lose nothing.** That property is inherited, not built; don't break it.

Note the operator's tailnet is the natural transport, and that **`hephastus`'s Tailscale
is currently dead** (LAN-only at `192.168.0.220`). Under a fleet objective that is no
longer a nuisance to route around — it is a node that cannot report.

### 2. Auth is required, and headless

Provider `None` posting unauthenticated is fine for a localhost bring-up. It is not fine
for a server every node can reach.

The mechanism already exists in the client and is exactly right:

```
KCAP_CLIENT_ID  +  KCAP_CLIENT_SECRET      →  grant_type=client_credentials  →  bearer
```

`MachineAuth` reads those two environment variables; kcap's own comment describes the
consumer as "a fresh container with two environment variables" with no profile and no
token store, and calls the shared value "one value for the whole fleet." `kcap machine
create | list | revoke` is the management surface, and `/api/admin/machines` is its route.

The token endpoint pointed at kcap's WorkOS tenant (`signin.kcap.ai/oauth2/token`)
and was severed at the fork. **That is a route to reimplement, not a feature to delete.**

So the minimum sink is **nine routes, not eight** — the eight in `SURFACE.md` §4 plus a
client-credentials token exchange. `/api/admin/machines` and `/api/daemons` move from
"product surface, out of scope" to **core**.

### 3. The canonical model needs a machine dimension — kcap's does not have one

This is the one place where "match kcap exactly" is the wrong instruction.

`v_an_sessions` has no machine or host column. Sessions attribute to a *user*, and a user
is one identity across machines, which is sufficient for a product sold to teams. It is
not sufficient here. Without a machine dimension you cannot ask:

- what ran on `hephastus` overnight
- which node is producing the errors
- which nodes have gone quiet
- did this session's repo path mean the same thing on the machine that recorded it

**Add machine identity to the session model as a first-class field**, carried on the
session-start payload and stored on the session. It is one column and one payload field
now; it is a backfill against a corpus you cannot reconstruct later.

`machine_id` already exists in the client's profile config (`mach-7a0756319756` on this
laptop) — but it is used to tag *memories*, not sessions. Reuse the identity; don't
reuse the plumbing.

### 4. Cross-machine identity problems become real

Single-machine, these don't exist. Fleet-wide, they all do:

- **Repo attribution.** The same repository lives at different paths on different nodes.
  `kcap remap <from> <to>` exists precisely for this ("rewrite a recorded transcript cwd
  prefix during import") and becomes a per-node necessity rather than a rename fixup.
- **Clock skew** across nodes, when ordering events and computing durations.
- **Session id collision.** Ids come from the agent on each node. Verify they are
  globally unique before assuming it.
- **Concurrent daemons.** `daemon --name` and multi-daemon support already exist; a fleet
  exercises them.
- **Vendor coverage differs per node.** Kilo's VS Code data is on `hephastus` and not on
  this laptop. Discovery must be per-node, and "this vendor has no data" is a statement
  about a node, never about the fleet.

---

## Revised feature cut under a fleet objective

Supersedes the informal cut elsewhere in this repo.

| subsystem | single-user read | **fleet read** |
|---|---|---|
| Machine credentials, `machine create/list/revoke` | marginal | **core — the primary enrolment path** |
| `/api/admin/machines`, `/api/daemons` | out of scope | **core — fleet health and inventory** |
| Auth (some form) | optional | **required** |
| Spool + drain durability | nice | **core — offline nodes must lose nothing** |
| `remap` (cwd rewriting) | rename fixup | **core — per-node path normalisation** |
| Machine dimension on sessions | absent in kcap | **build it — deliberate divergence** |
| Telemetry to kcap | cut | cut |
| Tenant provisioning / SaaS signup | delete | delete |
| npm distribution / update channel | cut | **revisit** — how does a fleet node get updated? |
| Teams, projects, roles, members | mostly cut | **revisit** — one user, many machines is not the same as one user, one machine |
| Flows, evals | operator's call | operator's call |

Two that genuinely change under fleet framing rather than merely surviving:

- **Distribution.** Cutting the update channel is right for one laptop you build on. For
  N nodes it reopens as "how does a node get a new binary" — a real question with a real
  answer, just not kcap's npm package.
- **Visibility.** `--private` / `hide` / `org_public` looked like team scaffolding. With
  many machines feeding one corpus, per-session visibility is how a node records without
  everything on it becoming equally exposed.

---

## The acceptance test changes

`PROMPT.md` calls for a fourth vendor added end to end, to prove the normalizer gate is
gone. Keep that. **Add a second, equally important one:**

> **A second machine, enrolled headlessly with a machine credential, records into the
> same corpus — and its sessions are distinguishable from this laptop's.**

`hephastus` is that machine. It holds vendor data this laptop does not, which makes it a
genuine test of per-node discovery and not just of transport.

A stack that records one machine perfectly has not met the objective.
