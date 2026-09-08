# Capacitor Replication Map — route ledger

Read [`REPLICATION-MAP-LANDED.md`](REPLICATION-MAP-LANDED.md) first.
This file is the route table and wave-PR footnote at `origin/main`
**`5ae4a671`**. The 2026-08-29 artifact is frozen at
[`REPLICATION-MAP-2026-08-29.md`](REPLICATION-MAP-2026-08-29.md).

Repo: MooseGooseConsulting/capacitor. `main` has the inherited CLI plus
the Sessions slice from **#38** (squash), **#12**, and **#13**. Leftover
`feat/schema-wave-*` and `recover/*` branches are not a merge queue.

**STATES**

| Mark | Name | Meaning |
| --- | --- | --- |
| ● | MERGED | on `main` |
| ◐ | PARTIAL | on `main`, thinner than the client contract |
| ○ | SPEC ONLY | documented, no code |
| × | NOT PLANNED / ABSENT | nothing on `main` |
| — | CLOSED UNMERGED | existed on a wave branch / open PR; not in this tree |

---

## DIAGRAM A

The pipeline, end to end. Every stage from the agent’s hook to the
browser, coloured by where its code actually lives.

```mermaid
flowchart LR
  subgraph importRead [Import and read · on main]
    H["Hooks and CLI · 9 vendors"]
    POST["POST /hooks/transcript · last-line"]
    ING["Ingest"]
    NORM["Normalizers · 5 vendors"]
    SCH["Canonical schema"]
    PG["Postgres 001-006"]
    HTTP["Sessions HTTP"]
    WEB["Capacitor.Web · 6 tabs"]
    H --> POST --> ING --> NORM --> SCH --> PG --> HTTP --> WEB
  end
  subgraph liveWatch [Live watch · absent]
    SIG["SignalR /hubs/sessions · not mapped"]
  end
  subgraph extra [Present in SQL or client, not answering]
    AN["Analytics views · HTTP unmounted"]
    MCP["MCP · analytics dark; sessions partial; review/flows/memory/work-items dark"]
  end
  HTTP -.-> AN
  HTTP -.-> MCP
  H -.-> SIG
```

Import → ingest → five normalizers → Postgres → Sessions HTTP →
`Capacitor.Web` is on `main` (#38). Live watch is a **parallel** hole:
the client dials `/hubs/sessions` and no hub is mapped. The console
does not sit behind that hub.

Analytics HTTP is unmounted, so `kcap-analytics` is dark even though
32 views exist in SQL. Sessions tools that call `/search`, `/turns`,
and `/transcript` can answer; `get_session_summary` cannot (`/recap`
absent). `kcap-review` calls `/api/review/...`, not the flows API.

### What the snapshot called “open PRs by wave”

Those PRs are not open. Status against `5ae4a671`:

| PR | Wave | Snapshot claim | Now |
| --- | --- | --- | --- |
| #10 | 2 | Server.Data, 905 lines | Closed unmerged. `Capacitor.Server.Data` is on `main` via #38. |
| #11 | 3 | Server.Ingest | Closed unmerged. Ingest is on `main`. |
| #12 | 4 | Claude, Universal ACP, Antigravity | **Merged.** Router also has Codex and Kiro. |
| #13 | 5 | 32 views + allowlist | **Merged** as library + `002_analytics_views.sql`. HTTP not mounted. |
| #14 | 6 | API gateway + eval catalog, 11/40 routes | **Closed unmerged.** Capture and Sessions reads landed in #38. Eval catalog HTTP did not. |
| #15 | 7 | Postgres, machines enroll/heartbeat | **Closed unmerged.** Postgres landed in #38 without those machine routes. |
| #16 | 8 | `/hub/capacitor` stub, wrong contract | **Closed unmerged.** No hub on `main`. Client still needs `/hubs/sessions`. |
| #17 | 9 | `POST /api/mcp/sessions` stub | **Closed unmerged.** Not mapped. |
| wip/backup-all-work | — | CLI harness auth, session-start memory, ui-assets | ui-assets landed as #18. The rest is not this file’s subject. |

Also merged after the snapshot: **#38** — Sessions persistence, HTTP,
and `Capacitor.Web`, with a recorded browser walkthrough.

---

## DIAGRAM B

What the client asks for, and what answers.

Left: the six MCP servers and the CLI feature areas, all merged and
working — against the vendor’s server. Right: the backend each one
needs from us.

```mermaid
flowchart LR
  subgraph client [CLI + MCP on main]
    C1["capture · hooks 9 vendors, daemon"]
    C2["import + live watch"]
    C3["eval"]
    C4["auth / setup"]
    C5["machines / daemons"]
    C6["LLM plane · titles, summaries, narration, judge, embeddings"]
    C7["search"]
    C8["web console · 6 session tabs"]
    M1["kcap-analytics · 2 tools"]
    M2["kcap-sessions · 5 tools"]
    M3["kcap-review · 6 tools"]
    M4["kcap-flows · 9 tools"]
    M5["kcap-memory · 6 tools"]
    M6["kcap-workitems · 7 tools"]
  end
  subgraph backend [Backend on main 5ae4a671]
    B1["32 analytics views in SQL<br/>HTTP /schema + /query unmounted"]
    B2["memories table · name reserved, DDL deferred"]
    B3["/hooks/transcript, session-start, session-end, last-line<br/>set-title, session-title · subagent start persists, stop completes"]
    B4["SignalR /hubs/sessions · absent"]
    B5["/api/sessions search ILIKE, turns, transcript, overview, details, events<br/>recap, errors, visibility 501, attachments absent"]
    B6["/api/flows/review/*, /api/work-items/declare · absent"]
    B7["/api/eval/catalog, questions, eval-context, evals/v2/v3, judge-facts · absent"]
    B8["/auth/config, /auth/refresh, signup, first-run, cli-setup · absent"]
    B9["/api/daemons, /api/admin/machines · absent"]
    B10["LLM plane server-side · not built"]
    B11["Capacitor.Web Sessions list + six tabs"]
    B12["/api/review/{owner}/{repo}/pulls/{n}<br/>/api/review/sessions/.../transcript · absent"]
  end
  C1 --> B3
  C2 --> B4
  C2 --> B5
  C3 --> B7
  C4 --> B8
  C5 --> B9
  C6 --> B10
  C7 --> B5
  C8 --> B11
  M1 --> B1
  M2 --> B5
  M3 --> B12
  M4 --> B6
  M5 --> B2
  M6 --> B6
```

The left column is not work remaining — it is inherited and
battle-tested. `kcap` works on this machine today because it is talking
to the vendor’s server. Point it at ours and the watcher will not
connect, analytics HTTP will not answer, and review / flows / memory /
work-items stay dark. Sessions search and turns can answer. Eval HTTP
is unmounted.

Search is substring `ILIKE` on title and event content.
`author_github_id` returns 501. There is no Postgres FTS and no vector
index. The vendor does sessions with FTS and memories with hybrid
semantic search, so matching that still forces a provider decision that
has not been made.

### FEATURE AREAS, PLAINLY

- **Capture** — client done; transcript, session start/end, last-line,
  set-title, session-title on `main`; subagent-start persists then ACKs;
  subagent-stop completes or 404s; whats-done, notification,
  permission-record, antigravity/subagent-link missing.
- **Live watcher** — contract absent, not working.
- **Schema + normalizers** — on `main`, five vendors (Claude, Codex,
  Kiro, Universal ACP, Antigravity). Upstream kcap has ~9 live / 10 import.
- **Postgres** — on `main`, migrations 001–006.
- **Analytics** — views and governor library on `main`; HTTP unmounted.
- **Sessions API** — list, detail, turns, transcript, events, overview,
  search ILIKE on `main`; recap, errors, attachments missing; visibility 501.
- **Search** — ILIKE only; no FTS, no vectors.
- **Memory** — DDL deferred, routes missing.
- **Review flows, work items** — missing.
- **Eval/judge** — `eval_runs` / `eval_verdicts` exist; Sessions detail
  can show a persisted run. Catalog, questions, scoring routes,
  history, and judge-facts missing from HTTP.
- **Auth, org, workspace, signup** — missing; client runs on the
  unauthenticated escape hatch.
- **Machines/daemons** — client still calls `/api/daemons` and
  `/api/admin/machines`; those routes are absent. Enroll/heartbeat
  are also absent.
- **LLM plane** — not built server-side.
- **Web console** — Sessions list + six detail tabs on `main`.
  Agents / Insights / Flows / Work items are visible and unavailable.
- **Multi-machine fleet** — spec only; architecture refs in PR #8.

---

## DIAGRAM C

Import and the Sessions console are on `main`. The remaining live-watch
piece is mapping `/hubs/sessions`. That does not block reading a session
you already imported.

```mermaid
flowchart TB
  subgraph done [On main]
    T["POST /hooks/transcript + GET last-line"]
    IMP["kcap import against local server"]
    WEB["Sessions console · six tabs"]
    T --> IMP --> WEB
  end
  subgraph next [Next against this tree]
    HUB["Map SignalR /hubs/sessions<br/>WatcherConnect, SendTranscriptBatchAcked, WatcherDrainComplete,<br/>SendTitle, ActiveSessionAdded/Changed/Removed, agent-launch plane"]
    ANH["Mount analytics HTTP over existing views"]
    S["Sessions remainder · recap, errors, visibility, FTS/vector"]
  end
  subgraph later [Client contract still absent]
    A["Auth · /auth/config, /auth/refresh"]
    MEM["Memory DDL then routes"]
    F["Flows, work items, /api/review"]
    E["Eval catalog HTTP + scoring"]
    L["LLM plane · blocked on embedding/summarization provider"]
    W["Console chrome · Agents, Insights, Flows, Work items"]
  end
  WEB --> HUB
  WEB --> ANH
  HUB --> S --> A --> MEM --> F --> E --> L --> W
```

Do not rebuild `/hub/capacitor`, enroll, heartbeat, or
`POST /api/mcp/sessions`. Closed #16 is the wrong contract.

- `kcap import` against the local server can complete. **Live watch cannot.**
- Sessions API — turns and transcript exist; recap, errors, search
  beyond ILIKE, visibility do not.
- Auth — `/auth/config` and `/auth/refresh`.
- Memory — the DDL first, since it is deferred, then the routes.
- Flows and work items routes.
- Eval — catalog and scoring HTTP, on top of `eval_runs` / `eval_verdicts`.
- LLM plane — cannot start until an embedding and summarization
  provider is chosen.
- Analytics HTTP — mount the already-merged views behind the allowlist.
- Web console — six session tabs exist. Remaining chrome: Agents,
  Insights, Flows, Work items, share, copy-link, delete.

Separately, one route on `main` exists because the server invented it,
not because the client calls it: `GET /watermarks`. Delete it or
realign it. Enroll, heartbeat, `/api/mcp/sessions`, and `/hub/capacitor` are
absent from this tree. The client calls `/api/daemons`,
`/api/admin/machines`, and `/hubs/sessions`.

---

## WAVE GATES, FOR REFERENCE

1. Contract document exists; three probe sessions captured
   input-and-output; unknown-vendor behaviour known, not assumed.
2. `kcap import` against the local server completes and last-line
   reports the right watermark. The two halves are connected.
3. For one real Claude session, turns and events match kcap’s — turn
   count, per-turn tool counts, token totals, event ordering.
4. Three vendors normalized end to end, live capture working, and the
   feature cut approved by the operator.
5. Open a session you captured yourself and read it.
6. A coding agent kcap cannot record, recorded.

`WAVES.md` marks the sequence itself as a weak guess and the gates as
strong. Gate 2’s import half can pass; the live-watch half cannot until
`/hubs/sessions` exists. Gate 5 has a console and a recorded
walkthrough in `deploy/recovery/` (that job was not re-run in this
checkout). Gate 6 is unmet.

---

## ROUTE LEDGER

Every endpoint the client calls. Measured against `5ae4a671`. Capture
and Sessions reads answer. Analytics HTTP, eval HTTP, live watcher,
auth, fleet, memory, flows, and work-items do not.

| Endpoint | State | Note |
| --- | --- | --- |
| **CAPTURE & HOOKS** | | |
| `POST /hooks/session-start/{vendor}` | ● MERGED | also vendor-less `POST /hooks/session-start` |
| `POST /hooks/session-end/{vendor}` | ● MERGED | also vendor-less `POST /hooks/session-end` |
| `POST /hooks/transcript` | ● MERGED | `strict`; failed lines skip event identity |
| `GET /api/sessions/{id}/last-line` | ● MERGED | watermark for resume |
| `POST /hooks/subagent-start` | ● MERGED | persists the child session, then ACK |
| `POST /hooks/subagent-stop` | ● MERGED | completes the child or 404 |
| `/hooks/session-title` | ● MERGED | |
| `/hooks/set-title` | ● MERGED | |
| `/hooks/whats-done` | × ABSENT | |
| `/hooks/notification` | × ABSENT | |
| `/hooks/permission-record` | × ABSENT | |
| `/hooks/antigravity/subagent-link` | × ABSENT | |
| **LIVE WATCHER** | | |
| SignalR `/hubs/sessions` | × ABSENT | client: `WatcherConnect`, `SendTranscriptBatchAcked`, `WatcherDrainComplete`, `SendTitle`, `ActiveSessionAdded` / `Changed` / `Removed`. No hub mapped. Closed #16 was `/hub/capacitor` — incompatible with this contract. |
| **ANALYTICS** | | |
| `GET /api/analytics/schema` | × ABSENT | 32 views exist in SQL; this HTTP is unmounted |
| `POST /api/analytics/query` | × ABSENT | `GovernedSql` library exists; HTTP unmounted |
| **EVAL & JUDGE** | | |
| `GET /api/eval/catalog` | × ABSENT | |
| `GET /api/eval/questions` | × ABSENT | |
| `GET /api/sessions/{id}/eval-context` | × ABSENT | |
| `GET /api/sessions/{id}/evals/v2` | × ABSENT | |
| `GET .../evals/v3` | × ABSENT | |
| `GET .../judge-facts` | × ABSENT | |
| `GET .../eval-summary` | × ABSENT | |
| **SESSIONS READ & SEARCH** | | |
| `GET /api/sessions/{id}` | ● MERGED | header + events + trace + latest eval |
| `GET /api/sessions/{id}/overview` | ● MERGED | |
| `GET /api/sessions/{id}/details` | ● MERGED | |
| `GET /api/sessions/{id}/events` | ● MERGED | |
| `GET /api/sessions/{id}/transcript` | ● MERGED | |
| `GET /api/sessions/{id}/turns[/{i}]` | ● MERGED | |
| `GET .../recap` | × ABSENT | blocks `kcap-sessions` `get_session_summary` |
| `GET .../errors` | × ABSENT | |
| `PUT .../visibility` | ◐ PARTIAL | returns 501 |
| `GET/POST /api/sessions/search` | ◐ PARTIAL | GET ILIKE title + event content; `author_github_id` → 501; POST unmapped; no FTS or vectors |
| `GET /api/attachments/{id}` | × ABSENT | |
| **PROJECTS & REPOSITORIES** | | |
| `GET /api/projects` | × ABSENT | |
| `GET /api/repositories/` | × ABSENT | |
| `GET /api/repositories/{id}/skills` | × ABSENT | |
| **MEMORY** | | |
| `memories` table | ○ SPEC ONLY | name reserved in CANONICAL-SCHEMA-SPEC.md, DDL deferred |
| `GET/POST /api/memories[/index]` | × ABSENT | |
| **FLOWS & WORK ITEMS** | | |
| `POST /api/flows/review/start{,/v2,/v3,/v4}` | × ABSENT | |
| `POST .../participant/message` | × ABSENT | |
| `POST .../reviewer/result` | × ABSENT | |
| `POST /api/work-items/declare` | × ABSENT | |
| **REVIEW MCP** | | |
| `GET /api/review/{owner}/{repo}/pulls/{n}` | × ABSENT | `kcap-review` |
| `GET /api/review/sessions/{id}/transcript` | × ABSENT | `kcap-review` |
| **AUTH, ORG & ONBOARDING** | | |
| `GET /auth/config` | × ABSENT | client runs on the unauthenticated escape hatch |
| `POST /auth/refresh` | × ABSENT | |
| `POST /api/signup/provision` | × ABSENT | |
| `POST /api/first-run/flows` | × ABSENT | |
| `POST /api/users/me/cli-setup` | × ABSENT | |
| **MACHINES & DAEMONS** | | |
| `GET /api/daemons` | × ABSENT | |
| `GET/POST /api/admin/machines` | × ABSENT | |
| **PRODUCT SURFACE** | | |
| `GET /api/me/notification-prefs` | × ABSENT | |
| `POST /api/feedback` | × ABSENT | |
| `POST /api/agent-runs/{id}/events` | × ABSENT | |
| **SERVER-INVENTED — THE CLIENT NEVER CALLS THESE** | | |
| `GET /watermarks` | ● MERGED | leftover; delete or realign |
| `POST /api/machines/enroll` | — CLOSED | was on wave 7; not on `main` |
| `POST /api/machines/heartbeat` | — CLOSED | was on wave 7; not on `main` |
| `POST /api/mcp/sessions` | — CLOSED | was PR #17 stub; not on `main` |
| hub `/hub/capacitor` | — CLOSED | was PR #16; not on `main`; replace with `/hubs/sessions` if building live watch |

---

## SETTLED

Decisions already made. Four calls, and one still open.

**Store.** Postgres, not KurrentDB. An append-only positioned events
table covers streams, subscriptions and projections at this scale.

**Eval.** Stays a core primitive — decision documented in PRs #20 and
#21, merged to main. `eval_runs` / `eval_verdicts` exist; Sessions
detail can include the latest run. Scoring HTTP is unbuilt.

**Memory.** DDL deferred. `memories` is a reserved name in the
canonical schema spec, nothing more.

**Search.** Still open for FTS and vectors, and that blocks matching
the vendor. This tree has ILIKE substring search on sessions. The
vendor does sessions with FTS and memories with hybrid semantic search,
so the replica has to pick an embedding provider before it can match
that.

### ALSO ON RECORD

The canonical schema spec covers 14 tables; the analytics spec covers
32 views. Both are merged to main as documents. The views also exist as
`002_analytics_views.sql`.

Multi-machine fleet is spec only — architecture references live in PR #8.

Normalizers on main cover Claude, Codex, Kiro, Universal ACP and
Antigravity. Upstream kcap normalizes about nine live vendors.

---

## ACCOUNTING

`SURFACE.md` inventories the client contract. This ledger is the status
column against `5ae4a671`. Refresh both when a PR changes a route. The
wire list uses inline “answered on main” / “missing” notes, not a
separate three-state column.

### Web console

`src/Capacitor.Web/` on `main` via #38: Sessions list, six-tab detail
(Overview, Transcript, Events, Trace, Evaluation, Details). Agents /
Insights / Flows / Work items are visible and unavailable. Share,
copy-link, and delete are disabled.

PR #18 is the vendor design-system capture (tokens, icons, frozen HTML).
The 2026-08-28 `agent-*` worktrees on `C:\_projects\capacitor` were not
collected into this tree.

---

## MERGED TO MAIN

- Inherited upstream client: CLI with 34 groups / 67 leaf commands,
  hooks for 9 vendors, daemon, 6 MCP servers with 35 tools, desktop app
- `docs/schema/CANONICAL-SCHEMA-SPEC.md` — 14 tables
- `ANALYTICS-VIEWS-SPEC.md` — 32 views
- `WIRECRAFT-MAPPING.md` — wire to schema field map
- `reference/SURFACE.md`, `WAVES.md`, `BACKLOG.md`, `VENDOR-README.md`
- `reference/ui-assets` — captured vendor CSS, fonts, icons
- PR #8 fork-pivot, PR #9 schema spec, PRs #20 and #21 eval-as-core-primitive
- PR #18 `docs/console-design-system-capture` — the vendor console’s
  tokens, icons and frozen tab HTML. Reference, not code.
- PR #12 `Capacitor.Server.Normalizers`
- PR #13 `Capacitor.Server.Analytics` + `002_analytics_views.sql`
- PR #38 `Capacitor.Server.{Data,Ingest,Api}` + `Capacitor.Web` Sessions
  slice, Postgres migrations 001–006
- PR #19 / #22 — self-hosted CI runner (`.github/workflows`)

`README.md` on this SHA says a route on `main` is not a deployed
service, and names `capacitor_test` as the recovery database. It does
not say the server is unbuilt.

---

## WHAT TO BUILD NEXT

Against this tree: `/hubs/sessions`, then mount analytics/eval HTTP
over work that already exists. Auth, memory DDL, flows, `/api/review`,
and an embedding provider are still open. `PROMPT.md` is the product
cut.

Session `2ecb16fd` (2026-08-28) raised pivoting to agent-corpus. Nothing
in that session decided it. #38 landed the C# Sessions slice. Do not
treat leftover branches as a second vote.

Do not merge `#14`–`#17` or recover `#31`/`#32`/`#36`. Wave 8 maps
`/hub/capacitor`. Recover work is inside the #38 squash.

---

## CURRENT BLOCKERS

Live watch (no `/hubs/sessions`), unmounted analytics/eval HTTP, no
embedding provider for FTS/hybrid search. CI runs on
`[self-hosted, Linux]`. Open PRs besides this documentation change:
none. Stale `feat/schema-wave-*` remotes are not `main`.

The 2026-08-28 snapshot (14 open PRs, 208 bot threads, billing-blocked
Actions) is history. It is not the current queue.
