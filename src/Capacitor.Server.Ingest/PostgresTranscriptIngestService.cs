using Npgsql;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Ingest;

/// <summary>
/// PostgreSQL ingestion boundary. A hook acknowledgement is not sent until its events and
/// affected watermarks have committed together, so a process crash cannot strand one without
/// the other.
/// </summary>
public sealed class PostgresTranscriptIngestService : ITranscriptIngest {
    private const string EventColumns = @"
        session_id, agent_id, line_number, logical_seq, event_id, event_type, vendor, model,
        timestamp, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
        reasoning_tokens, context_used_tokens, context_window_tokens, cost_usd, item_id,
        tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresTranscriptIngestService(NpgsqlDataSource dataSource) {
        _dataSource = dataSource;
    }

    public async Task<int> IngestAsync(
        IReadOnlyList<SessionEventRecord> events,
        string? ownerUserId = null,
        int firstLineNumber = 0,
        IReadOnlyList<TranscriptSourceLine>? acceptedSourceLines = null,
        IReadOnlyList<TranscriptSourceLine>? rejectedSourceLines = null,
        bool inferOmittedSourceLines = false,
        CancellationToken ct = default) {
        if (events.Count == 0 && (acceptedSourceLines is null || acceptedSourceLines.Count == 0)) return 0;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var session in events.GroupBy(@event => @event.SessionId, StringComparer.Ordinal)) {
            var first = session.First();
            await EnsurePlaceholderAsync(connection, transaction, first.SessionId, first.Vendor, ownerUserId, ct);
        }

        var inserted = 0;
        foreach (var @event in events) {
            inserted += await InsertEventAsync(connection, transaction, @event, ct);
        }

        var streams = events.Select(@event => (@event.SessionId, AgentId: @event.AgentId ?? string.Empty))
            .Concat((acceptedSourceLines ?? []).Select(line => (line.SessionId, line.AgentId)))
            .Concat((rejectedSourceLines ?? []).Select(line => (line.SessionId, line.AgentId)))
            .Distinct()
            .ToArray();
        foreach (var stream in streams) {
            var stored = await GetStreamEventsAsync(connection, transaction, stream.SessionId, stream.AgentId, ct);
            var acceptedLines = acceptedSourceLines?
                .Where(line => line.SessionId == stream.SessionId && line.AgentId == stream.AgentId)
                .Select(line => line.LineNumber);
            var rejectedLines = rejectedSourceLines?
                .Where(line => line.SessionId == stream.SessionId && line.AgentId == stream.AgentId)
                .Select(line => line.LineNumber);
            var watermark = await GetLastLineNumberAsync(connection, transaction, stream.SessionId, stream.AgentId, ct);
            var startLine = watermark is int lastWatermark
                ? Math.Max(firstLineNumber, lastWatermark + 1)
                : firstLineNumber;
            if (TranscriptIngestEngine.ContiguousLastLine(
                    stored, acceptedLines, startLine, rejectedLines, inferOmittedSourceLines) is int lastLine) {
                await AdvanceWatermarkAsync(connection, transaction, stream.SessionId, stream.AgentId, lastLine, ct);
            }
        }

        await transaction.CommitAsync(ct);
        return inserted;
    }

    private static async Task EnsurePlaceholderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionId,
        string vendor,
        string? ownerUserId,
        CancellationToken ct) {
        await using var command = new NpgsqlCommand(@"
            INSERT INTO sessions (session_id, vendor, owner_user_id, status, visibility, started_at)
            VALUES ($1, $2, $3, 'active', 'private', $4)
            ON CONFLICT (session_id) DO NOTHING;", connection, transaction);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(vendor);
        command.Parameters.AddWithValue(ownerUserId ?? "anonymous");
        command.Parameters.AddWithValue(EventTimestamp.ToUtcString(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionEventRecord @event,
        CancellationToken ct) {
        await using var command = new NpgsqlCommand($@"
            INSERT INTO session_events ({EventColumns})
            VALUES (
                $1, $2, $3, $4, $5, $6, $7, $8,
                $9, $10, $11, $12, $13, $14, $15, $16, $17, $18,
                $19, $20, $21, $22, $23, $24, $25, $26)
            ON CONFLICT (session_id, agent_id, line_number, logical_seq) DO NOTHING;", connection, transaction);
        command.Parameters.AddWithValue(@event.SessionId);
        command.Parameters.AddWithValue(@event.AgentId ?? string.Empty);
        command.Parameters.AddWithValue(@event.LineNumber);
        command.Parameters.AddWithValue(@event.LogicalSeq);
        command.Parameters.AddWithValue((object?)@event.EventId ?? DBNull.Value);
        command.Parameters.AddWithValue(@event.EventType);
        command.Parameters.AddWithValue(@event.Vendor);
        command.Parameters.AddWithValue((object?)@event.Model ?? DBNull.Value);
        command.Parameters.AddWithValue(EventTimestamp.ToUtcString(@event.Timestamp));
        command.Parameters.AddWithValue(@event.InputTokens);
        command.Parameters.AddWithValue(@event.OutputTokens);
        command.Parameters.AddWithValue(@event.CacheReadTokens);
        command.Parameters.AddWithValue(@event.CacheWriteTokens);
        command.Parameters.AddWithValue((object?)@event.ReasoningTokens ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)@event.ContextUsedTokens ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)@event.ContextWindowTokens ?? DBNull.Value);
        command.Parameters.AddWithValue(@event.CostUsd);
        command.Parameters.AddWithValue((object?)@event.ItemId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)@event.ToolServer ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)@event.ToolName ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)@event.ToolInput ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)@event.ToolOutput ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)@event.ToolExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue(@event.IsError);
        command.Parameters.AddWithValue((object?)@event.Content ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)@event.RawPayload ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<SessionEventRecord>> GetStreamEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionId,
        string agentId,
        CancellationToken ct) {
        await using var command = new NpgsqlCommand(@"
            SELECT session_id, agent_id, line_number, logical_seq, event_id, event_type, vendor, model,
                   timestamp, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
                   reasoning_tokens, context_used_tokens, context_window_tokens, cost_usd, item_id,
                   tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload
            FROM session_events
            WHERE session_id = $1 AND agent_id = $2
            ORDER BY line_number, logical_seq;", connection, transaction);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(agentId);

        var events = new List<SessionEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            events.Add(new SessionEventRecord {
                SessionId = reader.GetString(0),
                AgentId = reader.GetString(1),
                LineNumber = reader.GetInt32(2),
                LogicalSeq = reader.GetInt64(3),
                EventId = reader.IsDBNull(4) ? null : reader.GetString(4),
                EventType = reader.GetString(5),
                Vendor = reader.GetString(6),
                Model = reader.IsDBNull(7) ? null : reader.GetString(7),
                Timestamp = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
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
            });
        }

        return events;
    }

    private static async Task<int?> GetLastLineNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionId,
        string agentId,
        CancellationToken ct) {
        await using var command = new NpgsqlCommand(@"
            SELECT last_line_number
            FROM session_watermarks
            WHERE session_id = $1 AND agent_id = $2
            FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(agentId);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task AdvanceWatermarkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionId,
        string agentId,
        int lastLineNumber,
        CancellationToken ct) {
        await using var command = new NpgsqlCommand(@"
            INSERT INTO session_watermarks (session_id, agent_id, last_line_number, byte_offset, updated_at)
            VALUES ($1, $2, $3, 0, $4)
            ON CONFLICT (session_id, agent_id) DO UPDATE SET
                last_line_number = EXCLUDED.last_line_number,
                updated_at = EXCLUDED.updated_at
            WHERE EXCLUDED.last_line_number > session_watermarks.last_line_number;", connection, transaction);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(agentId);
        command.Parameters.AddWithValue(lastLineNumber);
        command.Parameters.AddWithValue(EventTimestamp.ToUtcString(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
    }
}
