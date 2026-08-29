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
