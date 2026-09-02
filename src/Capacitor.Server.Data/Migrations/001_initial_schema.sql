-- ============================================================================
-- 001_initial_schema.sql: Canonical Schema for Capacitor Server
-- Compatible with SQLite and PostgreSQL
-- ============================================================================

CREATE TABLE IF NOT EXISTS session_events (
    session_id          VARCHAR(64) NOT NULL,
    agent_id            VARCHAR(64) NOT NULL DEFAULT '',
    line_number         INTEGER NOT NULL,
    logical_seq         BIGINT NOT NULL DEFAULT 0,
    event_id            VARCHAR(64),
    event_type          VARCHAR(64) NOT NULL,
    vendor              VARCHAR(32) NOT NULL,
    model               VARCHAR(64),
    timestamp           VARCHAR(35) NOT NULL,
    input_tokens        BIGINT,
    output_tokens       BIGINT,
    cache_read_tokens   BIGINT,
    cache_write_tokens  BIGINT,
    reasoning_tokens    BIGINT,
    context_used_tokens BIGINT,
    context_window_tokens BIGINT,
    cost_usd            NUMERIC(10, 6),
    item_id             VARCHAR(64),
    tool_server         VARCHAR(64),
    tool_name           VARCHAR(64),
    tool_input          TEXT,
    tool_output         TEXT,
    tool_exit_code      INTEGER,
    is_error            BOOLEAN NOT NULL DEFAULT FALSE,
    content             TEXT,
    raw_payload         TEXT,
    cwd                 TEXT,
    repo_hash           VARCHAR(64),
    repo_owner          VARCHAR(128),
    repo_name           VARCHAR(128),
    PRIMARY KEY (session_id, agent_id, line_number)
);

-- The receipt is the source-resume boundary. Normalized events retain the same
-- coordinates but are not a substitute: one receipt can emit no events or many.
CREATE TABLE IF NOT EXISTS transcript_receipts (
    session_id              VARCHAR(64) NOT NULL,
    agent_id                VARCHAR(64) NOT NULL DEFAULT '',
    line_number             INTEGER NOT NULL,
    vendor                  VARCHAR(32) NOT NULL,
    raw_payload             TEXT NOT NULL,
    normalization_status    VARCHAR(16) NOT NULL,
    failure_reason          TEXT,
    cwd                     TEXT,
    repo_hash               VARCHAR(64),
    repo_owner              VARCHAR(128),
    repo_name               VARCHAR(128),
    received_at             VARCHAR(35) NOT NULL,
    updated_at              VARCHAR(35) NOT NULL,
    PRIMARY KEY (session_id, agent_id, line_number)
);

CREATE TABLE IF NOT EXISTS session_repositories (
    session_id              VARCHAR(64) NOT NULL,
    repo_hash               VARCHAR(64) NOT NULL,
    repo_owner              VARCHAR(128),
    repo_name               VARCHAR(128),
    first_seen_line         INTEGER,
    event_count             BIGINT NOT NULL DEFAULT 0,
    is_primary              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              VARCHAR(35) NOT NULL,
    updated_at              VARCHAR(35) NOT NULL,
    PRIMARY KEY (session_id, repo_hash)
);

CREATE TABLE IF NOT EXISTS session_watermarks (
    session_id          VARCHAR(64) NOT NULL,
    agent_id            VARCHAR(64) NOT NULL DEFAULT '',
    last_line_number    INTEGER NOT NULL,
    byte_offset         BIGINT NOT NULL DEFAULT 0,
    updated_at          VARCHAR(35) NOT NULL,
    PRIMARY KEY (session_id, agent_id)
);

CREATE TABLE IF NOT EXISTS sessions (
    session_id                  VARCHAR(64) PRIMARY KEY,
    title                       TEXT,
    slug                        VARCHAR(128),
    vendor                      VARCHAR(32) NOT NULL,
    model                       VARCHAR(64),
    status                      VARCHAR(32) NOT NULL DEFAULT 'active',
    visibility                  VARCHAR(32) NOT NULL DEFAULT 'project',
    hidden_reason               VARCHAR(64),
    disposition                 VARCHAR(32),
    owner_user_id               VARCHAR(64) NOT NULL,
    machine_id                  VARCHAR(64),
    daemon_id                   VARCHAR(64),
    repo_hash                   VARCHAR(64),
    repo_owner                  VARCHAR(128),
    repo_name                   VARCHAR(128),
    branch                      VARCHAR(128),
    pr_number                   INTEGER,
    pr_title                    TEXT,
    pr_url                      TEXT,
    pr_head_ref                 VARCHAR(128),
    started_at                  VARCHAR(35) NOT NULL,
    ended_at                    VARCHAR(35),
    last_event_at               VARCHAR(35),
    duration_min                NUMERIC(8, 2) DEFAULT 0,
    event_count                 INTEGER NOT NULL DEFAULT 0,
    tool_count                  INTEGER,
    total_tokens                BIGINT,
    total_cost_usd              NUMERIC(10, 4),
    previous_session_id         VARCHAR(64),
    next_session_id             VARCHAR(64),
    primary_phase               VARCHAR(32),
    secondary_phase             VARCHAR(32),
    classification_confidence   NUMERIC(3, 2),
    classification_source       VARCHAR(32)
);

CREATE TABLE IF NOT EXISTS subagent_runs (
    parent_session_id   VARCHAR(64) NOT NULL,
    agent_id            VARCHAR(64) NOT NULL,
    agent_type          VARCHAR(64),
    role                VARCHAR(128),
    prompt              TEXT,
    spawned_at          VARCHAR(35) NOT NULL,
    stopped_at          VARCHAR(35),
    duration_ms         BIGINT,
    exit_status         VARCHAR(32),
    PRIMARY KEY (parent_session_id, agent_id)
);

CREATE TABLE IF NOT EXISTS work_items (
    work_item_id        VARCHAR(64) PRIMARY KEY,
    repo_hash           VARCHAR(64) NOT NULL,
    title               TEXT NOT NULL,
    issue_key           VARCHAR(64),
    pr_number           INTEGER,
    status              VARCHAR(32) NOT NULL DEFAULT 'open',
    created_at          VARCHAR(35) NOT NULL,
    updated_at          VARCHAR(35) NOT NULL
);

CREATE TABLE IF NOT EXISTS work_item_sessions (
    work_item_id        VARCHAR(64) NOT NULL,
    session_id          VARCHAR(64) NOT NULL,
    correlation_source  VARCHAR(32) NOT NULL,
    confidence          NUMERIC(3, 2) NOT NULL DEFAULT 1.0,
    attached_at         VARCHAR(35) NOT NULL,
    PRIMARY KEY (work_item_id, session_id)
);

CREATE TABLE IF NOT EXISTS work_item_breakdowns (
    parent_id           VARCHAR(64) NOT NULL,
    part_id             VARCHAR(64) NOT NULL,
    PRIMARY KEY (parent_id, part_id)
);

CREATE TABLE IF NOT EXISTS work_item_relations (
    from_id             VARCHAR(64) NOT NULL,
    to_id               VARCHAR(64) NOT NULL,
    relation_kind       VARCHAR(32) NOT NULL,
    PRIMARY KEY (from_id, to_id, relation_kind)
);

CREATE TABLE IF NOT EXISTS eval_runs (
    eval_run_id                 VARCHAR(64) PRIMARY KEY,
    session_id                  VARCHAR(64) NOT NULL,
    judge_model                 VARCHAR(64) NOT NULL,
    overall_score               INTEGER NOT NULL,
    summary                     TEXT NOT NULL,
    retrospective_json          TEXT,
    retrospective_prompt_version VARCHAR(32),
    evaluated_at                VARCHAR(35) NOT NULL
);

CREATE TABLE IF NOT EXISTS eval_verdicts (
    eval_run_id                 VARCHAR(64) NOT NULL,
    category                    VARCHAR(32) NOT NULL,
    question_id                 VARCHAR(64) NOT NULL,
    score                       INTEGER NOT NULL,
    verdict                     VARCHAR(16) NOT NULL,
    finding                     TEXT NOT NULL,
    evidence                    TEXT,
    recommendation              TEXT,
    tools_used                  INTEGER,
    prompt_version              VARCHAR(32),
    PRIMARY KEY (eval_run_id, question_id)
);

CREATE TABLE IF NOT EXISTS judge_facts (
    fact_hash                   VARCHAR(64) PRIMARY KEY,
    repo_hash                   VARCHAR(64) NOT NULL,
    category                    VARCHAR(32) NOT NULL,
    fact                        TEXT NOT NULL,
    source_session_id           VARCHAR(64) NOT NULL,
    source_eval_run_id          VARCHAR(64) NOT NULL,
    applies_to_vendors          TEXT,
    applies_to_session_kinds    TEXT,
    retained_at                 VARCHAR(35) NOT NULL
);

CREATE TABLE IF NOT EXISTS machines (
    machine_id                  VARCHAR(64) PRIMARY KEY,
    hostname                    VARCHAR(128) NOT NULL,
    os                          VARCHAR(32) NOT NULL,
    arch                        VARCHAR(32) NOT NULL,
    client_id                   VARCHAR(64) UNIQUE,
    registered_at               VARCHAR(35) NOT NULL,
    last_heartbeat              VARCHAR(35) NOT NULL
);

CREATE TABLE IF NOT EXISTS daemons (
    daemon_id                   VARCHAR(64) PRIMARY KEY,
    machine_id                  VARCHAR(64) NOT NULL,
    daemon_name                 VARCHAR(64) NOT NULL,
    advertised_vendors          TEXT NOT NULL,
    max_agents                  INTEGER NOT NULL DEFAULT 4,
    status                      VARCHAR(32) NOT NULL DEFAULT 'connected',
    last_seen_at                VARCHAR(35) NOT NULL
);

CREATE TABLE IF NOT EXISTS dead_letter_entries (
    entry_id                    VARCHAR(64) PRIMARY KEY,
    session_id                  VARCHAR(64) NOT NULL,
    vendor                      VARCHAR(32) NOT NULL,
    line_number                 INTEGER NOT NULL,
    raw_line                    TEXT NOT NULL,
    error_reason                TEXT NOT NULL,
    received_at                 VARCHAR(35) NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_session_events_lookup ON session_events(session_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_session_events_vendor_model ON session_events(vendor, model);
CREATE INDEX IF NOT EXISTS idx_session_events_repository ON session_events(session_id, repo_hash, line_number);
CREATE INDEX IF NOT EXISTS idx_transcript_receipts_stream ON transcript_receipts(session_id, agent_id, line_number);
CREATE INDEX IF NOT EXISTS idx_session_repositories_repo ON session_repositories(repo_hash, session_id);
CREATE INDEX IF NOT EXISTS idx_sessions_repo ON sessions(repo_hash, started_at);
CREATE INDEX IF NOT EXISTS idx_sessions_owner ON sessions(owner_user_id, started_at);
CREATE INDEX IF NOT EXISTS idx_sessions_machine ON sessions(machine_id, started_at);
