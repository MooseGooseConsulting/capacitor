using System.Globalization;
using Microsoft.Data.Sqlite;
using Capacitor.Server.Data.Entities;
using System.Text.Json;

namespace Capacitor.Server.Data.Tests.Unit;

public class SchemaMigrationTests {
    [Test]
    public async Task InitializeAsync_creates_all_tables_and_views_in_memory() {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await SqliteDatabaseInitializer.InitializeAsync(connection);

        // Verify tables exist
        var tables = new List<string>();
        using (var cmd = connection.CreateCommand()) {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view');";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                tables.Add(reader.GetString(0));
            }
        }

        await Assert.That(tables).Contains("session_events");
        await Assert.That(tables).Contains("session_watermarks");
        await Assert.That(tables).Contains("sessions");
        await Assert.That(tables).Contains("subagent_runs");
        await Assert.That(tables).Contains("work_items");
        await Assert.That(tables).Contains("work_item_sessions");
        await Assert.That(tables).Contains("eval_runs");
        await Assert.That(tables).Contains("eval_verdicts");
        await Assert.That(tables).Contains("judge_facts");
        await Assert.That(tables).Contains("machines");
        await Assert.That(tables).Contains("daemons");
        await Assert.That(tables).Contains("v_an_sessions");
        await Assert.That(tables).Contains("v_an_token_usage_by_model");
        await Assert.That(tables).Contains("v_an_tool_usage");

        var toolUsageColumns = await ListColumnNamesAsync(connection, "v_an_tool_usage");
        await Assert.That(toolUsageColumns).Contains("session_id");
        await Assert.That(toolUsageColumns).Contains("errors");
    }

    [Test]
    public async Task InitializeAsync_creates_indexes_documented_in_the_schema_spec() {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await SqliteDatabaseInitializer.InitializeAsync(connection);

        var indexes = new List<string>();
        using (var cmd = connection.CreateCommand()) {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                indexes.Add(reader.GetString(0));
            }
        }

        await Assert.That(indexes).Contains("idx_session_events_vendor_model");
        await Assert.That(indexes).Contains("idx_sessions_owner");
    }

    [Test]
    public async Task InitializeAsync_creates_acp_envelope_columns_on_session_events() {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await SqliteDatabaseInitializer.InitializeAsync(connection);

        var columns = await ListColumnNamesAsync(connection, "session_events");
        await Assert.That(columns).Contains("item_id");
        await Assert.That(columns).Contains("reasoning_tokens");
        await Assert.That(columns).Contains("context_used_tokens");
        await Assert.That(columns).Contains("context_window_tokens");

        var pk = await ListPrimaryKeyColumnsAsync(connection, "session_events");
        await Assert.That(pk).IsEquivalentTo(["session_id", "agent_id", "line_number", "logical_seq"]);

        foreach (var column in new[] {
            "input_tokens", "output_tokens", "cache_read_tokens", "cache_write_tokens",
            "reasoning_tokens", "context_used_tokens", "context_window_tokens", "cost_usd"
        }) {
            await Assert.That(await ColumnIsNotNullAsync(connection, "session_events", column)).IsFalse();
        }

        var sessionColumns = await ListColumnNamesAsync(connection, "sessions");
        await Assert.That(sessionColumns).Contains("hidden_reason");
        await Assert.That(sessionColumns).Contains("disposition");
    }

    [Test]
    public async Task InitializeAsync_rebuilds_three_column_events_without_dropping_cwd() {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, ThreeColumnSessionEventsSql);
        await ExecuteNonQueryAsync(connection, """
            INSERT INTO session_events (
                session_id, agent_id, line_number, event_type, vendor, timestamp, cwd, repo_hash, repo_owner, repo_name
            ) VALUES (
                'sess-cwd', '', 1, 'Raw', 'claude', '2026-09-07T00:00:00Z',
                'C:\work\repo-a', 'hash-a', 'owner-a', 'repo-a'
            );
            """);

        await SqliteDatabaseInitializer.InitializeAsync(connection);

        var pk = await ListPrimaryKeyColumnsAsync(connection, "session_events");
        await Assert.That(pk).IsEquivalentTo(["session_id", "agent_id", "line_number", "logical_seq"]);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT cwd, repo_hash, repo_owner, repo_name, logical_seq
            FROM session_events WHERE session_id = 'sess-cwd';
            """;
        using var reader = await cmd.ExecuteReaderAsync();
        await Assert.That(await reader.ReadAsync()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo(@"C:\work\repo-a");
        await Assert.That(reader.GetString(1)).IsEqualTo("hash-a");
        await Assert.That(reader.GetString(2)).IsEqualTo("owner-a");
        await Assert.That(reader.GetString(3)).IsEqualTo("repo-a");
        await Assert.That(reader.GetInt64(4)).IsEqualTo(0);
    }

    [Test]
    public async Task InitializeAsync_makes_usage_nullable_when_four_column_key_is_already_current() {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, FourColumnNotNullUsageSql);
        await ExecuteNonQueryAsync(connection, """
            INSERT INTO session_events (
                session_id, agent_id, line_number, logical_seq, event_type, vendor, timestamp,
                input_tokens, cost_usd, cwd
            ) VALUES (
                'sess-notnull', '', 1, 7, 'Raw', 'claude', '2026-09-07T00:00:00Z',
                1, 0.5, 'C:\work\kept'
            );
            """);

        await SqliteDatabaseInitializer.InitializeAsync(connection);

        foreach (var column in new[] { "input_tokens", "cost_usd" }) {
            await Assert.That(await ColumnIsNotNullAsync(connection, "session_events", column)).IsFalse();
        }

        using (var read = connection.CreateCommand()) {
            read.CommandText = """
                SELECT logical_seq, input_tokens, cost_usd, cwd
                FROM session_events WHERE session_id = 'sess-notnull';
                """;
            using var reader = await read.ExecuteReaderAsync();
            await Assert.That(await reader.ReadAsync()).IsTrue();
            await Assert.That(reader.GetInt64(0)).IsEqualTo(7);
            await Assert.That(reader.GetInt64(1)).IsEqualTo(1);
            await Assert.That(reader.GetDecimal(2)).IsEqualTo(0.5m);
            await Assert.That(reader.GetString(3)).IsEqualTo(@"C:\work\kept");
        }

        await ExecuteNonQueryAsync(connection, """
            INSERT INTO session_events (
                session_id, agent_id, line_number, logical_seq, event_type, vendor, timestamp,
                input_tokens, cost_usd
            ) VALUES (
                'sess-notnull', '', 2, 0, 'Raw', 'claude', '2026-09-07T00:00:01Z',
                NULL, NULL
            );
            """);
    }

    [Test]
    public async Task analytics_views_sql_is_postgresql_safe() {
        var viewsSql = await SqliteDatabaseInitializer.GetEmbeddedMigrationAsync("002_analytics_views.sql");
        await Assert.That(viewsSql.Contains("CREATE VIEW IF NOT EXISTS", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_rolls_back_when_views_apply_fails() {
        using var tmp = new TempDir();
        var dbPath = tmp.PathTo("schema.db");
        var schemaSql = await SqliteDatabaseInitializer.GetEmbeddedMigrationAsync("001_initial_schema.sql");

        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False")) {
            await connection.OpenAsync();
            await Assert.That(async () =>
                await SqliteDatabaseInitializer.InitializeAsync(
                    connection,
                    schemaSql,
                    "CREATE VIEW v_an_sessions AS SELECT FROM;")).Throws<SqliteException>();
        }

        using var after = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await after.OpenAsync();
        var names = await ListTableAndViewNamesAsync(after);
        await Assert.That(names).DoesNotContain("session_events");
        await Assert.That(names).DoesNotContain("sessions");
        await Assert.That(names).DoesNotContain("v_an_sessions");
    }

    [Test]
    public async Task SessionEventRecord_serializes_and_deserializes_cleanly() {
        var record = new SessionEventRecord {
            SessionId = "70dc37b2b3b14f139c153858abbe88a8",
            AgentId = "sub-1",
            LineNumber = 1,
            LogicalSeq = 100,
            EventType = "ToolCall",
            Vendor = "claude",
            Model = "claude-3-5-sonnet",
            Timestamp = DateTimeOffset.UtcNow,
            InputTokens = 1500,
            OutputTokens = 300,
            ReasoningTokens = 128,
            ContextUsedTokens = 142_000,
            ContextWindowTokens = 200_000,
            CostUsd = 0.0045m,
            ItemId = "item-42",
            ToolName = "bash",
            ToolInput = "{\"command\":\"ls -la\"}",
            IsError = false
        };

        var json = JsonSerializer.Serialize(record);
        var deserialized = JsonSerializer.Deserialize<SessionEventRecord>(json);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized!.SessionId).IsEqualTo(record.SessionId);
        await Assert.That(deserialized.AgentId).IsEqualTo(record.AgentId);
        await Assert.That(deserialized.LineNumber).IsEqualTo(record.LineNumber);
        await Assert.That(deserialized.InputTokens).IsEqualTo(1500);
        await Assert.That(deserialized.ReasoningTokens).IsEqualTo(128L);
        await Assert.That(deserialized.ContextUsedTokens).IsEqualTo(142_000L);
        await Assert.That(deserialized.ContextWindowTokens).IsEqualTo(200_000L);
        await Assert.That(deserialized.ItemId).IsEqualTo("item-42");
        await Assert.That(deserialized.ToolName).IsEqualTo("bash");
    }

    [Test]
    public async Task Session_metric_json_keeps_unknown_values_explicit() {
        var record = new SessionHeaderRecord {
            SessionId = "sess-unknown-metrics",
            Vendor = "codex",
            OwnerUserId = "dev-user",
            StartedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(record);

        await Assert.That(json).Contains("\"tool_count\":null");
        await Assert.That(json).Contains("\"total_tokens\":null");
        await Assert.That(json).Contains("\"total_cost_usd\":null");
    }

    static async Task<List<string>> ListTableAndViewNamesAsync(SqliteConnection connection) {
        var names = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view');";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    static async Task<List<string>> ListColumnNamesAsync(SqliteConnection connection, string table) {
        var columns = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    static async Task<List<string>> ListPrimaryKeyColumnsAsync(SqliteConnection connection, string table) {
        var columns = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}') WHERE pk > 0 ORDER BY pk;";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    static async Task<bool> ColumnIsNotNullAsync(SqliteConnection connection, string table, string column) {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT \"notnull\" FROM pragma_table_info('{table}') WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", column);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) != 0;
    }

    static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql) {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    const string ThreeColumnSessionEventsSql = """
        CREATE TABLE session_events (
            session_id          VARCHAR(64) NOT NULL,
            agent_id            VARCHAR(64) NOT NULL DEFAULT '',
            line_number         INTEGER NOT NULL,
            event_type          VARCHAR(64) NOT NULL,
            vendor              VARCHAR(32) NOT NULL,
            model               VARCHAR(64),
            timestamp           VARCHAR(35) NOT NULL,
            cwd                 TEXT,
            repo_hash           VARCHAR(64),
            repo_owner          VARCHAR(128),
            repo_name           VARCHAR(128),
            PRIMARY KEY (session_id, agent_id, line_number)
        );
        """;

    const string FourColumnNotNullUsageSql = """
        CREATE TABLE session_events (
            session_id          VARCHAR(64) NOT NULL,
            agent_id            VARCHAR(64) NOT NULL DEFAULT '',
            line_number         INTEGER NOT NULL,
            logical_seq         BIGINT NOT NULL DEFAULT 0,
            event_type          VARCHAR(64) NOT NULL,
            vendor              VARCHAR(32) NOT NULL,
            model               VARCHAR(64),
            timestamp           VARCHAR(35) NOT NULL,
            input_tokens        BIGINT NOT NULL DEFAULT 0,
            cost_usd            NUMERIC(10, 6) NOT NULL DEFAULT 0,
            cwd                 TEXT,
            repo_hash           VARCHAR(64),
            repo_owner          VARCHAR(128),
            repo_name           VARCHAR(128),
            PRIMARY KEY (session_id, agent_id, line_number, logical_seq)
        );
        """;
}
