# Analytics Views Specification (32 Governed SQL Views)

This document defines the 32 analytical SQL views served by the Capacitor Server over the canonical schema.

These views provide governed, read-only analytics for the web console and the `kcap mcp analytics` tool surface (`get_analytics_schema` / `query_analytics`).

---

## 1. View Inventory by Category

| Category | View Name | Description |
| :--- | :--- | :--- |
| **Sessions (10)** | `v_an_sessions` | Core session rollups, status, phases, duration, event counts |
| | `v_an_session_steps` | Detailed step-by-step turns, tools, latency, errors |
| | `v_an_context` | Context window occupancy and memory usage |
| | `v_an_cost` | Financial spend and token rollups by session |
| | `v_an_token_usage_by_model` | Input, output, and cached token totals grouped by model |
| | `v_an_tool_usage` | Invocations, error counts, and latency grouped by tool |
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
| | `v_an_work_item_links` | Dependency graph (blocks / blocked_by) |
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
    s.previous_session_id,
    s.next_session_id,
    s.primary_phase,
    s.secondary_phase,
    s.classification_confidence,
    s.classification_source,
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
```sql
CREATE VIEW v_an_tool_usage AS
SELECT
    s.repo_hash,
    e.vendor,
    e.tool_name,
    COUNT(*) AS invocation_count,
    SUM(CASE WHEN e.is_error THEN 1 ELSE 0 END) AS error_count,
    ROUND(SUM(CASE WHEN e.is_error THEN 1.0 ELSE 0.0 END) / COUNT(*), 4) AS error_rate
FROM session_events e
JOIN sessions s ON e.session_id = s.session_id
WHERE e.tool_name IS NOT NULL
GROUP BY s.repo_hash, e.vendor, e.tool_name;
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
