using System.Globalization;
using Npgsql;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Ingest;

public class PostgresEventStoreRepository : IEventStoreRepository {
    private const string EventColumns = @"
        session_id, agent_id, line_number, logical_seq, event_id, event_type, vendor, model,
        timestamp, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
        reasoning_tokens, context_used_tokens, context_window_tokens, cost_usd, item_id,
        tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresEventStoreRepository(NpgsqlDataSource dataSource) {
        _dataSource = dataSource;
    }

    public async Task<int> AppendEventsAsync(IReadOnlyList<SessionEventRecord> events, CancellationToken ct = default) {
        if (events.Count == 0) return 0;

        var inserted = 0;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string sql = $@"
            INSERT INTO session_events ({EventColumns})
            VALUES (
                $1, $2, $3, $4, $5, $6, $7, $8,
                $9, $10, $11, $12, $13, $14, $15, $16, $17, $18,
                $19, $20, $21, $22, $23, $24, $25, $26
            )
            ON CONFLICT(session_id, agent_id, line_number, logical_seq) DO NOTHING;
        ";

        foreach (var ev in events) {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue(ev.SessionId);
            cmd.Parameters.AddWithValue(ev.AgentId ?? string.Empty);
            cmd.Parameters.AddWithValue(ev.LineNumber);
            cmd.Parameters.AddWithValue(ev.LogicalSeq);
            cmd.Parameters.AddWithValue((object?)ev.EventId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(ev.EventType);
            cmd.Parameters.AddWithValue(ev.Vendor);
            cmd.Parameters.AddWithValue((object?)ev.Model ?? DBNull.Value);
            cmd.Parameters.AddWithValue(EventTimestamp.ToUtcString(ev.Timestamp));
            cmd.Parameters.AddWithValue(ev.InputTokens);
            cmd.Parameters.AddWithValue(ev.OutputTokens);
            cmd.Parameters.AddWithValue(ev.CacheReadTokens);
            cmd.Parameters.AddWithValue(ev.CacheWriteTokens);
            cmd.Parameters.AddWithValue((object?)ev.ReasoningTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ev.ContextUsedTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ev.ContextWindowTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue(ev.CostUsd);
            cmd.Parameters.AddWithValue((object?)ev.ItemId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ev.ToolServer ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ev.ToolName ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ev.ToolInput ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ev.ToolOutput ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ev.ToolExitCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue(ev.IsError);
            cmd.Parameters.AddWithValue((object?)ev.Content ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ev.RawPayload ?? DBNull.Value);

            inserted += await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return inserted;
    }

    public async Task<IReadOnlyList<SessionEventRecord>> GetEventsAsync(string sessionId, string? agentId = null, int fromLine = 0, CancellationToken ct = default) {
        var list = new List<SessionEventRecord>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        if (agentId == null) {
            cmd.CommandText = $@"
                SELECT {EventColumns}
                FROM session_events
                WHERE session_id = $1 AND line_number >= $2
                ORDER BY line_number ASC, logical_seq ASC, agent_id ASC;";
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(fromLine);
        } else {
            cmd.CommandText = $@"
                SELECT {EventColumns}
                FROM session_events
                WHERE session_id = $1 AND agent_id = $2 AND line_number >= $3
                ORDER BY line_number ASC, logical_seq ASC;";
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(agentId);
            cmd.Parameters.AddWithValue(fromLine);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            list.Add(ReadEvent(reader));
        }

        return list;
    }

    public async Task<long> GetEventCountAsync(string sessionId, CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM session_events WHERE session_id = $1;";
        cmd.Parameters.AddWithValue(sessionId);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res is long count ? count : Convert.ToInt64(res, CultureInfo.InvariantCulture);
    }

    public async Task<SessionRollupAggregate?> GetRollupAggregateAsync(string sessionId, CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*) AS event_count,
                SUM(CASE WHEN tool_name IS NOT NULL THEN 1 ELSE 0 END) AS tool_count,
                SUM(input_tokens + output_tokens + cache_read_tokens + cache_write_tokens) AS total_tokens,
                SUM(cost_usd) AS total_cost_usd,
                MIN(timestamp) AS first_event,
                MAX(timestamp) AS last_event
            FROM session_events
            WHERE session_id = $1;";
        cmd.Parameters.AddWithValue(sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var eventCount = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
        if (eventCount == 0) return null;

        var toolCount = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        var totalTokens = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
        var totalCost = reader.IsDBNull(3) ? 0m : Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
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

    private static SessionEventRecord ReadEvent(NpgsqlDataReader reader) => new() {
        SessionId = reader.GetString(0),
        AgentId = reader.GetString(1),
        LineNumber = reader.GetInt32(2),
        LogicalSeq = reader.GetInt64(3),
        EventId = reader.IsDBNull(4) ? null : reader.GetString(4),
        EventType = reader.GetString(5),
        Vendor = reader.GetString(6),
        Model = reader.IsDBNull(7) ? null : reader.GetString(7),
        Timestamp = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        InputTokens = reader.GetInt64(9),
        OutputTokens = reader.GetInt64(10),
        CacheReadTokens = reader.GetInt64(11),
        CacheWriteTokens = reader.GetInt64(12),
        ReasoningTokens = reader.IsDBNull(13) ? null : reader.GetInt64(13),
        ContextUsedTokens = reader.IsDBNull(14) ? null : reader.GetInt64(14),
        ContextWindowTokens = reader.IsDBNull(15) ? null : reader.GetInt64(15),
        CostUsd = reader.GetDecimal(16),
        ItemId = reader.IsDBNull(17) ? null : reader.GetString(17),
        ToolServer = reader.IsDBNull(18) ? null : reader.GetString(18),
        ToolName = reader.IsDBNull(19) ? null : reader.GetString(19),
        ToolInput = reader.IsDBNull(20) ? null : reader.GetString(20),
        ToolOutput = reader.IsDBNull(21) ? null : reader.GetString(21),
        ToolExitCode = reader.IsDBNull(22) ? null : reader.GetInt32(22),
        IsError = reader.GetBoolean(23),
        Content = reader.IsDBNull(24) ? null : reader.GetString(24),
        RawPayload = reader.IsDBNull(25) ? null : reader.GetString(25)
    };
}
