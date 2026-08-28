# Analytics Views Specification

Live `get_analytics_schema` inventories **32 view names**. This wave records that inventory and defines SQL for the 9 views the session list, the MCP tour's Q-MENU query, and the query governor's own tests ground against real tables. Remaining names are placeholders — do not invent 23 more view bodies to match the count; each of the rest needs base tables (users, teams, deployments, code changes, ...) no wave has created yet.

MCP wire: `GET /api/analytics/schema` and `POST /api/analytics/query` with body `{sql, repos, max_rows}`.

---

## 1. View Inventory by Category

| Category | View Name | Description |
| :--- | :--- | :--- |
| **Sessions (10)** | `v_an_sessions` | Core session rollups, status, phases, duration, event counts |
| | `v_an_session_steps` | Detailed step-by-step turns, tools, latency, errors |
| | `v_an_context` | Context window occupancy and memory usage |
| | `v_an_cost` | Financial spend and token rollups by session |
| | `v_an_token_usage_by_model` | Input, output, and cached token totals grouped by model |
| | `v_an_tool_usage` | Per-session invocations and `errors` (joinable to `v_an_sessions`) |
| | `v_an_skill_usage` | Skill triggers and outcomes |
| | `v_an_subagent_runs` | Subagent hierarchy, durations, and nesting |
| | `v_an_memory_ops` | Memory fetches, writes, and rescopes |
| | `v_an_incident_signals` | Anomalies, runaway loops, high-error sessions |
| **Code (3)** | `v_an_code_changes` | Diffs, additions, deletions |
| | `v_an_file_changes` | Modified file paths and frequencies |
| | `v_an_commits` | Commits generated and associated with sessions |
| **PRs (5)** | `v_an_prs` | Pull request metadata, review state |
| | `v_an_pr_sessions` | Sessions contributing to specific PRs |
| | `v_an_pr_churn` | Code churn and revisions per PR |
| | `v_an_pr_churn_summary` | Aggregate churn metrics across repos |
| | `v_an_pr_test_runs` | Test execution outcomes on PR branches |
| **Work Items (4)** | `v_an_work_items` | Tracked issues, tasks, and feature items |
| | `v_an_work_item_sessions` | Sessions associated with work items |
| | `v_an_work_item_links` | Live session↔work-item link rows — **not** `work_item_relations` (`blocks` / `blocked_by`) |
| | `v_an_work_item_milestones` | Progress milestones and completion state |
| **Evals (2)** | `v_an_eval_scores` | Per-question and per-category judge scores |
| | `v_an_eval_summaries` | Retrospective summaries and suggestion clusters |
| **Deployments (4)**| `v_an_deployments` | Release and deployment events |
| | `v_an_deployment_coverage` | Verification coverage per deployment |
| | `v_an_deployment_status_uncertainties` | Unverified deployment states |
| | `v_an_release_publications` | Released versions and changelog rollups |
| **Org & Repo (4)** | `v_an_users` | Team member activity and seat usage |
| | `v_an_repositories` | Connected repos, branches, and activity |
| | `v_an_team_memberships` | User-to-team mappings |
| | `v_an_user_primary_team` | Primary team associations |

---

SQL below is the subset this wave can ground. `v_an_users` is used by the guided-tour queries and is **not** defined here yet — it needs a users/teams table no wave has created. `machine_id` on `v_an_sessions` is an additive fork column (`FLEET.md`).

## 2. Core SQL View Definitions

### `v_an_sessions`
```sql
CREATE VIEW v_an_sessions AS
SELECT
    s.repo_hash,
    s.session_id,
    s.model,
    s.vendor,
    s.status,
    s.visibility,
    s.started_at,
    s.ended_at,
    s.owner_user_id,
    s.event_count,
    s.last_event_at,
    s.hidden_reason,
    s.previous_session_id,
    s.next_session_id,
    s.primary_phase,
    s.secondary_phase,
    s.classification_confidence,
    s.classification_source,
    s.disposition,
    s.duration_min,
    s.total_tokens,
    s.total_cost_usd,
    s.machine_id
FROM sessions s;
```

### `v_an_token_usage_by_model`
```sql
CREATE VIEW v_an_token_usage_by_model AS
SELECT
    s.repo_hash,
    e.vendor,
    e.model,
    COUNT(DISTINCT e.session_id) AS session_count,
    SUM(e.input_tokens) AS total_input_tokens,
    SUM(e.output_tokens) AS total_output_tokens,
    SUM(e.cache_read_tokens) AS total_cache_read_tokens,
    SUM(e.cache_write_tokens) AS total_cache_write_tokens,
    SUM(e.cost_usd) AS total_cost_usd
FROM session_events e
JOIN sessions s ON e.session_id = s.session_id
GROUP BY s.repo_hash, e.vendor, e.model;
```

### `v_an_tool_usage`
Per-session grain so tour SQL can `JOIN v_an_sessions` and `WHERE errors > 0`. Not a repo×vendor×tool rollup.

```sql
CREATE VIEW v_an_tool_usage AS
SELECT
    s.repo_hash,
    e.session_id,
    e.vendor,
    e.tool_name,
    COUNT(*) AS invocation_count,
    SUM(CASE WHEN e.is_error THEN 1 ELSE 0 END) AS errors
FROM session_events e
JOIN sessions s ON e.session_id = s.session_id
WHERE e.tool_name IS NOT NULL
GROUP BY s.repo_hash, e.session_id, e.vendor, e.tool_name;
```

### `v_an_eval_scores`
```sql
CREATE VIEW v_an_eval_scores AS
SELECT
    s.repo_hash,
    r.session_id,
    r.eval_run_id,
    r.judge_model,
    r.overall_score,
    v.category,
    v.question_id,
    v.score,
    v.verdict,
    v.tools_used,
    r.evaluated_at
FROM eval_runs r
JOIN sessions s ON r.session_id = s.session_id
JOIN eval_verdicts v ON r.eval_run_id = v.eval_run_id;
```

### `v_an_cost`
One row per session per model — the Q-MENU query in `kcap/skills/guided-tour/SKILL.md` collapses it to one row per session before joining.

```sql
CREATE VIEW v_an_cost AS
SELECT
    s.repo_hash,
    e.session_id,
    e.model,
    SUM(e.cost_usd) AS cost_usd,
    SUM(e.input_tokens) AS input_tokens,
    SUM(e.output_tokens) AS output_tokens,
    SUM(e.cache_read_tokens) AS cache_read_tokens,
    SUM(e.cache_write_tokens) AS cache_write_tokens
FROM session_events e
JOIN sessions s ON e.session_id = s.session_id
GROUP BY s.repo_hash, e.session_id, e.model;
```

### `v_an_session_steps`
Per-event grain. No `latency_ms` column: nothing in the schema records per-step latency yet, so the guided-tour's `v_an_session_steps.latency_ms` claim stays unmet until a wave adds that column to `session_events`.

```sql
CREATE VIEW v_an_session_steps AS
SELECT
    s.repo_hash,
    e.session_id,
    e.line_number,
    e.event_type,
    e.vendor,
    e.tool_name,
    e.is_error,
    e.timestamp
FROM session_events e
JOIN sessions s ON e.session_id = s.session_id;
```

### `v_an_prs`
`sessions` carries PR fields directly — there is no separate `prs` table.

```sql
CREATE VIEW v_an_prs AS
SELECT
    s.repo_hash,
    s.pr_number,
    s.pr_title,
    s.pr_url,
    s.pr_head_ref,
    COUNT(DISTINCT s.session_id) AS session_count,
    MAX(s.last_event_at) AS last_session_at
FROM sessions s
WHERE s.pr_number IS NOT NULL
GROUP BY s.repo_hash, s.pr_number, s.pr_title, s.pr_url, s.pr_head_ref;
```

### `v_an_repositories`
```sql
CREATE VIEW v_an_repositories AS
SELECT
    s.repo_hash,
    s.repo_owner AS owner,
    s.repo_name,
    COUNT(DISTINCT s.session_id) AS session_count,
    MAX(s.last_event_at) AS last_activity_at
FROM sessions s
WHERE s.repo_hash IS NOT NULL
GROUP BY s.repo_hash, s.repo_owner, s.repo_name;
```
