# Cross-repo sessions: does one session mean one repository?

Measured 2026-08-27 on `icarus-laptop` from on-disk vendor data. Every number below came from
a scan of the vendor's own store, not from documentation.

Scanning scripts:
`%LOCALAPPDATA%\Temp\claude\C---projects\70dc37b2-.../scratchpad\{codex_repos,codex_workdir2,oc_scan,kimi_scan,cursor_scan2}.py`

"Repo root" throughout = distinct first-level directory under `C:\_projects\`, which is how every
checkout on this node is laid out. Sub-paths of one checkout are folded into their root, so the
counts are of *repositories*, not of directories.

---

## The measurement

| Vendor | Store | Is cwd recorded? At what granularity? | Sessions | >1 distinct cwd | >1 distinct repo root |
|---|---|---|---|---|---|
| **Claude Code** | `~/.claude/projects/**/*.jsonl` | Yes — `cwd` on **every event record** | 378 | **13** | max observed 7 dirs / 5 repos |
| **Codex CLI** | `~/.codex/sessions/**/rollout-*.jsonl` + `archived_sessions/` | Yes — `session_meta.cwd` (session), `turn_context.cwd` (**per turn**), `turn_context.workspace_roots[]` (**array, per turn**), `workdir` (**per tool call**) | 1235 | 1 by `turn_context.cwd`; **434** by tool `workdir` | **493** (40%) |
| **OpenCode** | `~/.local/share/opencode/opencode.db` | Yes — `session.directory` (session, singular), `message.data.path.{cwd,root}` (**per message**), `part.data.state.input.workdir` (**per bash call**), `project_directory` table (**many dirs per project**) | 405 | 70 by message cwd/root; 14 by bash workdir | **2** |
| **Kilo CLI** | `~/.local/share/kilo/kilo.db` (schema-identical) | Same as OpenCode | 79 | 5 by message cwd/root; 4 by bash workdir | **1** |
| **Kimi Code** | `~/.kimi-code/sessions/**/wire.jsonl` | Yes — `environmentDisclosure.cwd` / `profile.bind.environmentDisclosure.cwd`, **per event** | 25 | **3** | **2** |
| **Kimi (legacy)** | `~/.kimi/sessions/**/{wire,context*}.jsonl` | Field exists, **always `null`** (39/39 occurrences) | 164 | unmeasurable | unmeasurable |
| **Cursor CLI** | `~/.cursor/chats/*/*/{meta.json,store.db}` | Yes — `meta.json.cwd` (per chat), `Workspace Path:` in each user message | 83 | **0** | **0** |
| **Antigravity** | `~/.gemini/antigravity-cli/` | Yes — `conversation_summaries.workspace_uris` is a **JSON array** (plural by design) | 60 summaries / 150 conversation DBs | **0 of 60** have >1 `workspace_uris` entry | 18 of 99 conversation DBs *reference* >1 real project dir in step blobs — **low confidence**, see caveat |
| **Gemini CLI** | `~/.gemini/tmp/<project-slug>/chats/*.jsonl` | **No cwd field at all.** Partitioned by project directory; records carry a single `projectHash` | 62 files (19 with `projectHash`) | **0** | **0** |
| **GitHub Copilot CLI** | `~/.copilot/` | **No transcripts on disk.** Only `config.json`, hooks, and four 392–704-byte process logs; zero `cwd`/`workdir` keys anywhere. `%LOCALAPPDATA%\copilot` is the 178 MB binary package only | 0 | n/a | n/a |
| **Kiro** | — | **No data on this node** (`~/.kiro` absent) | — | — | — |
| **Pi** | — | **No data on this node** (`~/.pi` absent) | — | — | — |

Absence on this node is not absence on the fleet — Copilot CLI, Kiro, and Pi may all have stores
on other machines. Treat those three rows as unmeasured, not as negative results.

### Caveats on specific numbers

- **Claude 378 vs. the 365 in the brief.** The transcript count grew between the two scans; the
  finding is unchanged — 13 multi-cwd sessions either way. The 7-directory example is
  `70dc37b2-b3b1-4f13-9c15-3858abbe88a8.jsonl`: `kcap-cli` (586 lines), `agent-corpus` (206),
  `capacitor` (196), `_projects` (178), `llm-archiver` (53), `capacitor\reference\ui-assets\css` (25),
  `frozenSkillz` (2) — 5 repositories.
- **Antigravity's 18/99.** That row comes from byte-scanning protobuf `steps` blobs for path
  literals, filtered against the 46 real directories in `C:\_projects`. It proves those conversations
  *mention* several repos; it does not prove the agent worked in them. The trustworthy structured
  signal is `workspace_uris`, and there **0 of 60** conversations have more than one entry.
  Antigravity's schema is plural; this node's data is not. Marked unmeasured for the stronger claim.
- **Kimi legacy.** `"cwd": null` in all 39 occurrences. The session cannot be attributed to a repo
  from its transcript at all — a distinct failure mode from "one repo per session".

### Concrete examples

**Codex** — `rollout-2026-07-16T16-37-20-019f6cdc-...jsonl`, `session_meta.cwd = C:\_projects\coldaine-homelab`,
tool `workdir` values spanning 10 repos: `agent-control-plane`, `agent-systems-exploration`,
`ai-config-registry`, `coldaine-configurations`, `coldaine-homelab`, `coldaine-infra`,
`coldaine-k8cluster`, `frozenSkillz`, `HUGEIMPORTANTOS`, `repo-audit`.

Codex root-count distribution for the 493 multi-repo sessions:
`{2: 397, 3: 51, 4: 19, 5: 6, 6: 10, 7: 5, 8: 2, 10: 3}`.

**Codex `workspace_roots`** — 55 rollouts carry more than one non-`.codex` root. The most common
pair, 37 times: `C:\_projects\pieces_memory_observations` + `C:\_projects\screenpipe-goal-1`.
Distribution of `workspace_roots` array length across all 1235: `{0: 218, 1: 899, 2: 78, 3: 40}`.

**OpenCode** — `ses_190222f15ffexg6Mjd1EOzTsus`, `session.directory = C:/_projects/screenpipe - Copy`
(228 refs), also `C:\_projects\screenpipe-vsinsiders-trunk` (16). The `project_directory` table
independently records `RobotOverview` and `RobotOverview-deploy` under one `project_id`, the
second with `strategy = git_worktree`.

**Kilo** — `ses_054b68a88ffedBMVbLBsGpAxNm`, `session.directory = C:/_projects/proxmox-stateful`
(178), also `C:\_projects\coldaine-homelab` (18).

**Kimi Code** — `session_3e6ae546-01d3-4821-bfc2-14397fcf5348`:
`coldaine-homelab-smoke-628` (147), `wt-router` (20), `wt-hygiene` (9). Three repos, in a session
whose own directory name pins it to one workspace.

---

## Can each format distinguish "the agent cd'd" from "a tool ran elsewhere"?

| Vendor | Answer |
|---|---|
| **Codex** | **Yes, cleanly.** `turn_context.cwd` is the agent's own directory; `workdir` on each `exec` call is where that command ran. The split is stark: the agent's cwd changed in **1** session, but tool `workdir` varied in **434**. Nearly all Codex cross-repo work is tools reaching out, not the agent relocating. |
| **OpenCode / Kilo** | **Yes.** `message.data.path.{cwd,root}` is the agent's position per message; `part.data.state.input.workdir` is per bash invocation. `root` vs `cwd` additionally separates repo root from working subdirectory. |
| **Claude Code** | **No.** One `cwd` per event, reflecting the agent's own directory. A `Bash` call with an explicit `cd` inside the command string leaves no structured trace — its directory is buried in unparsed shell text. |
| **Kimi Code** | **No.** `environmentDisclosure.cwd` is the agent's environment; there is no per-tool directory field. |
| **Cursor** | **Not applicable here** — one workspace per chat, no variation to attribute. |
| **Antigravity** | **Unmeasured.** Step payloads are opaque protobuf blobs; a reliable per-tool directory field was not identified without a schema. |
| **Gemini CLI** | **No** — no directory is recorded at any level. |

---

## The conclusion

**One-repo-per-session is not a safe assumption for any vendor that records enough to test it.**

Three vendors positively disprove it in their own data: Claude Code (13/378), Codex (493/1235 —
40%), Kimi Code (2/25). Two more show it at low rates: OpenCode (2/405), Kilo (1/79).

The vendors that *appear* to hold the assumption do so for reasons that don't generalize:

- **Cursor** (0/83) pins one workspace per chat at the UI level — a product constraint, not a
  property of coding work.
- **Gemini CLI** (0/62) partitions its store *by project directory*. It cannot record a
  cross-repo session because the file layout has nowhere to put one. This is the assumption
  baked into a storage format, and it is exactly the mistake to avoid copying.
- **Antigravity** already models `workspace_uris` as an array. Its schema disagrees with
  one-repo-per-session even though this node's 60 conversations happen not to exercise it.
- **Kimi legacy** records `null` and attributes to nothing at all.

The strongest evidence is Codex, and it reframes the problem. Codex sessions almost never *move*
(1 session changed `turn_context.cwd`), yet 40% of them **touch** several repos, via per-tool
`workdir`. So the model isn't wrong because agents wander between checkouts — it's wrong because a
session's work is not confined to the directory the session started in. Capturing only the launch
cwd would have missed 492 of Codex's 493 cross-repo sessions on this node alone.

Codex, OpenCode, and Antigravity have each independently reached for a *plural* representation —
`workspace_roots[]`, the `project_directory` table, `workspace_uris[]`. Three vendors converging on
many-directories-per-session is a strong signal that the singular model is the outlier.

---

## What needs to happen

### 1. cwd/repo becomes an attribute of the event, not (only) the session

**Take this.** It is the only change that matches what every format actually stores. Claude, Codex,
OpenCode, Kilo, and Kimi Code all record directory *per record* — the session-level value is a
derived summary in every one of them, and `SessionTranscriptLocator` is currently the only place
that treats it as ground truth.

Add a nullable `repo_hash` (and cwd) to the event/turn row, populated from whatever the vendor
gives at the finest granularity available:

| Vendor | Event-level source |
|---|---|
| Claude | `cwd` on each record |
| Codex | `turn_context.cwd`; `workdir` per `exec` call |
| OpenCode / Kilo | `message.data.path.cwd`; `part` bash `workdir` |
| Kimi Code | `environmentDisclosure.cwd` |
| Cursor | `meta.json.cwd` (constant) |
| Gemini / Antigravity / Kimi legacy | null — nothing to populate from |

Nullable matters: three vendors cannot supply it, so every downstream query must already tolerate
absence. Do not backfill a null event repo from the session's repo — that manufactures the exact
false attribution this document exists to stop.

### 2. session→repository becomes many, with a primary

**Take this too** — it is the read model over (1), not an alternative to it.

- `session_repositories(session_id, repo_hash, is_primary, first_seen_event, event_count)`.
- Primary = the repo with the most events, not the launch cwd. In the Codex 10-repo example the
  launch cwd (`coldaine-homelab`) is genuinely the primary; in the OpenCode `screenpipe - Copy`
  example it is too. But nothing guarantees this, and picking by evidence weight is no harder than
  picking by launch order.
- Keep `v_an_sessions.repo_hash` as the primary, so existing consumers keep working, and let new
  consumers join the many-table. That makes this additive rather than a breaking migration.
- The wire `repository` payload becomes a list, or gains a `repositories[]` alongside the
  existing singular field for one release.

### 3. What breaks if it stays one-to-one

Present tense — these are current defects, not future risks:

- **Console left-rail repo filter.** Filtering to `agent-corpus` hides the Claude session above,
  which spent 206 events there, because it attributed to `kcap-cli`. The filter under-reports, and
  silently — the user sees a shorter list, never a warning.
- **`repo_hash` scoping in the analytics views.** Per-repo cost, token, and error rollups charge a
  session's entire spend to one repo. For the Codex 10-repo session, 100% of the cost lands on
  `coldaine-homelab`; nine repos read as zero-activity. With 40% of Codex sessions multi-repo, this
  is not a tail case — per-repo analytics on Codex data are systematically wrong.
- **PR attribution.** A session that edits repo A and opens a PR in repo B attributes the PR to A.
  The Codex `_codex-worktrees` sessions are precisely this shape: work in a worktree,
  PR against the parent repo.
- **`scope: 'repo'` memory queries.** `McpMemoryServer.cs:305` sends `["repo_hash"] = global ? null : cwdRepoHash`.
  A memory saved while the agent was in repo B is scoped to B; the rest of the session, back in A,
  cannot retrieve it. Recall silently fails, which is worse than an error — the agent proceeds
  without the memory and never knows.

### 4. Fix the two places upstream already half-noticed

- **`WatchCommand.ShouldReplaceRepository` (`src/Capacitor.Cli/Commands/WatchCommand.cs:3307`)** and its
  caller at `:758` re-probe the repo every 60s and **replace** `state.Repository`. Replacement is
  the bug: the previous repo's events were real and are now misattributed to whatever the probe
  found last. Change to accumulate into a set, and derive the primary from event counts.
  `RepositoryFromEvidence` (`Models.cs:133`) exists to stop a null probe from *clearing* the repo —
  the same instinct, one step short. Accumulating makes that guard unnecessary.
- **`SessionTranscriptLocator.cs:59`** documents its rule-out cache as safe because
  *"a cwd never changes"*. The Claude data disproves this directly: 13 of 378 transcripts change cwd,
  one across 7 directories. The locator rules out a file whose early `cwd` is "foreign" and never
  re-checks it — so a session that starts in the source repo and later moves into the worktree is
  permanently ruled out and never located. Match on *any* cwd seen so far rather than the first, and
  only rule out a file once it is positively claimed by another session id.

### Sequencing

(4) is a correctness fix against data already in hand and is independent of the schema work — do it
first. (1) then (2); (2)'s read model depends on (1)'s event rows existing. Nothing here requires
the wire format to break, provided the singular `repository` field is kept alongside the plural one
for a release.
