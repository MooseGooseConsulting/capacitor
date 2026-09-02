# Canonical Schema Specification

> **Status: derived candidate, not an authoritative migration or a running
> database.** The required product order is [`PROMPT.md`](../../PROMPT.md),
> [`FLEET.md`](../../reference/FLEET.md), [`SURFACE.md`](../../reference/SURFACE.md)
> plus the captured UI assets, and the measured reference findings. Before using
> any DDL below, reconcile it with the
> [`Sessions vertical-slice contract`](../SESSION-VERTICAL-SLICE.md).

This document preserves an earlier schema synthesis. It is useful design material,
but its original title overstated its authority. In particular, its one-repository
session shape and deferred `logical_seq` conflict with the measured cross-repository
evidence and the event/trace read model. The vertical-slice contract resolves those
requirements: a receipt remains idempotent at `(machine_id, session_id, agent_id,
line_number)` because agent-supplied session IDs are not proven fleet-global;
normalized events have a stable in-line ordinal and source reference; repository
evidence is event-level with a many-repository session projection; repeated lifecycle
callbacks are first-write-wins facts, while subagent facts remain distinguished by
`agent_id`.

The DDL below must not be applied as-is until those corrections are represented in a
reviewed migration that is traced to the browser fields it serves. The target is a
networked, multi-node fleet store; no section of this document establishes that such a
Capacitor service is already deployed.

---

## 1. Table Inventory

| Table Name | Purpose | Primary Key |
| :--- | :--- | :--- |
| `session_events` | Append-only raw and normalized event log | `(session_id, agent_id, line_number)` |
| `session_watermarks` | Checkpoint frontier for client ingestion (`last-line`) | `(session_id, agent_id)` |
| `sessions` | Session metadata, lifecycle status, repo context, and summary rollups | `session_id` |
| `subagent_runs` | Subagent lifecycle and hierarchy tracking | `(parent_session_id, agent_id)` |
| `work_items` | Work items (issues, tickets, PRs) | `work_item_id` |
| `work_item_sessions` | Associations between sessions and work items | `(work_item_id, session_id)` |
| `work_item_breakdowns` | Parent -> Parts work item hierarchy | `(parent_id, part_id)` |
| `work_item_relations` | Work item dependencies (`blocks`, `blocked_by`) | `(from_id, to_id, relation_kind)` |
| `eval_runs` | LLM-as-a-judge evaluation run metadata | `eval_run_id` |
| `eval_verdicts` | Per-question evaluation scoring and findings | `(eval_run_id, question_id)` |
| `judge_facts` | Cross-session patterns and lessons learned | `fact_hash` |
| `machines` | Fleet node registry (`FLEET.md`) | `machine_id` |
| `daemons` | Hosted agent runner daemon instances | `daemon_id` |
| `memories` | Team memories and preferences (name reserved; DDL deferred) | `memory_id` |

---

## 2. Entity Definitions & DDL Specifications

### 2.1 `session_events`
The append-only log of every user turn, model thought, tool call, tool result, and cost emission.

```sql
CREATE TABLE session_events (
    session_id          VARCHAR(64) NOT NULL,
    agent_id            VARCHAR(64) NOT NULL DEFAULT '', -- Empty string for parent session; subagent UUID for child
    line_number         INTEGER NOT NULL,                -- 0-based index in the vendor transcript (WatchCommand / SessionImporter)
    event_type          VARCHAR(64) NOT NULL,            -- 'SessionStarted', 'ToolCall', 'ToolResult', 'AssistantThinking', 'UserMessage', 'CostRecorded'
    vendor              VARCHAR(32) NOT NULL,            -- 'claude', 'codex', 'cursor', 'gemini', 'antigravity', 'copilot', 'opencode', 'pi', 'kiro', 'kimi'
    model               VARCHAR(64),                     -- 'claude-3-5-sonnet', 'gpt-5', 'gemini-2.5-flash', etc.
    timestamp           TIMESTAMPTZ NOT NULL,
    
    -- Token Accounting
    input_tokens        BIGINT NOT NULL DEFAULT 0,
    output_tokens       BIGINT NOT NULL DEFAULT 0,
    cache_read_tokens   BIGINT NOT NULL DEFAULT 0,
    cache_write_tokens  BIGINT NOT NULL DEFAULT 0,
    cost_usd            NUMERIC(10, 6) NOT NULL DEFAULT 0,
    
    -- Tool Invocations
    tool_server         VARCHAR(64),                     -- e.g. 'claude-in-chrome', 'kcap-workitems'
    tool_name           VARCHAR(64),                     -- e.g. 'browser_batch', 'bash', 'view_file'
    tool_input          JSONB,                           -- JSON argument payload
    tool_output         JSONB,                           -- JSON result payload
    tool_exit_code      INTEGER,
    is_error            BOOLEAN NOT NULL DEFAULT FALSE,
    
    -- Content & Raw Trace
    content             TEXT,                            -- Extracted user/assistant text or thoughts
    raw_payload         JSONB,                           -- Verbatim line JSON from vendor transcript
    
    PRIMARY KEY (session_id, agent_id, line_number)
);

-- One stored row per transcript line at this PK. Nested Claude content blocks
-- stay inside raw_payload until a later wave adds a sub-line ordinal (logical_seq).

CREATE INDEX idx_session_events_lookup ON session_events(session_id, timestamp);
CREATE INDEX idx_session_events_vendor_model ON session_events(vendor, model);
```

### 2.2 `session_watermarks`
Maintains the ingested frontier per session and agent stream.

```sql
CREATE TABLE session_watermarks (
    session_id          VARCHAR(64) NOT NULL,
    agent_id            VARCHAR(64) NOT NULL DEFAULT '',
    last_line_number    INTEGER NOT NULL,                -- 0-based; clients resume at last_line_number + 1. Missing row ≠ 0.
    byte_offset         BIGINT NOT NULL DEFAULT 0,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (session_id, agent_id)
);
```

### 2.3 `sessions`
The header and metadata table for every recorded session.

```sql
CREATE TABLE sessions (
    session_id                  VARCHAR(64) PRIMARY KEY, -- Stored dashless
    title                       TEXT,
    slug                        VARCHAR(128),
    vendor                      VARCHAR(32) NOT NULL,
    model                       VARCHAR(64),
    status                      VARCHAR(32) NOT NULL DEFAULT 'active', -- 'active', 'completed', 'hidden', 'archived'
    visibility                  VARCHAR(32) NOT NULL DEFAULT 'project',-- 'private', 'project', 'org_public', 'public'
    hidden_reason               VARCHAR(64),             -- live v_an_sessions column
    disposition                 VARCHAR(32),             -- live v_an_sessions column
    owner_user_id               VARCHAR(64) NOT NULL,
    machine_id                  VARCHAR(64),             -- Host node attribution (FLEET.md)
    daemon_id                   VARCHAR(64),             -- Daemon attribution if hosted
    
    -- Git / Repo Context
    repo_hash                   VARCHAR(64),
    repo_owner                  VARCHAR(128),
    repo_name                   VARCHAR(128),
    branch                      VARCHAR(128),
    pr_number                   INTEGER,
    pr_title                    TEXT,
    pr_url                      TEXT,
    pr_head_ref                 VARCHAR(128),
    
    -- Timestamps & Metrics
    started_at                  TIMESTAMPTZ NOT NULL,
    ended_at                    TIMESTAMPTZ,
    last_event_at               TIMESTAMPTZ,
    duration_min                NUMERIC(8, 2) DEFAULT 0,
    event_count                 INTEGER NOT NULL DEFAULT 0,
    tool_count                  INTEGER NOT NULL DEFAULT 0,
    total_tokens                BIGINT NOT NULL DEFAULT 0,
    total_cost_usd              NUMERIC(10, 4) NOT NULL DEFAULT 0,
    
    -- Continuation Chain & Classification
    previous_session_id         VARCHAR(64) REFERENCES sessions(session_id),
    next_session_id             VARCHAR(64) REFERENCES sessions(session_id),
    primary_phase               VARCHAR(32),             -- 'spec', 'implementation', 'review', 'debug', 'chore', 'neutral'
    secondary_phase             VARCHAR(32),
    classification_confidence   NUMERIC(3, 2),
    classification_source       VARCHAR(32)              -- 'llm', 'rule', 'manual'
);

CREATE INDEX idx_sessions_repo ON sessions(repo_hash, started_at);
CREATE INDEX idx_sessions_owner ON sessions(owner_user_id, started_at);
CREATE INDEX idx_sessions_machine ON sessions(machine_id, started_at);
```

### 2.4 `subagent_runs`
Hierarchical subagent links (Antigravity subagents, Claude child tasks, Codex collab children).

```sql
CREATE TABLE subagent_runs (
    parent_session_id   VARCHAR(64) NOT NULL REFERENCES sessions(session_id),
    agent_id            VARCHAR(64) NOT NULL,
    agent_type          VARCHAR(64),                     -- 'research', 'build', 'general'
    role                VARCHAR(128),
    prompt              TEXT,
    spawned_at          TIMESTAMPTZ NOT NULL,
    stopped_at          TIMESTAMPTZ,
    duration_ms         BIGINT,
    exit_status         VARCHAR(32),                     -- 'completed', 'error', 'killed'
    PRIMARY KEY (parent_session_id, agent_id)
);
```

### 2.5 `work_items`, `work_item_sessions`, & Topology

```sql
CREATE TABLE work_items (
    work_item_id        VARCHAR(64) PRIMARY KEY,
    repo_hash           VARCHAR(64) NOT NULL,
    title               TEXT NOT NULL,
    issue_key           VARCHAR(64),                     -- 'AI-1234'
    pr_number           INTEGER,
    status              VARCHAR(32) NOT NULL DEFAULT 'open',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE work_item_sessions (
    work_item_id        VARCHAR(64) NOT NULL REFERENCES work_items(work_item_id),
    session_id          VARCHAR(64) NOT NULL REFERENCES sessions(session_id),
    correlation_source  VARCHAR(32) NOT NULL,            -- 'manual_mcp', 'branch_name', 'commit', 'llm'
    confidence          NUMERIC(3, 2) NOT NULL DEFAULT 1.0,
    attached_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (work_item_id, session_id)
);

CREATE TABLE work_item_breakdowns (
    parent_id           VARCHAR(64) NOT NULL REFERENCES work_items(work_item_id),
    part_id             VARCHAR(64) NOT NULL REFERENCES work_items(work_item_id),
    PRIMARY KEY (parent_id, part_id)
);

CREATE TABLE work_item_relations (
    from_id             VARCHAR(64) NOT NULL REFERENCES work_items(work_item_id),
    to_id               VARCHAR(64) NOT NULL REFERENCES work_items(work_item_id),
    relation_kind       VARCHAR(32) NOT NULL,            -- 'blocks', 'blocked_by'
    PRIMARY KEY (from_id, to_id, relation_kind)
);
```

### 2.6 `eval_runs`, `eval_verdicts`, & `judge_facts`

```sql
CREATE TABLE eval_runs (
    eval_run_id                 VARCHAR(64) PRIMARY KEY,
    session_id                  VARCHAR(64) NOT NULL REFERENCES sessions(session_id),
    judge_model                 VARCHAR(64) NOT NULL,
    overall_score               INTEGER NOT NULL,
    summary                     TEXT NOT NULL,
    retrospective_json          JSONB,
    retrospective_prompt_version VARCHAR(32),
    evaluated_at                TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE eval_verdicts (
    eval_run_id                 VARCHAR(64) NOT NULL REFERENCES eval_runs(eval_run_id),
    category                    VARCHAR(32) NOT NULL,    -- 'safety', 'quality', 'efficiency', 'plan_adherence'
    question_id                 VARCHAR(64) NOT NULL,    -- 'destructive_commands', 'tests_written', etc.
    score                       INTEGER NOT NULL,
    verdict                     VARCHAR(16) NOT NULL,    -- 'pass', 'warn', 'fail'
    finding                     TEXT NOT NULL,
    evidence                    TEXT,
    recommendation              TEXT,
    tools_used                  INTEGER,
    prompt_version              VARCHAR(32),
    PRIMARY KEY (eval_run_id, question_id)
);

CREATE TABLE judge_facts (
    fact_hash                   VARCHAR(64) PRIMARY KEY,
    repo_hash                   VARCHAR(64) NOT NULL,
    category                    VARCHAR(32) NOT NULL,
    fact                        TEXT NOT NULL,
    source_session_id           VARCHAR(64) NOT NULL,
    source_eval_run_id          VARCHAR(64) NOT NULL,
    applies_to_vendors          TEXT[],
    applies_to_session_kinds    TEXT[],
    retained_at                 TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

### 2.7 `machines` & `daemons` (Fleet Registry)

```sql
CREATE TABLE machines (
    machine_id                  VARCHAR(64) PRIMARY KEY,
    hostname                    VARCHAR(128) NOT NULL,
    os                          VARCHAR(32) NOT NULL,
    arch                        VARCHAR(32) NOT NULL,
    client_id                   VARCHAR(64) UNIQUE,
    registered_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_heartbeat              TIMESTAMPTZ NOT NULL
);

CREATE TABLE daemons (
    daemon_id                   VARCHAR(64) PRIMARY KEY,
    machine_id                  VARCHAR(64) NOT NULL REFERENCES machines(machine_id),
    daemon_name                 VARCHAR(64) NOT NULL,
    advertised_vendors          TEXT[] NOT NULL,
    max_agents                  INTEGER NOT NULL DEFAULT 4,
    status                      VARCHAR(32) NOT NULL DEFAULT 'connected',
    last_seen_at                TIMESTAMPTZ NOT NULL
);
```
