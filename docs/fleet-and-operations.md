# Fleet and operations

## Objective

Capacitor's operating target is one corpus for every place an agent runs. A
single laptop connected to a local store is a useful development condition but
not the product objective. The fleet requirement changes the server boundary,
authentication, data model, and acceptance tests.

This document states the target operating model and distinguishes it from the
current recovery environment. It is based on the measured and reviewed fleet
brief in [reference/FLEET.md](../reference/FLEET.md).

## Present recovery environment

**Implemented and verified for testing:** Blood Arrow's CloudNativePG recovery
cluster has a dedicated `capacitor_test` database and `capacitor_test` role.
It is reached through the cluster's read-write service during remote
integration tests. The credential is provisioned from the named Doppler secret
`CAPACITOR_TEST_DB_PASSWORD` into the Kubernetes basic-auth Secret
`data-platform/capacitor-test-db-credentials`; values are never committed or
printed.

**Not present:** an API Deployment, Service, Ingress, deployed dashboard,
production database, customer-facing alpha environment, or production
credential design. The recovery database must not be described as a production
surface, and tests must not reuse another application's role or database.

## Target topology

```text
fleet node
  CLI / hooks / watcher / daemon
  local lifecycle and transcript spools
       | authenticated, retrying transport
       v
network-reachable Capacitor API
  ingestion + read APIs + session update hub
       v
PostgreSQL corpus
  raw evidence, canonical events, watermarks, projections
       v
web console / MCP query surface / fleet operations
```

The network endpoint, transport, service placement, TLS and ingress policy,
and production PostgreSQL topology are open implementation decisions. They
must be selected and operated as one system; a local-only API is insufficient
for a fleet. A deployment plan is not evidence that this topology is live.

## Fleet invariants

### Durable capture through outages

The inherited client persists lifecycle and transcript work in local spools
and includes drain behavior in both hook and daemon paths. That durability is
load-bearing: an offline node that runs for a day must later deliver its
unacknowledged data without loss or duplication. Service operations must
preserve the client contract of an honest watermark and idempotent retry; they
must not accept a batch and report progress before it is durable.

### Headless enrollment and authentication

A network-reachable ingestion service cannot use the no-auth localhost
development posture. The inherited headless client path expects machine
credentials:

```text
KCAP_CLIENT_ID + KCAP_CLIENT_SECRET
  -> client-credentials token exchange
  -> bearer token for capture and daemon traffic
```

The target server therefore needs a client-credentials exchange and machine
management compatible with the existing `machine create`, `machine list`, and
`machine revoke` client surface. The management API and daemon registration
are core fleet services, not optional dashboard features. Credential issuance,
storage, revocation, token lifetime, and operator authorization still need a
production design; the historical hosted WorkOS endpoint is not available to
reuse.

### Machine and daemon attribution

`machine_id` belongs on the session from session-start onward. It cannot be
reliably reconstructed after a corpus is collected. Daemon identity, name,
advertised vendor capabilities, and last-seen state are likewise operational
facts. The existing schema-oriented code contains machine and daemon record
types and fields, but a fully operating registry/API is not yet a present
service claim.

Fleet operations should be able to answer:

- Which node captured a session or produced an error?
- Which nodes and named daemons are currently reporting or overdue?
- Which vendors are available on a given node, rather than merely absent from
  one machine's disk?
- Which repository path was observed on the node that produced an event?

### Node-local context remains node-local

The same repository has different filesystem paths across machines. Clock
skew, distinct vendor installations, concurrent daemons, and possible
session-ID collisions are normal fleet conditions. Repository remapping is a
per-node attribution tool, not a global string replacement. Before treating
agent-generated session IDs as globally unique, the implementation must
measure and enforce the required uniqueness boundary.

## Repository attribution

Observed vendor data disproves a one-repository-per-session model. For example,
many Codex sessions touched multiple repositories through per-tool `workdir`
values even when the session's own `cwd` stayed put. The detailed measurement,
including its caveats by vendor, is in
[reference/CROSS-REPO-SESSIONS.md](../reference/CROSS-REPO-SESSIONS.md).

The target model is therefore:

- retain the finest observed `cwd` and repository evidence on each event or
  turn when a vendor supplies it;
- permit missing event-level attribution rather than filling it in with a
  guessed session repository;
- derive a many-to-many session/repository projection with an evidence-based
  primary repository; and
- retain a single primary repository projection for compatibility where a
  consumer needs one.

This work is a correctness requirement for fleet reporting, repository filters,
cost attribution, PR attribution, and scoped memory. It is not merely a
dashboard enhancement.

## Database operations

PostgreSQL is the selected shared backend. The recovery cluster's isolated
database is the integration target while the server is being recovered. A
test requiring PostgreSQL runs remotely against that database; it does not
substitute SQLite, a local temporary store, or a mock and then call the result
PostgreSQL validation.

Operational requirements for the eventual corpus include:

- migrations with an upgrade path, not only clean-database creation;
- transactional event insertion and contiguous watermark advancement;
- scoped test data and cleanup that cannot affect another application;
- least-privilege service/database credentials and rotation without exposing
  secret values; and
- backups, retention, restore verification, observability, and capacity
  planning before a production corpus is declared available.

The last two bullets are target requirements, not completed operations work.
The dedicated recovery test database does not settle production retention,
backup, access, or billing decisions.

## Service operations

The target service exposes two distinct operational planes:

| Plane | Purpose |
| --- | --- |
| **Capture and data plane** | Lifecycle and transcript ingestion, watermark reads, canonical projections, Sessions reads, and post-persistence updates. |
| **Fleet control plane** | Headless identity, machine and daemon registration, health, and the operator-facing inventory needed to keep capture running. |

Remote agent launching, terminal multiplexing, ACP-hosted sessions, review
flows, and Flows are a third, later plane. They must not be bundled into the
first service deployment merely because the inherited client has commands for
them. Their operational and permission model needs explicit design.

## Fleet acceptance

The first complete fleet proof is not a healthy process on one laptop. It is a
second machine enrolled with a machine credential, sending real capture data
into the same corpus, with its sessions distinguishable from the first node.
That proof should include a temporary network loss and successful later drain.
The selected second-node candidate in the evidence is `hephastus`, which has
vendor data that is not present on the first node; current reachability must be
rechecked before using it as an acceptance environment.

## Open operational decisions

- Where the network API and production PostgreSQL cluster will live, and who
  operates them.
- Node-to-service transport and private-network reachability.
- Production authentication/authorization, credential issuance, token
  lifetime, revocation, and audit policy.
- Backup, retention, recovery-point/recovery-time objectives, and corpus cost
  allocation.
- Fleet binary distribution and update policy.
- Multi-user/project/visibility semantics for a one-operator, many-machine
  corpus, and how they evolve if more users are added.

Resolve these with an explicit decision and an operational test, rather than
silently importing hosted Kurrent defaults.
