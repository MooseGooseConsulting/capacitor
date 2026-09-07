using Microsoft.Data.Sqlite;

namespace Capacitor.Server.Data;

public static class SqliteDatabaseInitializer {
    public static async Task InitializeAsync(SqliteConnection connection, CancellationToken ct = default) {
        if (connection.State != System.Data.ConnectionState.Open) {
            await connection.OpenAsync(ct);
        }

        var schemaSql = await GetEmbeddedMigrationAsync("001_initial_schema.sql", ct);
        var viewsSql = await GetEmbeddedMigrationAsync("002_analytics_views.sql", ct);
        await InitializeAsync(connection, schemaSql, viewsSql, ct);
    }

    internal static async Task InitializeAsync(
        SqliteConnection connection,
        string schemaSql,
        string viewsSql,
        CancellationToken ct = default) {
        using var transaction = connection.BeginTransaction();
        try {
            using (var cmd = connection.CreateCommand()) {
                cmd.Transaction = transaction;
                cmd.CommandText = schemaSql;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await UpgradeSessionEventPrimaryKeyAsync(connection, transaction, ct);
            await EnsureSourceEvidenceSchemaAsync(connection, transaction, ct);

            using (var cmd = connection.CreateCommand()) {
                cmd.Transaction = transaction;
                cmd.CommandText = viewsSql;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    // SQLite cannot ALTER a primary key or drop NOT NULL in place. Rebuild when
    // the key is still three-column, or when a preceding upgrade left usage
    // columns NOT NULL DEFAULT 0 while the key is already current.
    private static readonly string[] SessionEventPrimaryKey =
        ["session_id", "agent_id", "line_number", "logical_seq"];

    private static readonly string[] NullableUsageColumns = [
        "input_tokens", "output_tokens", "cache_read_tokens", "cache_write_tokens",
        "reasoning_tokens", "context_used_tokens", "context_window_tokens", "cost_usd"
    ];

    private static async Task UpgradeSessionEventPrimaryKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct) {
        var columns = new List<(string Name, bool NotNull, long PkOrdinal)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var inspect = connection.CreateCommand()) {
            inspect.Transaction = transaction;
            inspect.CommandText = "PRAGMA table_info(session_events);";
            using var reader = await inspect.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                var name = reader.GetString(1);
                var notNull = reader.GetInt64(3) != 0;
                var pkOrdinal = reader.GetInt64(5);
                columns.Add((name, notNull, pkOrdinal));
                names.Add(name);
            }
        }

        var keyMatches = columns.Where(column => column.PkOrdinal > 0)
            .OrderBy(column => column.PkOrdinal)
            .Select(column => column.Name)
            .SequenceEqual(SessionEventPrimaryKey, StringComparer.OrdinalIgnoreCase);
        var usageNotNull = NullableUsageColumns.Any(column =>
            columns.Any(info => info.NotNull && string.Equals(info.Name, column, StringComparison.OrdinalIgnoreCase)));
        if (keyMatches && !usageNotNull) {
            return;
        }

        string Copy(string column, string fallback) => names.Contains(column) ? column : fallback;
        string CopyUsage(string column) {
            if (!names.Contains(column)) {
                return "NULL";
            }

            // Match 006_nullable_observed_metrics.postgres.sql: DEFAULT 0 on a
            // NOT NULL usage column was the unreported sentinel, not an observed zero.
            return usageNotNull ? $"NULLIF({column}, 0)" : column;
        }

        using (var rebuild = connection.CreateCommand()) {
            rebuild.Transaction = transaction;
            rebuild.CommandText = $@"
                DROP VIEW IF EXISTS v_an_token_usage_by_model;
                DROP VIEW IF EXISTS v_an_tool_usage;
                DROP VIEW IF EXISTS v_an_cost;
                DROP VIEW IF EXISTS v_an_session_steps;
                CREATE TABLE session_events_upgrade (
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
                    PRIMARY KEY (session_id, agent_id, line_number, logical_seq)
                );
                INSERT INTO session_events_upgrade (
                    session_id, agent_id, line_number, logical_seq, event_id, event_type, vendor, model,
                    timestamp, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
                    reasoning_tokens, context_used_tokens, context_window_tokens, cost_usd, item_id,
                    tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload,
                    cwd, repo_hash, repo_owner, repo_name
                )
                SELECT
                    session_id, agent_id, line_number, {Copy("logical_seq", "0")},
                    {Copy("event_id", "NULL")}, event_type, vendor, {Copy("model", "NULL")},
                    timestamp, {CopyUsage("input_tokens")}, {CopyUsage("output_tokens")},
                    {CopyUsage("cache_read_tokens")}, {CopyUsage("cache_write_tokens")},
                    {Copy("reasoning_tokens", "NULL")}, {Copy("context_used_tokens", "NULL")},
                    {Copy("context_window_tokens", "NULL")}, {CopyUsage("cost_usd")},
                    {Copy("item_id", "NULL")}, {Copy("tool_server", "NULL")}, {Copy("tool_name", "NULL")},
                    {Copy("tool_input", "NULL")}, {Copy("tool_output", "NULL")}, {Copy("tool_exit_code", "NULL")},
                    {Copy("is_error", "FALSE")}, {Copy("content", "NULL")}, {Copy("raw_payload", "NULL")},
                    {Copy("cwd", "NULL")}, {Copy("repo_hash", "NULL")}, {Copy("repo_owner", "NULL")},
                    {Copy("repo_name", "NULL")}
                FROM session_events;
                DROP TABLE session_events;
                ALTER TABLE session_events_upgrade RENAME TO session_events;
                CREATE INDEX IF NOT EXISTS idx_session_events_lookup ON session_events(session_id, timestamp);
                CREATE INDEX IF NOT EXISTS idx_session_events_vendor_model ON session_events(vendor, model);";
            await rebuild.ExecuteNonQueryAsync(ct);
        }
    }

    // SQLite is retained only as a local compatibility harness, but its schema
    // must remain able to read the same event shape. PostgreSQL owns the atomic
    // receipt/projection implementation used by the API.
    private static async Task EnsureSourceEvidenceSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct) {
        await AddColumnIfMissingAsync(connection, transaction, "session_events", "cwd", "TEXT", ct);
        await AddColumnIfMissingAsync(connection, transaction, "session_events", "repo_hash", "VARCHAR(64)", ct);
        await AddColumnIfMissingAsync(connection, transaction, "session_events", "repo_owner", "VARCHAR(128)", ct);
        await AddColumnIfMissingAsync(connection, transaction, "session_events", "repo_name", "VARCHAR(128)", ct);

        using (var command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS transcript_receipts (
                    session_id VARCHAR(64) NOT NULL,
                    agent_id VARCHAR(64) NOT NULL DEFAULT '',
                    line_number INTEGER NOT NULL,
                    vendor VARCHAR(32) NOT NULL,
                    raw_payload TEXT NOT NULL,
                    normalization_status VARCHAR(16) NOT NULL,
                    failure_reason TEXT,
                    cwd TEXT,
                    repo_hash VARCHAR(64),
                    repo_owner VARCHAR(128),
                    repo_name VARCHAR(128),
                    received_at VARCHAR(35) NOT NULL,
                    updated_at VARCHAR(35) NOT NULL,
                    PRIMARY KEY (session_id, agent_id, line_number)
                );
                CREATE TABLE IF NOT EXISTS session_repositories (
                    session_id VARCHAR(64) NOT NULL,
                    repo_hash VARCHAR(64) NOT NULL,
                    repo_owner VARCHAR(128),
                    repo_name VARCHAR(128),
                    first_seen_line INTEGER,
                    event_count BIGINT NOT NULL DEFAULT 0,
                    is_primary BOOLEAN NOT NULL DEFAULT FALSE,
                    created_at VARCHAR(35) NOT NULL,
                    updated_at VARCHAR(35) NOT NULL,
                    PRIMARY KEY (session_id, repo_hash)
                );
                CREATE INDEX IF NOT EXISTS idx_session_events_repository ON session_events(session_id, repo_hash, line_number);
                CREATE INDEX IF NOT EXISTS idx_transcript_receipts_stream ON transcript_receipts(session_id, agent_id, line_number);
                CREATE INDEX IF NOT EXISTS idx_session_repositories_repo ON session_repositories(repo_hash, session_id);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_session_repositories_primary ON session_repositories(session_id) WHERE is_primary;

                INSERT OR IGNORE INTO transcript_receipts (
                    session_id, agent_id, line_number, vendor, raw_payload, normalization_status,
                    cwd, repo_hash, repo_owner, repo_name, received_at, updated_at
                )
                SELECT session_id, agent_id, line_number, vendor, COALESCE(raw_payload, ''), 'accepted',
                       cwd, repo_hash, repo_owner, repo_name, timestamp, timestamp
                FROM session_events
                GROUP BY session_id, agent_id, line_number;

                INSERT OR IGNORE INTO session_repositories (
                    session_id, repo_hash, repo_owner, repo_name, event_count, is_primary, created_at, updated_at
                )
                SELECT session_id, repo_hash, repo_owner, repo_name, 0, FALSE, started_at, started_at
                FROM sessions
                WHERE repo_hash IS NOT NULL;";
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string declaration,
        CancellationToken ct) {
        using (var inspect = connection.CreateCommand()) {
            inspect.Transaction = transaction;
            inspect.CommandText = $"PRAGMA table_info({table});";
            using var reader = await inspect.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        await alter.ExecuteNonQueryAsync(ct);
    }

    public static async Task<string> GetEmbeddedMigrationAsync(string filename, CancellationToken ct = default) {
        var baseDir = AppContext.BaseDirectory;
        var localPath = Path.Combine(baseDir, "Migrations", filename);
        if (File.Exists(localPath)) {
            return await File.ReadAllTextAsync(localPath, ct);
        }

        // Try direct source path fallback during tests
        var currentDir = Directory.GetCurrentDirectory();
        while (currentDir != null && !File.Exists(Path.Combine(currentDir, "Capacitor.slnx"))) {
            currentDir = Directory.GetParent(currentDir)?.FullName;
        }

        if (currentDir != null) {
            var fallback = Path.Combine(currentDir, "src", "Capacitor.Server.Data", "Migrations", filename);
            if (File.Exists(fallback)) {
                return await File.ReadAllTextAsync(fallback, ct);
            }
        }

        throw new FileNotFoundException($"Migration file '{filename}' not found.");
    }
}
