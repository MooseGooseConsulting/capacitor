-- Source receipts are intentionally separate from normalized events. A receipt
-- is the client resume key; a normalizer may emit zero, one, or many events for it.
ALTER TABLE session_events
    ADD COLUMN IF NOT EXISTS cwd TEXT,
    ADD COLUMN IF NOT EXISTS repo_hash VARCHAR(64),
    ADD COLUMN IF NOT EXISTS repo_owner VARCHAR(128),
    ADD COLUMN IF NOT EXISTS repo_name VARCHAR(128);

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
    PRIMARY KEY (session_id, agent_id, line_number),
    CHECK (normalization_status IN ('accepted', 'rejected'))
);

-- Existing events predate receipts. Preserve their raw payloads as the best
-- available source evidence before all new traffic uses the receipt path.
INSERT INTO transcript_receipts (
    session_id, agent_id, line_number, vendor, raw_payload, normalization_status,
    cwd, repo_hash, repo_owner, repo_name, received_at, updated_at
)
SELECT DISTINCT ON (session_id, agent_id, line_number)
    session_id,
    agent_id,
    line_number,
    vendor,
    COALESCE(raw_payload, ''),
    'accepted',
    cwd,
    repo_hash,
    repo_owner,
    repo_name,
    timestamp,
    timestamp
FROM session_events
ORDER BY session_id, agent_id, line_number, logical_seq
ON CONFLICT (session_id, agent_id, line_number) DO NOTHING;

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

-- Retain lifecycle-supplied associations, but never select one as primary just
-- because it arrived first. Primary is derived from the normalized event count.
INSERT INTO session_repositories (
    session_id, repo_hash, repo_owner, repo_name, event_count, is_primary, created_at, updated_at
)
SELECT session_id, repo_hash, repo_owner, repo_name, 0, FALSE, started_at, started_at
FROM sessions
WHERE repo_hash IS NOT NULL
ON CONFLICT (session_id, repo_hash) DO NOTHING;

WITH event_evidence AS (
    SELECT
        session_id,
        repo_hash,
        MAX(repo_owner) AS repo_owner,
        MAX(repo_name) AS repo_name,
        MIN(line_number) AS first_seen_line,
        COUNT(*) AS event_count,
        MIN(timestamp) AS seen_at
    FROM session_events
    WHERE repo_hash IS NOT NULL
    GROUP BY session_id, repo_hash
)
INSERT INTO session_repositories (
    session_id, repo_hash, repo_owner, repo_name, first_seen_line, event_count, is_primary, created_at, updated_at
)
SELECT session_id, repo_hash, repo_owner, repo_name, first_seen_line, event_count, FALSE, seen_at, seen_at
FROM event_evidence
ON CONFLICT (session_id, repo_hash) DO UPDATE SET
    repo_owner = COALESCE(EXCLUDED.repo_owner, session_repositories.repo_owner),
    repo_name = COALESCE(EXCLUDED.repo_name, session_repositories.repo_name),
    first_seen_line = CASE
        WHEN session_repositories.first_seen_line IS NULL THEN EXCLUDED.first_seen_line
        WHEN EXCLUDED.first_seen_line < session_repositories.first_seen_line THEN EXCLUDED.first_seen_line
        ELSE session_repositories.first_seen_line
    END,
    event_count = EXCLUDED.event_count,
    updated_at = EXCLUDED.updated_at;

UPDATE session_repositories
SET is_primary = FALSE
WHERE is_primary;

WITH ranked AS (
    SELECT session_id, repo_hash,
           ROW_NUMBER() OVER (
               PARTITION BY session_id
               ORDER BY event_count DESC, first_seen_line ASC NULLS LAST, repo_hash ASC
           ) AS rank
    FROM session_repositories
    WHERE event_count > 0
)
UPDATE session_repositories target
SET is_primary = TRUE
FROM ranked
WHERE target.session_id = ranked.session_id
  AND target.repo_hash = ranked.repo_hash
  AND ranked.rank = 1;

UPDATE sessions target
SET repo_hash = source.repo_hash,
    repo_owner = source.repo_owner,
    repo_name = source.repo_name
FROM session_repositories source
WHERE target.session_id = source.session_id
  AND source.is_primary;

CREATE INDEX IF NOT EXISTS idx_session_events_repository
    ON session_events(session_id, repo_hash, line_number);
CREATE INDEX IF NOT EXISTS idx_transcript_receipts_stream
    ON transcript_receipts(session_id, agent_id, line_number);
CREATE INDEX IF NOT EXISTS idx_session_repositories_repo
    ON session_repositories(repo_hash, session_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_session_repositories_primary
    ON session_repositories(session_id)
    WHERE is_primary;
