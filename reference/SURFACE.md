# The surface, mapped

Everything observable about Kurrent Capacitor, and which half of it we already have.

Compiled 2026-08-27 from: the `kcap-cli` source now in this repo; the live instance at
`moosegoose.kcap.ai` (~123 real sessions); the connected `kcap` MCP tools; `kcap --help`
from the installed binary; and the console's own downloaded stylesheets.

---

## 0. The two halves

| | source | status |
|---|---|---|
| **Client** | this repo, 772 commits, Kurrent's `main` @ `50928963` | **inherited, working** |
| **Server** | closed | **to be built** |

The client is complete and battle-tested: ten vendors' on-disk discovery, classification
against a server watermark, batching, resume, spooling with drain throttling, live hooks
per vendor, a SignalR streaming watcher, auth, a daemon, an MCP server, and a desktop
app. None of that needs writing.

The server is everything the client talks to. It is smaller than it sounds — see §4.

---

## 1. Console surface

**Stack: Blazor + MudBlazor.** Assets live under `_content/Capacitor.Ui/` and
`_content/MudBlazor/`. Also loaded: BlazorMonaco (the Monaco editor), xterm.js
(terminal), Prism (syntax highlighting), driver.js (product tour).

This matters: **MudBlazor is MIT-licensed and freely reusable.** Most of the console's
component vocabulary — tabs, cards, tables, drawers, chips — is a library, not bespoke
work. The bespoke layer is `components.css`: 107 KB, 355 component classes, 15 semantic
custom properties.

Downloaded to `reference/ui-assets/`: three fonts, three favicons, four stylesheets.

> **Note on those assets.** Inter is openly licensed. **Solina is a commercial typeface
> and the favicons are Kurrent's marks** — fine as local placeholders while standing the
> stack up, but they are trademarks and must be replaced before this is shown to anyone
> outside the org. Same for the product name.

### Top-level navigation

`SESSIONS` (count badge) · `AGENTS` (live count) · `INSIGHTS` · `FLOWS` ·
`WORK ITEMS` (beta) · global search (⌘K) · help · account.

Left rail: `All`, `My projects`, `Other repos` grouped by GitHub owner. A trial banner.
A `Setup · 5/8` progress widget. Version footer.

### Session list card

Each card carries: status dot · title · work-item chip · repo (`owner/name`) · PR number
· **vendor chip** (`claude`, `codex`) · **model chip** (`claude-opus-5`, `gpt-5.6-sol`) ·
token flow `in → out (Σ total)` · **context occupancy** `254k / 1M (25%)` · diff
`+796 -4` · tool count · error count · a second badge (skills?) · relative start time.

Filters: an "All users" selector; repo selection from the rail.

### Session detail — six tabs

| tab | what it shows |
|---|---|
| **Overview** | summary |
| **Transcript** | the rendered conversation, per-message: model chip, token flow, cache read/write, cost, timestamp, duration |
| **Events** | the raw typed event stream — see §2 |
| **Trace** | the turn rollup — see §2 |
| **Evaluation** | LLM-as-judge output |
| **Details** | metadata |

Header actions: share, copy link, refresh, delete, an `Active`/status pill, owner avatar.

### Known dead ends

- Global search matches session **titles and transcripts**, not vendor. Searching a
  vendor name returns sessions *about* it.

### Insights, now enabled

Was unconfigured as of the original capture; enabled on this instance since, at
**Administration → Assistants → Insights agent** (`Provider: OpenRouter`, `Model:
poolside/laguna-s-2.1:free` — the same free-tier model the title/backfill assistants on
this tenant already use, reusing that vendor's key from AI Providers; no separate key
needed). Once enabled it renders six category tabs — Adoption & Utilization, Cost/
Attribution/Allocation, Productivity & Impact, Agent Behavior & Observability, Delivery &
SDLC, Evaluation & Quality — plus a chat box (`Ask about sessions, tools, evals...`) backed
by a `query_read_model` tool that runs live queries, not canned answers: asked "How many
sessions ran in the last day?", it answered correctly against the actual corpus. Confirms
the settings page's own description — the agent "also runs live, event-level queries over
KurrentDB Flight SQL" on the tenant-wide view.

---

## 2. The canonical model, as observed

This is the thing that has to be reproduced. Read from a real 23-turn session.

**Events — 756 for 23 turns.** A typed, ordered stream. Types seen include:

- `SessionStarted`
- `Assistant Thinking Generated`
- tool invocations — named `<server>: <tool>` (e.g. `claude-in-chrome: browser_batch`),
  each with expandable **Input** and **Output**
- user messages
- background-command lifecycle (`completed`, `stopped`, with exit status)

Every event carries: **model** · **token delta** `in → out (Σ total)` · **cache**
`r<read> w<write>` · **cost estimate** · **timestamp**. Some carry flags — `Compacted`
was observed on a tool event.

**Trace — the turn rollup.** `Turn N` · start time · **duration** (`11m 15s`, `56ms`) ·
token flow · **tool count**. Turns interleave at the top level with non-turn entries:
`SessionStarted`, user messages, and background-command completions each sit between
turns as first-class rows.

**No machine dimension.** `v_an_sessions` has no machine or host column; sessions
attribute to a *user*. Sufficient for a product sold to teams, insufficient here — see
`FLEET.md` §3. This is the one place where matching kcap exactly is the wrong
instruction.

**Identifiers.** Session ids are stored **dashless**: the session UUID
`70dc37b2-b3b1-4f13-9c15-3858abbe88a8` appears as
`/sessions/70dc37b2b3b14f139c153858abbe88a8`.

**Session classification.** The stylesheet's custom properties expose the taxonomy:
`--phase-spec`, `--phase-implementation`, `--phase-review`, `--phase-debug`,
`--phase-chore`, `--phase-neutral` — matching `primary_phase` / `secondary_phase` in the
analytics views. Work-tracker lanes: `--wt-lane-commit`, `--wt-lane-issue`,
`--wt-lane-pr`; milestone states `--wt-ms-{idle,merged,closed,reopened}`.

---

## 3. The analytics surface — 32 governed views

`get_analytics_schema` returns ~87,000 characters of view and column semantics. **This is
the model described by the system in its own words, and it is the single highest-value
artifact available.** Read it in full before designing any schema.

```
sessions      v_an_sessions, v_an_session_steps, v_an_context, v_an_cost,
              v_an_token_usage_by_model, v_an_tool_usage, v_an_skill_usage,
              v_an_subagent_runs, v_an_memory_ops, v_an_incident_signals
code          v_an_code_changes, v_an_file_changes, v_an_commits
prs           v_an_prs, v_an_pr_sessions, v_an_pr_churn, v_an_pr_churn_summary,
              v_an_pr_test_runs
work          v_an_work_items, v_an_work_item_sessions, v_an_work_item_links,
              v_an_work_item_milestones
evals         v_an_eval_scores, v_an_eval_summaries
deploys       v_an_deployments, v_an_deployment_coverage,
              v_an_deployment_status_uncertainties, v_an_release_publications
org           v_an_users, v_an_repositories, v_an_team_memberships,
              v_an_user_primary_team
```

`v_an_sessions` columns: `repo_hash, session_id, model, vendor, status, visibility,
started_at, ended_at, owner_user_id, event_count, last_event_at, hidden_reason,
previous_session_id, next_session_id, primary_phase, secondary_phase,
classification_confidence, classification_source, disposition, duration_min`.

Note `previous_session_id` / `next_session_id` — **sessions chain**, which is what
`--chain` means on the CLI's `recap` and `errors`.

Query governor: rejects column aliases outside the governed list; takes
`scope: 'repo' | 'global'`; row-capped.

**Documentation drift worth knowing:** the schema documents `vendor` as *"claude, codex,
copilot, or cursor"* while the client ships ten. Don't assume the docs are current.

---

## 4. The wire contract — what the server must answer

Fully determined by the client, which is in this repo. Extracted by enumerating every
`{baseUrl}/…` literal and every hub invocation.

### Minimum viable sink — eight routes for one machine, **nine for a fleet**

> Under the fleet objective (`FLEET.md`) add a ninth: a **client-credentials token
> exchange**, replacing the severed `signin.kcap.ai/oauth2/token`. Headless nodes carry
> `KCAP_CLIENT_ID` / `KCAP_CLIENT_SECRET` and have no profile or token store, so this is
> their only way in. `/api/admin/machines` and `/api/daemons` are core, not product
> surface.


```
POST /hooks/session-start/{vendor}
POST /hooks/session-end/{vendor}
POST /hooks/subagent-start          # must ACK before children stream
POST /hooks/subagent-stop
POST /hooks/transcript              # honour `strict`
GET  /api/sessions/{id}/last-line   # 200 + last_line_number | 204 | 404
GET  /auth/config
POST /auth/refresh
```

The `{vendor}` segment is **parameterized, not enumerated**.

### The payload

```jsonc
// POST /hooks/transcript
{ "session_id","agent_id","lines":[…≤100 raw vendor JSONL…],
  "line_numbers":[…], "vendor":"kimi", "strict":true, "repository":{…} }
```

Lifecycle payloads are flat objects keyed by `hook_event_name`
(`agentSpawn` / `sessionEnd` / `subagent_start` / `subagent_stop`), carrying
`session_id`, `cwd`, `workspace_root`, `model`, `started_at`/`ended_at`, and an
`origin` field distinguishing **live capture from historical import**.

### Beyond the minimum

Ingestion: `/hooks/session-title`, `/hooks/set-title`, `/hooks/whats-done`,
`/hooks/notification`, `/hooks/permission-record`, `/hooks/antigravity/subagent-link`.

Read: `/api/sessions/{id}/turns[/{i}]`, `/recap`, `/errors`, `/visibility` (PUT),
`/api/sessions/search`, `/api/projects`, `/api/repositories/`, `/api/memories[/index]`,
`/api/attachments/{id}`, `/api/work-items/declare`, `/api/analytics/{schema,query}`,
`/api/daemons`, `/api/admin/machines` (**both core under FLEET.md**), `/api/flows/*`, `/api/eval/*`,
`/api/sessions/{id}/{eval-context,evals/v2,evals/v3,judge-facts}`,
`/api/me/notification-prefs`, `/api/feedback`, `/api/signup/provision`.

### SignalR `/hubs/sessions`

Ingestion-relevant: `WatcherConnect` → `int`, `SendTranscriptBatchAcked` →
`TranscriptBatchAck`, `WatcherDrainComplete`, `SendTitle`/`UpdateTitle`; server→client
`AckProcessedPrefix`, `AckResolvedCandidates`, `ActiveSessionAdded/Changed/Removed`.

Everything else on the hub is the agent-launch plane (`LaunchAgent`, `StopAgent`,
`SendInput`, `SendSpecialKey`, `ResizeTerminalAggregate`), the ACP hosted-agent runtime,
evals, and flows.

---

## 5. Client command surface (inherited — all of this already works)

```
setup · status · login · logout · whoami
profile add|list|remove|show · use · machine create|list|revoke
config show|set · ignore · remap
harness list|dismiss|reset
agent start|ls|attach|stop
daemon start|stop|status|doctor · repos [add|remove]
projects · project <slug>
errors · recap · validate-plan · eval · generate-whats-done · import
set-title · disable · hide · review <pr>
curate apply
mcp review|judge|sessions|flows|memory|workitems|analytics
plugin install|remove  [--codex|--cursor|--copilot|--gemini|--kiro|--pi|--opencode|--antigravity|--skills]
update · feedback · cleanup · uninstall
hook --claude|--codex|--cursor|--copilot|--gemini|--kiro|--pi|--opencode|--antigravity
```

Env: `KCAP_URL`, `KCAP_SESSION_ID`, `CODEX_THREAD_ID`, `KCAP_DAEMON_NAME`.

---

## 6. Vendors

**Import sources (10):** Claude · Codex · Cursor · Copilot · Gemini · Kiro · Kimi · Pi ·
OpenCode · Antigravity. Registered at two sites — `Program.cs` and `SetupCommand.cs` —
both required.

**Live harnesses (9):** the same minus Kimi, in `HarnessCatalog.cs`. Antigravity, Kiro,
OpenCode and Pi declare `CallbackMayRepeat: true`.

**On-disk data available here:**

```
~/.claude/                     Claude Code — JSONL + sibling subagent dir
~/.codex/                      Codex — rollouts, parent_thread_id linkage
~/.kimi/  ~/.kimi-code/        Kimi — two distinct layouts, both live
~/.local/share/opencode/       OpenCode — SQLite, 22-table drizzle schema
~/.local/share/kilo/kilo.db    Kilo CLI — an OpenCode fork, schema-identical
VS Code globalStorage          Kilo extension — on `hephastus`, not this laptop
```

`hephastus` = `ssh pmacl@192.168.0.220`, **LAN only** (its Tailscale is dead — do not try
to fix it). Default shell is `cmd.exe`: `&` not `;`, `findstr` not `grep`.

---

## 7. kcap coupling to cut

Detaching from Kurrent's hosted service, in rough order of importance:

1. **Telemetry → PostHog.** `src/Capacitor.Cli.Core/Telemetry/` — 14 files
   (`TelemetryClient`, `PostHogPayload`, `TelemetrySpool`, `SetupFunnel`, …). Honours
   `DO_NOT_TRACK` and `config set telemetry off`, but the default path reports to
   Kurrent. Cut or repoint.
2. **Hosted URL defaults.** A bare profile slug expands to `{slug}.kcap.ai`
   (`SetupCommand.cs:311`, `SpectreTenantProvisioner.cs:93`). Tenant provisioning calls
   Kurrent's API.
3. **Auth.** WorkOS OAuth. **Escape hatch: a profile with `auth_provider: null` resolves
   to provider `None` and posts unauthenticated** — start there.
4. **Update check.** `update_check` / `update` pings Kurrent's npm channel.
5. **Feedback.** `kcap feedback` posts to Kurrent support.
6. **git remote.** Already removed.

**What stays.** `LICENSE.md` is Kurrent License v1: it grants "use, copy, distribute,
make available, and prepare derivative works," so building on this is permitted. It also
forbids removing or obscuring licensing/copyright notices, and forbids offering the
software to third parties as a hosted service exposing a substantial set of its features.
Keep `LICENSE.md` and record provenance in the README. Internal use is fine; SaaS is not.

---

## 8. Summary — what is actually left to build

| layer | state |
|---|---|
| discovery, classification, transport, resume, spool | **have it** |
| live hooks, watcher, daemon, MCP, desktop app | **have it** |
| CLI UX, config, profiles, plugin install | **have it** |
| **normalizers** (vendor JSONL → canonical events) | **build** |
| **canonical model** (event / turn / subagent) | **build** |
| **store** (idempotent on `session_id, agent_id, line_number`) | **build** |
| **the eight routes** | **build** |
| **console** (Blazor + MudBlazor gets you most of the way) | **build** |
| evals, flows, ACP runtime, analytics, work items, memory | **decide** — see the cut |
