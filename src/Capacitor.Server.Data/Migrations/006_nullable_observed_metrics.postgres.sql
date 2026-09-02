-- A numeric zero is meaningful only when the source explicitly reported it.
-- Older capture releases materialized absent values as zero, so move those
-- ambiguous historical zeros to NULL rather than presenting them as observed.

DO $$
DECLARE legacy_event_metrics BOOLEAN;
BEGIN
    SELECT is_nullable = 'NO'
    INTO legacy_event_metrics
    FROM information_schema.columns
    WHERE table_schema = current_schema()
      AND table_name = 'session_events'
      AND column_name = 'input_tokens';

    ALTER TABLE session_events
        ALTER COLUMN input_tokens DROP NOT NULL,
        ALTER COLUMN input_tokens DROP DEFAULT,
        ALTER COLUMN output_tokens DROP NOT NULL,
        ALTER COLUMN output_tokens DROP DEFAULT,
        ALTER COLUMN cache_read_tokens DROP NOT NULL,
        ALTER COLUMN cache_read_tokens DROP DEFAULT,
        ALTER COLUMN cache_write_tokens DROP NOT NULL,
        ALTER COLUMN cache_write_tokens DROP DEFAULT,
        ALTER COLUMN cost_usd DROP NOT NULL,
        ALTER COLUMN cost_usd DROP DEFAULT;

    -- Historic zero-valued measurements have no observability bit. Treat them
    -- conservatively as unknown; new normalizers retain explicit zero.
    IF legacy_event_metrics THEN
        UPDATE session_events
        SET input_tokens = NULLIF(input_tokens, 0),
            output_tokens = NULLIF(output_tokens, 0),
            cache_read_tokens = NULLIF(cache_read_tokens, 0),
            cache_write_tokens = NULLIF(cache_write_tokens, 0),
            cost_usd = NULLIF(cost_usd, 0);
    END IF;
END $$;

DO $$
DECLARE legacy_session_metrics BOOLEAN;
BEGIN
    SELECT is_nullable = 'NO'
    INTO legacy_session_metrics
    FROM information_schema.columns
    WHERE table_schema = current_schema()
      AND table_name = 'sessions'
      AND column_name = 'total_tokens';

    ALTER TABLE sessions
        ALTER COLUMN tool_count DROP NOT NULL,
        ALTER COLUMN tool_count DROP DEFAULT,
        ALTER COLUMN total_tokens DROP NOT NULL,
        ALTER COLUMN total_tokens DROP DEFAULT,
        ALTER COLUMN total_cost_usd DROP NOT NULL,
        ALTER COLUMN total_cost_usd DROP DEFAULT;

    -- A count is observable once there are canonical events. Token and cost
    -- header zeros from earlier releases remain ambiguous and become unknown.
    IF legacy_session_metrics THEN
        UPDATE sessions
        SET tool_count = CASE WHEN event_count = 0 THEN NULL ELSE tool_count END,
            total_tokens = NULLIF(total_tokens, 0),
            total_cost_usd = NULLIF(total_cost_usd, 0);
    END IF;
END $$;

DO $$
DECLARE legacy_checkpoint_metrics BOOLEAN;
BEGIN
    SELECT is_nullable = 'NO'
    INTO legacy_checkpoint_metrics
    FROM information_schema.columns
    WHERE table_schema = current_schema()
      AND table_name = 'session_usage_checkpoints'
      AND column_name = 'input_tokens';

    ALTER TABLE session_usage_checkpoints
        ALTER COLUMN input_tokens DROP NOT NULL,
        ALTER COLUMN input_tokens DROP DEFAULT,
        ALTER COLUMN output_tokens DROP NOT NULL,
        ALTER COLUMN output_tokens DROP DEFAULT,
        ALTER COLUMN cache_read_tokens DROP NOT NULL,
        ALTER COLUMN cache_read_tokens DROP DEFAULT,
        ALTER COLUMN cache_write_tokens DROP NOT NULL,
        ALTER COLUMN cache_write_tokens DROP DEFAULT,
        ALTER COLUMN cost_usd DROP NOT NULL,
        ALTER COLUMN cost_usd DROP DEFAULT;

    IF legacy_checkpoint_metrics THEN
        UPDATE session_usage_checkpoints
        SET input_tokens = NULLIF(input_tokens, 0),
            output_tokens = NULLIF(output_tokens, 0),
            cache_read_tokens = NULLIF(cache_read_tokens, 0),
            cache_write_tokens = NULLIF(cache_write_tokens, 0),
            cost_usd = NULLIF(cost_usd, 0);
    END IF;
END $$;
