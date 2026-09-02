-- Upgrade databases created before logical_seq was part of event identity.
ALTER TABLE session_events
    ADD COLUMN IF NOT EXISTS logical_seq BIGINT NOT NULL DEFAULT 0;

DO $$
DECLARE
    primary_key_name TEXT;
    primary_key_columns TEXT[];
BEGIN
    SELECT c.conname,
           array_agg(a.attname ORDER BY key_column.ordinality)
      INTO primary_key_name, primary_key_columns
      FROM pg_constraint c
      JOIN unnest(c.conkey) WITH ORDINALITY AS key_column(attnum, ordinality) ON TRUE
      JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = key_column.attnum
     WHERE c.conrelid = 'session_events'::regclass
       AND c.contype = 'p'
     GROUP BY c.conname;

    IF primary_key_columns IS DISTINCT FROM ARRAY['session_id', 'agent_id', 'line_number', 'logical_seq'] THEN
        IF primary_key_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE session_events DROP CONSTRAINT %I', primary_key_name);
        END IF;

        ALTER TABLE session_events
            ADD CONSTRAINT session_events_pkey
            PRIMARY KEY (session_id, agent_id, line_number, logical_seq);
    END IF;
END $$;
