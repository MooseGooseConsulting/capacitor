using System.Globalization;
using Microsoft.Data.Sqlite;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Ingest;

public class SqliteEventStoreRepository : IEventStoreRepository {
    private const string EventColumns = @"
        session_id, agent_id, line_number, logical_seq, event_id, event_type, vendor, model,
        timestamp, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
        reasoning_tokens, context_used_tokens, context_window_tokens, cost_usd, item_id,
        tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload,
        cwd, repo_hash, repo_owner, repo_name";

    private readonly SqliteConnection _connection;

    public SqliteEventStoreRepository(SqliteConnection connection) {
        _connection = connection;
    }

    public async Task<int> AppendEventsAsync(IReadOnlyList<SessionEventRecord> events, CancellationToken ct = default) {
        if (events.Count == 0) return 0;

        var inserted = 0;
        using var tx = _connection.BeginTransaction();
        try {
            const string sql = $@"
                INSERT INTO session_events ({EventColumns})
                VALUES (
                    $session_id, $agent_id, $line_number, $logical_seq, $event_id, $event_type, $vendor, $model,
                    $timestamp, $input_tokens, $output_tokens, $cache_read_tokens, $cache_write_tokens,
                    $reasoning_tokens, $context_used_tokens, $context_window_tokens, $cost_usd, $item_id,
                    $tool_server, $tool_name, $tool_input, $tool_output, $tool_exit_code, $is_error, $content, $raw_payload,
                    $cwd, $repo_hash, $repo_owner, $repo_name
                )
                ON CONFLICT(session_id, agent_id, line_number, logical_seq) DO NOTHING;
            ";

            foreach (var ev in events) {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;

                cmd.Parameters.AddWithValue("$session_id", ev.SessionId);
                cmd.Parameters.AddWithValue("$agent_id", ev.AgentId ?? string.Empty);
                cmd.Parameters.AddWithValue("$line_number", ev.LineNumber);
                cmd.Parameters.AddWithValue("$logical_seq", ev.LogicalSeq);
                cmd.Parameters.AddWithValue("$event_id", (object?)ev.EventId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$event_type", ev.EventType);
                cmd.Parameters.AddWithValue("$vendor", ev.Vendor);
                cmd.Parameters.AddWithValue("$model", (object?)ev.Model ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$timestamp", SqliteUtc.Format(ev.Timestamp));
                cmd.Parameters.AddWithValue("$input_tokens", (object?)ev.InputTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$output_tokens", (object?)ev.OutputTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cache_read_tokens", (object?)ev.CacheReadTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cache_write_tokens", (object?)ev.CacheWriteTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$reasoning_tokens", (object?)ev.ReasoningTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$context_used_tokens", (object?)ev.ContextUsedTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$context_window_tokens", (object?)ev.ContextWindowTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cost_usd", (object?)ev.CostUsd ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$item_id", (object?)ev.ItemId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tool_server", (object?)ev.ToolServer ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tool_name", (object?)ev.ToolName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tool_input", (object?)ev.ToolInput ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tool_output", (object?)ev.ToolOutput ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tool_exit_code", (object?)ev.ToolExitCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$is_error", ev.IsError ? 1 : 0);
                cmd.Parameters.AddWithValue("$content", (object?)ev.Content ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$raw_payload", (object?)ev.RawPayload ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cwd", (object?)ev.Cwd ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$repo_hash", (object?)ev.RepoHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$repo_owner", (object?)ev.RepoOwner ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$repo_name", (object?)ev.RepoName ?? DBNull.Value);

                inserted += await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return inserted;
        } catch {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<SessionEventRecord>> GetEventsAsync(string sessionId, string? agentId = null, int fromLine = 0, CancellationToken ct = default) {
        var list = new List<SessionEventRecord>();
        using var cmd = _connection.CreateCommand();

        if (agentId == null) {
            cmd.CommandText = $@"
                SELECT {EventColumns}
                FROM session_events
                WHERE session_id = $session_id AND line_number >= $from_line
                ORDER BY timestamp ASC, agent_id ASC, line_number ASC, logical_seq ASC;";
            cmd.Parameters.AddWithValue("$session_id", sessionId);
            cmd.Parameters.AddWithValue("$from_line", fromLine);
        } else {
            cmd.CommandText = $@"
                SELECT {EventColumns}
                FROM session_events
                WHERE session_id = $session_id AND agent_id = $agent_id AND line_number >= $from_line
                ORDER BY line_number ASC, logical_seq ASC;";
            cmd.Parameters.AddWithValue("$session_id", sessionId);
            cmd.Parameters.AddWithValue("$agent_id", agentId);
            cmd.Parameters.AddWithValue("$from_line", fromLine);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            list.Add(ReadEvent(reader));
        }

        return list;
    }

    public async Task<long> GetEventCountAsync(string sessionId, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM session_events WHERE session_id = $session_id;";
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res is long count ? count : Convert.ToInt64(res, CultureInfo.InvariantCulture);
    }

    public async Task<SessionRollupAggregate?> GetRollupAggregateAsync(string sessionId, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*) AS event_count,
                SUM(CASE WHEN tool_name IS NOT NULL THEN 1 ELSE 0 END) AS tool_count,
                CASE
                    WHEN SUM(CASE WHEN input_tokens IS NOT NULL
                                       OR output_tokens IS NOT NULL
                                       OR cache_read_tokens IS NOT NULL
                                       OR cache_write_tokens IS NOT NULL THEN 1 ELSE 0 END) = 0 THEN NULL
                    ELSE SUM(COALESCE(input_tokens, 0)
                           + COALESCE(output_tokens, 0)
                           + COALESCE(cache_read_tokens, 0)
                           + COALESCE(cache_write_tokens, 0))
                END AS total_tokens,
                SUM(cost_usd) AS total_cost_usd,
                MIN(timestamp) AS first_event,
                MAX(timestamp) AS last_event
            FROM session_events
            WHERE session_id = $session_id;";
        cmd.Parameters.AddWithValue("$session_id", sessionId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var eventCount = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
        if (eventCount == 0) return null;

        var toolCount = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        var totalTokens = reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
        var totalCost = reader.IsDBNull(3) ? null : Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
        var firstEventStr = reader.IsDBNull(4) ? null : reader.GetString(4);
        var lastEventStr = reader.IsDBNull(5) ? null : reader.GetString(5);

        decimal durationMin = 0m;
        DateTimeOffset? lastEventAt = null;
        if (firstEventStr != null && lastEventStr != null) {
            var first = DateTimeOffset.Parse(firstEventStr, CultureInfo.InvariantCulture);
            var last = DateTimeOffset.Parse(lastEventStr, CultureInfo.InvariantCulture);
            lastEventAt = last;
            durationMin = (decimal)Math.Round((last - first).TotalMinutes, 2);
        }

        return new SessionRollupAggregate(eventCount, toolCount, totalTokens, totalCost, durationMin, lastEventAt);
    }

    private static SessionEventRecord ReadEvent(SqliteDataReader reader) => new() {
        SessionId = reader.GetString(0),
        AgentId = reader.GetString(1),
        LineNumber = reader.GetInt32(2),
        LogicalSeq = reader.GetInt64(3),
        EventId = reader.IsDBNull(4) ? null : reader.GetString(4),
        EventType = reader.GetString(5),
        Vendor = reader.GetString(6),
        Model = reader.IsDBNull(7) ? null : reader.GetString(7),
        Timestamp = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        InputTokens = reader.IsDBNull(9) ? null : reader.GetInt64(9),
        OutputTokens = reader.IsDBNull(10) ? null : reader.GetInt64(10),
        CacheReadTokens = reader.IsDBNull(11) ? null : reader.GetInt64(11),
        CacheWriteTokens = reader.IsDBNull(12) ? null : reader.GetInt64(12),
        ReasoningTokens = reader.IsDBNull(13) ? null : reader.GetInt64(13),
        ContextUsedTokens = reader.IsDBNull(14) ? null : reader.GetInt64(14),
        ContextWindowTokens = reader.IsDBNull(15) ? null : reader.GetInt64(15),
        CostUsd = reader.IsDBNull(16) ? null : reader.GetDecimal(16),
        ItemId = reader.IsDBNull(17) ? null : reader.GetString(17),
        ToolServer = reader.IsDBNull(18) ? null : reader.GetString(18),
        ToolName = reader.IsDBNull(19) ? null : reader.GetString(19),
        ToolInput = reader.IsDBNull(20) ? null : reader.GetString(20),
        ToolOutput = reader.IsDBNull(21) ? null : reader.GetString(21),
        ToolExitCode = reader.IsDBNull(22) ? null : reader.GetInt32(22),
        IsError = reader.GetInt32(23) != 0,
        Content = reader.IsDBNull(24) ? null : reader.GetString(24),
        RawPayload = reader.IsDBNull(25) ? null : reader.GetString(25),
        Cwd = reader.IsDBNull(26) ? null : reader.GetString(26),
        RepoHash = reader.IsDBNull(27) ? null : reader.GetString(27),
        RepoOwner = reader.IsDBNull(28) ? null : reader.GetString(28),
        RepoName = reader.IsDBNull(29) ? null : reader.GetString(29)
    };
}
