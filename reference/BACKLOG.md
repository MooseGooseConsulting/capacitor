# Backlog — scope captured, not yet planned

Things established as wanted or needed, recorded so they are not lost. Not ordered, not
committed to. The feature cut (`PROMPT.md`) decides what of this is in.

---

## 1. Vendors

### Kilo CLI — nearly free

`~/.local/share/kilo/kilo.db` is an **OpenCode fork**: 22 tables, same drizzle
migrations, same `session` / `message` / `part` / `event_sequence` shape, and
`~/.config/kilo/` mirrors `~/.config/opencode/` down to the plugin layout.

**Requirement:** verify the schema is still identical, then Kilo is the OpenCode import
source and the OpenCode normalizer with a different root path and vendor tag. If the
schemas have drifted, the diff is the work — and it will be small.

Do **not** assume the reverse is cheap: see Kilo VS Code below.

### Kilo VS Code extension — a genuinely different format

`kilocode.kilo-code` task folders holding `api_conversation_history.json` +
`ui_messages.json`. Unrelated to the CLI's SQLite. `MooseGooseConsulting/llm-archiver`
already has a parser for this shape.

**Not on this laptop. Confirmed present on `hephastus`.** Per-node discovery, per
`FLEET.md` — "this vendor has no data" is a statement about a node, never the fleet.

### Kimi — written, but not in this repo

A complete `KimiImportSource` (232 lines: discovery across both on-disk layouts,
classification, per-child watermarks, subagent attachment) exists on
`MooseGooseConsulting/kcap-cli` branch `feat/kimi-history-import`, PR #1. **This repo is
Kurrent's `main`, so it is absent here** — `src/Capacitor.Cli/Harness/` has no `Kimi/`.

**Requirement:** port it. Two on-disk layouts, both live: `~/.kimi`
(`<group>/<guid>/wire.jsonl`, subagents under `subagents/`) and `~/.kimi-code`
(`session_<id>/agents/main/wire.jsonl`, siblings under `agents/`).

Kimi is **import-only** kcap — no `HarnessCatalog` entry, no live hook. Live capture
was specified and never built; that spec is the only place it is written down and it is
not in this repo either.

### Letta — a different *kind* of source

Letta has no useful on-disk transcript; conversations live behind a cloud API:

```
GET /v1/conversations?agent_id=
GET /v1/conversations/{id}/messages
GET /v1/agents/{id}/export?conversation_id=      (supports scrub_messages)
```

**This does not fit `IImportSource` as written.** Every existing source assumes local
files, and `IsAvailable` means "the root data dir exists on this machine." A remote
source is a new shape: paginated pulls, API credentials, rate limits, and no watermark
file to resume from — though `GET /api/sessions/{id}/last-line` still gives the resume
point.

Repo attribution, which looked like the hard part, is largely solved:
`~/.letta/desktop-cwd-map.json` maps conversation → cwd (16 entries observed), and
`~/.letta/agent-folders.json` covers the rest.

**Requirement:** decide whether remote sources are a first-class concept
(`IRemoteImportSource`, or a capability flag on the existing interface) before writing
the first one. Getting this wrong means retrofitting when the second remote vendor
arrives — and browser-based chat (ChatGPT, Claude.ai, Gemini Apps) is the obvious second.

---

## 2. Agent configuration and memory — extend, don't invent

**This is already a first-class kcap subsystem.** The instinct to add it is right; the
work is smaller than it looks because three pieces exist.

What already works:

- **Memory *operations* are observed and modelled.**
  `v_an_memory_ops(repo_hash, vendor, session_id, ts, op, memory_system, memory_kind,
  memory_scope, file_key, content_chars, is_error)` where `memory_system` is `'file'`
  (an instruction or auto-memory **file** operation) or `'server'` (a `kcap-memory` MCP
  call), and `memory_kind` is `project_instructions` | `user_instructions` (instruction
  files under an agent config directory, typically the user's global config) |
  `auto_memory`. So the system already records, per vendor, when an agent writes to its
  own memory.
- **Writing config back already happens.** `kcap curate apply` writes promoted
  guidelines into `CLAUDE.md` / `AGENTS.md`, de-duplicating symlinked pairs.
  `kcap plugin install --<vendor>` writes hook and skill configuration for nine agents.
- **Injection at session start already happens.** `MemoryIndexEmitter`,
  `SessionStartMemory/`, and the `kcap-memory` MCP server (search / get / save / update /
  rescope / archive, scoped user | team | org).

What is missing, and is the actual ask:

1. **Snapshot the config and memory files themselves, as content.** Today the system
   records *that* a memory file was written; it does not keep the file. `CLAUDE.md`,
   `AGENTS.md`, `SKILL.md`, `.claude/`, `~/.codex/`, per-tool settings — these are the
   context that produced every session, and they change constantly. Versioned, per
   machine, they belong in the corpus for the same reason the transcripts do.
2. **Sync and configure across the fleet.** Push a config to every node, or reconcile
   drift between nodes. Today `plugin install` is per-machine and manual.

**Is a daemon the right home? Yes** — and one already exists, per machine, with the
right primitives: it knows its repo paths (`kcap repos`), it already writes agent config,
and it already holds a server connection. The fleet framing makes it more natural, not
less: config sync across N nodes is exactly what a per-node daemon is for.

Two things to decide before building:

- **Snapshot vs. sync are different products.** Capture is read-only and safe. Pushing
  config *mutates the machines that produce your corpus*, which is a much bigger
  commitment and a real footgun (a bad push breaks every agent on every node at once).
  They should probably ship in that order, and possibly the second never ships.
- **Secrets.** Agent config directories contain API keys and tokens. The client already
  has a redaction path with a hard-won invariant (rewrite decoded JSON string values,
  never the serialized line). Any config snapshot must go through something equivalent,
  or it becomes the worst file in the corpus.

---

## 3. Model divergences from kcap

Both are cases where "match kcap" is the wrong instruction, because kcap's model
has a gap that its own product doesn't feel.

- **Machine dimension.** `v_an_sessions` has no machine or host column. See
  `FLEET.md` §3. One column and one payload field now; an unreconstructable backfill
  later.
- **Cross-repository sessions.** A session can span multiple working directories and
  therefore multiple repositories — measured, not assumed: 13 of 365 Claude transcripts
  on this laptop, one spanning 7 directories across 5 repos. The model attributes a
  session to exactly one repo. kcap half-noticed and added mid-session repo
  *replacement* (`ShouldReplaceRepository`, `RepoEvidenceScanner`) rather than
  accumulation, while `SessionTranscriptLocator.cs:59` still asserts "a cwd never
  changes." See `CROSS-REPO-SESSIONS.md` for the per-vendor measurement and the proposed
  model change.

---

## 4. Evaluation — keep the execution primitive

**Decision: keep evals.** Do not cut the existing eval execution machinery during the
server rebuild or feature cut.

The useful capability is broader than kcap's current fixed 13-question product score:
Capacitor needs a generic way to dispatch a versioned set of independent, grounded
questions over any captured or replayed session, persist the per-question verdicts, and
compare them later as part of corpus learning and counterfactual experiments.

Cost is not a reason to remove this. The intended steady-state judge path is **free or
local models**. Judge/model routing therefore needs to become provider-agnostic enough to
support local model endpoints and free hosted endpoints as first-class choices. Paid
hosted judges are optional, not an architectural dependency.

Preserve the good inherited properties while generalizing it:

- one evaluator question per independent invocation;
- read-only, session-scoped evidence access for large traces;
- versioned evaluator/question text;
- persisted per-question findings, not only an aggregate score;
- model/backend identity recorded with every eval run;
- no requirement to auto-evaluate every captured session.

---

## 5. Open questions worth an answer before they cost something

- Does an unsupported `vendor` tag get rejected by the server, or silently accepted and
  dropped during normalization? Unverified; settle by measurement (`PROMPT.md`).
- How does a fleet node get a new binary, now that kcap's npm channel is severed?
- Do we keep per-session visibility (`--private`, `hide`, `org_public`)? Trivial to keep,
  awkward to retrofit.
