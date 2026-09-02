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

    // SQLite cannot alter a primary key in place. Older Capacitor databases used
    // (session_id, agent_id, line_number); rebuild only those databases so the
    // four-part conflict target used by both event stores is valid after upgrade.
    private static async Task UpgradeSessionEventPrimaryKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct) {
        var keyColumns = new List<(long Ordinal, string Name)>();
        using (var inspect = connection.CreateCommand()) {
            inspect.Transaction = transaction;
            inspect.CommandText = "PRAGMA table_info(session_events);";
            using var reader = await inspect.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                var ordinal = reader.GetInt64(5);
                if (ordinal > 0) keyColumns.Add((ordinal, reader.GetString(1)));
            }
        }

        var expected = new[] { "session_id", "agent_id", "line_number", "logical_seq" };
        if (keyColumns.OrderBy(column => column.Ordinal).Select(column => column.Name)
            .SequenceEqual(expected, StringComparer.OrdinalIgnoreCase)) {
            return;
        }

        using (var rebuild = connection.CreateCommand()) {
            rebuild.Transaction = transaction;
            rebuild.CommandText = @"
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
                    input_tokens        BIGINT NOT NULL DEFAULT 0,
                    output_tokens       BIGINT NOT NULL DEFAULT 0,
                    cache_read_tokens   BIGINT NOT NULL DEFAULT 0,
                    cache_write_tokens  BIGINT NOT NULL DEFAULT 0,
                    reasoning_tokens    BIGINT,
                    context_used_tokens BIGINT,
                    context_window_tokens BIGINT,
                    cost_usd            NUMERIC(10, 6) NOT NULL DEFAULT 0,
                    item_id             VARCHAR(64),
                    tool_server         VARCHAR(64),
                    tool_name           VARCHAR(64),
                    tool_input          TEXT,
                    tool_output         TEXT,
                    tool_exit_code      INTEGER,
                    is_error            BOOLEAN NOT NULL DEFAULT FALSE,
                    content             TEXT,
                    raw_payload         TEXT,
                    PRIMARY KEY (session_id, agent_id, line_number, logical_seq)
                );
                INSERT INTO session_events_upgrade (
                    session_id, agent_id, line_number, logical_seq, event_id, event_type, vendor, model,
                    timestamp, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
                    reasoning_tokens, context_used_tokens, context_window_tokens, cost_usd, item_id,
                    tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload
                )
                SELECT
                    session_id, agent_id, line_number, 0, event_id, event_type, vendor, model,
                    timestamp, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
                    reasoning_tokens, context_used_tokens, context_window_tokens, cost_usd, item_id,
                    tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload
                FROM session_events;
                DROP TABLE session_events;
                ALTER TABLE session_events_upgrade RENAME TO session_events;
                CREATE INDEX IF NOT EXISTS idx_session_events_lookup ON session_events(session_id, timestamp);
                CREATE INDEX IF NOT EXISTS idx_session_events_vendor_model ON session_events(vendor, model);";
            await rebuild.ExecuteNonQueryAsync(ct);
        }
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
