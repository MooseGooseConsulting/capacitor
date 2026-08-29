-- ============================================================================
-- 002_analytics_views.sql: Analytics Views for Capacitor Server
-- Compatible with SQLite and PostgreSQL
-- ============================================================================

DROP VIEW IF EXISTS v_an_sessions;
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

DROP VIEW IF EXISTS v_an_token_usage_by_model;
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

DROP VIEW IF EXISTS v_an_tool_usage;
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

DROP VIEW IF EXISTS v_an_eval_scores;
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

DROP VIEW IF EXISTS v_an_work_items;
CREATE VIEW v_an_work_items AS
SELECT
    w.repo_hash,
    w.work_item_id,
    w.title,
    w.issue_key,
    w.pr_number,
    w.status,
    COUNT(DISTINCT ws.session_id) AS session_count,
    w.created_at,
    w.updated_at
FROM work_items w
LEFT JOIN work_item_sessions ws ON w.work_item_id = ws.work_item_id
GROUP BY w.repo_hash, w.work_item_id, w.title, w.issue_key, w.pr_number, w.status, w.created_at, w.updated_at;

-- One row per session per model; kcap/skills/guided-tour depends on this exact grain.
DROP VIEW IF EXISTS v_an_cost;
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

DROP VIEW IF EXISTS v_an_session_steps;
CREATE VIEW v_an_session_steps AS
SELECT
    s.repo_hash,
    e.session_id,
    e.agent_id,
    e.line_number,
    e.logical_seq,
    e.event_type,
    e.vendor,
    e.tool_name,
    e.is_error,
    e.timestamp
FROM session_events e
JOIN sessions s ON e.session_id = s.session_id;

DROP VIEW IF EXISTS v_an_prs;
CREATE VIEW v_an_prs AS
SELECT
    s.repo_hash,
    s.pr_number,
    (
        SELECT s2.pr_title FROM sessions s2
        WHERE s2.repo_hash IS s.repo_hash AND s2.pr_number = s.pr_number
        ORDER BY s2.last_event_at DESC, s2.session_id DESC
        LIMIT 1
    ) AS pr_title,
    (
        SELECT s2.pr_url FROM sessions s2
        WHERE s2.repo_hash IS s.repo_hash AND s2.pr_number = s.pr_number
        ORDER BY s2.last_event_at DESC, s2.session_id DESC
        LIMIT 1
    ) AS pr_url,
    (
        SELECT s2.pr_head_ref FROM sessions s2
        WHERE s2.repo_hash IS s.repo_hash AND s2.pr_number = s.pr_number
        ORDER BY s2.last_event_at DESC, s2.session_id DESC
        LIMIT 1
    ) AS pr_head_ref,
    COUNT(DISTINCT s.session_id) AS session_count,
    MAX(s.last_event_at) AS last_session_at
FROM sessions s
WHERE s.pr_number IS NOT NULL
GROUP BY s.repo_hash, s.pr_number;

DROP VIEW IF EXISTS v_an_repositories;
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
