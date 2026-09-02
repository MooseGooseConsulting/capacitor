# Desktop application

## Role

`Capacitor.App` is an existing Avalonia desktop client. It is a local operator
surface for the CLI and daemon; it is not the web dashboard and it is not a
replacement backend. The web Sessions experience remains the data-first
console for the shared corpus, as described in [Web console](web-console.md).

This distinction matters while the backend is being built. A desktop feature
that can render local state, start a local daemon, or attach to a local
process does not mean a network API, shared PostgreSQL corpus, or dashboard
capability exists.

## Implemented desktop surface

The source contains a Windows/desktop Avalonia application with:

- an onboarding flow for connection, sign-in, agent integration, daemon,
  defaults, import, shim, and completion steps;
- a Sessions-oriented main window with a session/repository rail and launcher
  empty state;
- daemon start, retry, status, and local lifecycle presentation;
- an activity feed for local consent decisions;
- session/workspace views, terminal attachment, chat tabs, and tool-detail
  rendering;
- tray integration and notifications; and
- services that invoke the local CLI, connect to daemon IPC, manage consent,
  resolve a configured server URL, and track local application state.

Those features are existing source, not a claim that each path succeeds
against Capacitor's future server. Some were designed for the inherited hosted
system or for a locally running daemon and will require service-side contracts
before they can be called end-to-end features.

## Relationship to the daemon

The desktop speaks to the local machine through CLI and daemon-facing services.
It should describe local state honestly: whether the daemon is starting,
healthy, unavailable, or refused, and why. It must not manufacture a healthy
service status from a cached UI state.

The application may guide a user through installing hooks, selecting agent
integrations, starting the daemon, importing local history, or opening the
configured web console. These are local orchestration affordances. The daemon
remains responsible for capture durability; the server remains responsible for
canonical persistence and shared reads.

## Data and control boundaries

| Capability | Current role | Target requirement before it is called complete |
| --- | --- | --- |
| Local daemon lifecycle | Desktop invokes or observes a local daemon. | Clear lifecycle outcome and accurate status on the local node. |
| Local capture setup/import | Desktop can lead users to client capabilities. | Real data reaches the Capacitor ingestion service and appears in persisted projections. |
| Sessions rail/workspace | Existing local UI models and views exist. | Shared web Sessions reads correct PostgreSQL projections; desktop does not diverge into its own corpus. |
| Terminal attach/chat | Local implementations and UI surfaces exist. | Chosen server/process ownership, authorization, consent, and audit contract. |
| Agent launch/review/Flows | Source contains related client pathways. | Explicit remote control-plane design and tests; no decorative or simulated action. |

## Onboarding principles

Onboarding should be a truthful route into a working capture system, not a
checklist that declares success after local configuration writes. A useful
completion state requires at least:

1. an intentional server/profile selection;
2. an authentication path appropriate to an interactive user or a headless
   machine;
3. a configured, running, or clearly unavailable local daemon;
4. installed capture integration for selected agents; and
5. a demonstrated import or live session that reached the intended corpus.

When a future fleet service is unavailable, the desktop should say so and
preserve local capture/spool state where the client supports it. It must not
substitute a private temporary database and label the result fleet capture.

## Consent and mutations

The desktop has local consent and activity components. They should remain the
visible boundary for a local mutation request, but a UI prompt alone is not a
security model for a remote service. Any future action that starts an agent,
writes to a repository, passes terminal input, accesses files, or grants a
tool capability needs a server-side authorization decision and a durable
outcome record. This applies equally to an action initiated by a desktop
button, CLI command, MCP request, or automated Flow.

## Future direction

The desktop remains valuable as the per-node operator experience: configure
capture, show daemon health, expose the local reasons capture cannot proceed,
and link a human to the shared corpus. Its future should follow the data path:

- first, help connect each node to the shared capture service and verify it;
- next, show fleet-aware state only when it is supplied by a real service;
- later, add interactive operations after their authority and containment
  model is selected.

The captured web console should not be reverse-engineered into the desktop,
and the desktop should not be used as evidence that the web console or backend
is complete.
