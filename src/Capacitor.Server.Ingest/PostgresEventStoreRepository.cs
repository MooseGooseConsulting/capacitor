using System.Globalization;
using Npgsql;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Ingest;

public class PostgresEventStoreRepository : IEventStoreRepository {
    private readonly NpgsqlDataSource _dataSource;

    public PostgresEventStoreRepository(NpgsqlDataSource dataSource) {
        _dataSource = dataSource;
    }

    public async Task<int> AppendEventsAsync(IReadOnlyList<SessionEventRecord> events, CancellationToken ct = default) {
        if (events.Count == 0) return 0;

        var inserted = 0;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string sql = @"
            INSERT INTO session_events (
                session_id, agent_id, line_number, logical_seq, event_id, event_type, vendor, model,
                timestamp, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_usd,
                tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload
            ) VALUES (
                $1, $2, $3, $4, $5, $6, $7, $8,
                $9, $10, $11, $12, $13, $14,
                $15, $16, $17, $18, $19, $20, $21, $22
            )
            ON CONFLICT(session_id, agent_id, line_number) DO UPDATE SET
                tool_output = COALESCE(EXCLUDED.tool_output, session_events.tool_output),
                tool_exit_code = COALESCE(EXCLUDED.tool_exit_code, session_events.tool_exit_code),
                is_error = EXCLUDED.is_error OR session_events.is_error,
                raw_payload = COALESCE(EXCLUDED.raw_payload, session_events.raw_payload);
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
            cmd.Parameters.AddWithValue(ev.CostUsd);
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

    public async Task<int> AppendEventsAndAdvanceWatermarkAsync(
            IReadOnlyList<SessionEventRecord> events,
            string sessionId,
            string agentId,
            int lastLineNumber,
            CancellationToken ct = default
        ) {
        var inserted = 0;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (events.Count > 0) {
            const string sql = @"
                INSERT INTO session_events (
                    session_id, agent_id, line_number, logical_seq, event_id, event_type, vendor, model,
                    timestamp, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_usd,
                    tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload
                ) VALUES (
                    $1, $2, $3, $4, $5, $6, $7, $8,
                    $9, $10, $11, $12, $13, $14,
                    $15, $16, $17, $18, $19, $20, $21, $22
                )
                ON CONFLICT(session_id, agent_id, line_number) DO UPDATE SET
                    tool_output = COALESCE(EXCLUDED.tool_output, session_events.tool_output),
                    tool_exit_code = COALESCE(EXCLUDED.tool_exit_code, session_events.tool_exit_code),
                    is_error = EXCLUDED.is_error OR session_events.is_error,
                    raw_payload = COALESCE(EXCLUDED.raw_payload, session_events.raw_payload);
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
                cmd.Parameters.AddWithValue(ev.CostUsd);
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
        }

        await using (var watermark = new NpgsqlCommand(@"
            INSERT INTO session_watermarks (session_id, agent_id, last_line_number, byte_offset, updated_at)
            VALUES ($1, $2, $3, 0, $4)
            ON CONFLICT(session_id, agent_id) DO UPDATE SET
                last_line_number = GREATEST(session_watermarks.last_line_number, EXCLUDED.last_line_number),
                updated_at = EXCLUDED.updated_at;
        ", conn, tx)) {
            watermark.Parameters.AddWithValue(sessionId);
            watermark.Parameters.AddWithValue(agentId ?? string.Empty);
            watermark.Parameters.AddWithValue(lastLineNumber);
            watermark.Parameters.AddWithValue(EventTimestamp.ToUtcString(DateTimeOffset.UtcNow));
            await watermark.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return inserted;
    }

    public async Task<IReadOnlyList<SessionEventRecord>> GetEventsAsync(string sessionId, string? agentId = null, int fromLine = 0, CancellationToken ct = default) {
        var list = new List<SessionEventRecord>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        if (agentId == null) {
            cmd.CommandText = @"
                SELECT session_id, agent_id, line_number, logical_seq, event_id, event_type, vendor, model,
                       timestamp, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_usd,
                       tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload
                FROM session_events
                WHERE session_id = $1 AND line_number >= $2
                ORDER BY line_number ASC, agent_id ASC;";
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(fromLine);
        } else {
            cmd.CommandText = @"
                SELECT session_id, agent_id, line_number, logical_seq, event_id, event_type, vendor, model,
                       timestamp, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_usd,
                       tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload
                FROM session_events
                WHERE session_id = $1 AND agent_id = $2 AND line_number >= $3
                ORDER BY line_number ASC;";
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(agentId);
            cmd.Parameters.AddWithValue(fromLine);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            list.Add(new SessionEventRecord {
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
                CostUsd = reader.GetDecimal(13),
                ToolServer = reader.IsDBNull(14) ? null : reader.GetString(14),
                ToolName = reader.IsDBNull(15) ? null : reader.GetString(15),
                ToolInput = reader.IsDBNull(16) ? null : reader.GetString(16),
                ToolOutput = reader.IsDBNull(17) ? null : reader.GetString(17),
                ToolExitCode = reader.IsDBNull(18) ? null : reader.GetInt32(18),
                IsError = reader.GetBoolean(19),
                Content = reader.IsDBNull(20) ? null : reader.GetString(20),
                RawPayload = reader.IsDBNull(21) ? null : reader.GetString(21)
            });
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
}
