-- Make normalization failures durable per transcript stream. A later sparse
-- batch may not advance a watermark over one until that exact source line is
-- accepted on a retry.
ALTER TABLE dead_letter_entries
    ADD COLUMN IF NOT EXISTS agent_id VARCHAR(64) NOT NULL DEFAULT '';

-- The baseline schema permitted several dead-letter rows for one source line. Retain the
-- newest record before enforcing the per-stream identity used for durable retry state.
DELETE FROM dead_letter_entries
WHERE ctid IN (
    SELECT ctid
    FROM (
        SELECT ctid,
               ROW_NUMBER() OVER (
                   PARTITION BY session_id, agent_id, line_number
                   ORDER BY received_at DESC, entry_id DESC
               ) AS duplicate_rank
        FROM dead_letter_entries
    ) duplicates
    WHERE duplicate_rank > 1
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_dead_letter_entries_stream_line
    ON dead_letter_entries(session_id, agent_id, line_number);

CREATE TABLE IF NOT EXISTS session_usage_checkpoints (
    session_id                  VARCHAR(64) NOT NULL,
    agent_id                    VARCHAR(64) NOT NULL DEFAULT '',
    vendor                      VARCHAR(32) NOT NULL,
    input_tokens                BIGINT NOT NULL DEFAULT 0,
    output_tokens               BIGINT NOT NULL DEFAULT 0,
    cache_read_tokens           BIGINT NOT NULL DEFAULT 0,
    cache_write_tokens          BIGINT NOT NULL DEFAULT 0,
    reasoning_tokens            BIGINT,
    cost_usd                    NUMERIC(10, 6) NOT NULL DEFAULT 0,
    last_line_number            INTEGER NOT NULL DEFAULT -1,
    PRIMARY KEY (session_id, agent_id, vendor)
);

ALTER TABLE session_usage_checkpoints
    ADD COLUMN IF NOT EXISTS last_line_number INTEGER NOT NULL DEFAULT -1;
