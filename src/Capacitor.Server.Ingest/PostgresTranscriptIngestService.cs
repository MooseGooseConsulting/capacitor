using System.Security.Cryptography;
using System.Text;
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
        IReadOnlyList<RejectedTranscriptSourceLine>? rejectedSourceLines = null,
        bool inferOmittedSourceLines = false,
        CancellationToken ct = default) {
        if (events.Count == 0
            && (acceptedSourceLines is null || acceptedSourceLines.Count == 0)
            && (rejectedSourceLines is null || rejectedSourceLines.Count == 0)) return 0;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var session in events.GroupBy(@event => @event.SessionId, StringComparer.Ordinal)) {
            var first = session.First();
            await EnsurePlaceholderAsync(connection, transaction, first.SessionId, first.Vendor, ownerUserId, ct);
        }

        var streams = events.Select(@event => (@event.SessionId, AgentId: @event.AgentId ?? string.Empty))
            .Concat((acceptedSourceLines ?? []).Select(line => (line.SessionId, line.AgentId)))
            .Concat((rejectedSourceLines ?? []).Select(line => (line.SessionId, line.AgentId)))
            .Distinct()
            .OrderBy(stream => stream.SessionId, StringComparer.Ordinal)
            .ThenBy(stream => stream.AgentId, StringComparer.Ordinal)
            .ToArray();

        // Serialize each stream inside this transaction. Token snapshots and watermarks both
        // depend on the preceding accepted line, so parallel batches must not independently
        // calculate a delta or an advancement from the same predecessor.
        foreach (var stream in streams) {
            await LockStreamAsync(connection, transaction, stream.SessionId, stream.AgentId, ct);
        }

        foreach (var rejected in rejectedSourceLines ?? []) {
            await RecordRejectedLineAsync(connection, transaction, rejected, ct);
        }
        foreach (var accepted in acceptedSourceLines ?? []) {
            await ClearRejectedLineAsync(connection, transaction, accepted, ct);
        }

        var inserted = 0;
        foreach (var @event in events
            .OrderBy(candidate => candidate.SessionId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.AgentId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.LineNumber)
            .ThenBy(candidate => candidate.LogicalSeq)) {
            var storedEvent = @event;
            var usageSnapshot = IsCodexUsageSnapshot(@event);
            UsageCheckpoint? checkpoint = null;
            if (usageSnapshot) {
                checkpoint = await GetUsageCheckpointAsync(connection, transaction, @event, ct);
                var reset = checkpoint is not null && HasUsageCounterReset(@event, checkpoint);
                storedEvent = @event with {
                    InputTokens = Delta(@event.InputTokens, checkpoint?.InputTokens ?? 0, checkpoint is not null, reset),
                    OutputTokens = Delta(@event.OutputTokens, checkpoint?.OutputTokens ?? 0, checkpoint is not null, reset),
                    CacheReadTokens = Delta(@event.CacheReadTokens, checkpoint?.CacheReadTokens ?? 0, checkpoint is not null, reset),
                    CacheWriteTokens = Delta(@event.CacheWriteTokens, checkpoint?.CacheWriteTokens ?? 0, checkpoint is not null, reset),
                    ReasoningTokens = @event.ReasoningTokens is { } reasoning
                        ? Delta(reasoning, checkpoint?.ReasoningTokens ?? 0, checkpoint is not null, reset)
                        : null,
                    CostUsd = Delta(@event.CostUsd, checkpoint?.CostUsd ?? 0m, checkpoint is not null, reset)
                };
            }

            var eventInserted = await InsertEventAsync(connection, transaction, storedEvent, ct);
            inserted += eventInserted;
            if (eventInserted > 0 && usageSnapshot) {
                await SaveUsageCheckpointAsync(connection, transaction, @event, ct);
            }
        }

        foreach (var stream in streams) {
            var acceptedLines = acceptedSourceLines?
                .Where(line => line.SessionId == stream.SessionId && line.AgentId == stream.AgentId)
                .Select(line => line.LineNumber);
            var watermark = await GetLastLineNumberAsync(connection, transaction, stream.SessionId, stream.AgentId, ct);
            var startLine = watermark is int lastWatermark
                ? Math.Max(firstLineNumber, lastWatermark + 1)
                : firstLineNumber;

            var lastLine = inferOmittedSourceLines
                ? await GetSparseContiguousLastLineAsync(
                    connection, transaction, stream.SessionId, stream.AgentId, acceptedLines, startLine, ct)
                : ContiguousLastLine(
                    await GetStoredLineNumbersAsync(connection, transaction, stream.SessionId, stream.AgentId, startLine, ct),
                    acceptedLines,
                    startLine);
            if (lastLine is int last) {
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

    private static async Task LockStreamAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionId,
        string agentId,
        CancellationToken ct) {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));", connection, transaction);
        command.Parameters.AddWithValue($"{sessionId}\u001f{agentId}");
        await command.ExecuteScalarAsync(ct);
    }

    private static async Task RecordRejectedLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RejectedTranscriptSourceLine rejected,
        CancellationToken ct) {
        var entryId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{rejected.SessionId}\u001f{rejected.AgentId}\u001f{rejected.LineNumber}"))).ToLowerInvariant();
        await using var command = new NpgsqlCommand(@"
            INSERT INTO dead_letter_entries (
                entry_id, session_id, agent_id, vendor, line_number, raw_line, error_reason, received_at
            ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (session_id, agent_id, line_number) DO UPDATE SET
                vendor = EXCLUDED.vendor,
                raw_line = EXCLUDED.raw_line,
                error_reason = EXCLUDED.error_reason,
                received_at = EXCLUDED.received_at;", connection, transaction);
        command.Parameters.AddWithValue(entryId);
        command.Parameters.AddWithValue(rejected.SessionId);
        command.Parameters.AddWithValue(rejected.AgentId);
        command.Parameters.AddWithValue(rejected.Vendor);
        command.Parameters.AddWithValue(rejected.LineNumber);
        command.Parameters.AddWithValue(rejected.RawLine);
        command.Parameters.AddWithValue(rejected.ErrorReason);
        command.Parameters.AddWithValue(EventTimestamp.ToUtcString(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ClearRejectedLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TranscriptSourceLine accepted,
        CancellationToken ct) {
        await using var command = new NpgsqlCommand(@"
            DELETE FROM dead_letter_entries
            WHERE session_id = $1 AND agent_id = $2 AND line_number = $3;", connection, transaction);
        command.Parameters.AddWithValue(accepted.SessionId);
        command.Parameters.AddWithValue(accepted.AgentId);
        command.Parameters.AddWithValue(accepted.LineNumber);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<int>> GetStoredLineNumbersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionId,
        string agentId,
        int startLine,
        CancellationToken ct) {
        await using var command = new NpgsqlCommand(@"
            SELECT DISTINCT line_number
            FROM session_events
            WHERE session_id = $1 AND agent_id = $2 AND line_number >= $3
            ORDER BY line_number;", connection, transaction);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(agentId);
        command.Parameters.AddWithValue(startLine);

        var lines = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) lines.Add(reader.GetInt32(0));
        return lines;
    }

    private static async Task<int?> GetSparseContiguousLastLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionId,
        string agentId,
        IEnumerable<int>? acceptedLines,
        int startLine,
        CancellationToken ct) {
        var lastAccepted = (acceptedLines ?? [])
            .Where(line => line >= startLine)
            .DefaultIfEmpty(startLine - 1)
            .Max();
        if (lastAccepted < startLine) return null;

        await using var command = new NpgsqlCommand(@"
            SELECT MIN(line_number)
            FROM dead_letter_entries
            WHERE session_id = $1 AND agent_id = $2 AND line_number >= $3;", connection, transaction);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(agentId);
        command.Parameters.AddWithValue(startLine);
        var result = await command.ExecuteScalarAsync(ct);
        var firstRejected = result is null or DBNull ? null : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
        var last = firstRejected is { } rejected && rejected <= lastAccepted ? rejected - 1 : lastAccepted;
        return last >= startLine ? last : null;
    }

    private static int? ContiguousLastLine(
        IEnumerable<int> storedLines,
        IEnumerable<int>? acceptedLines,
        int startLine) {
        var lines = new HashSet<int>(storedLines);
        if (acceptedLines is not null) {
            foreach (var line in acceptedLines) lines.Add(line);
        }

        int? last = null;
        for (var line = startLine; lines.Contains(line); line++) last = line;
        return last;
    }

    private static bool IsCodexUsageSnapshot(SessionEventRecord @event) =>
        string.Equals(@event.Vendor, "codex", StringComparison.OrdinalIgnoreCase)
        && string.Equals(@event.EventType, "UsageSnapshot", StringComparison.Ordinal);

    private static long Delta(long current, long previous, bool checkpointExists, bool reset = false) =>
        checkpointExists && !reset ? Math.Max(0, current - previous) : current;

    private static decimal Delta(decimal current, decimal previous, bool checkpointExists, bool reset = false) =>
        checkpointExists && !reset ? Math.Max(0m, current - previous) : current;

    private static bool HasUsageCounterReset(SessionEventRecord current, UsageCheckpoint previous) =>
        current.InputTokens < previous.InputTokens
        || current.OutputTokens < previous.OutputTokens
        || current.CacheReadTokens < previous.CacheReadTokens
        || current.CacheWriteTokens < previous.CacheWriteTokens;

    private static async Task<UsageCheckpoint?> GetUsageCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionEventRecord @event,
        CancellationToken ct) {
        await using var command = new NpgsqlCommand(@"
            SELECT input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, cost_usd
            FROM session_usage_checkpoints
            WHERE session_id = $1 AND agent_id = $2 AND vendor = $3
            FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue(@event.SessionId);
        command.Parameters.AddWithValue(@event.AgentId ?? string.Empty);
        command.Parameters.AddWithValue(@event.Vendor);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new UsageCheckpoint(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetDecimal(5))
            : null;
    }

    private static async Task SaveUsageCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionEventRecord @event,
        CancellationToken ct) {
        await using var command = new NpgsqlCommand(@"
            INSERT INTO session_usage_checkpoints (
                session_id, agent_id, vendor, input_tokens, output_tokens, cache_read_tokens,
                cache_write_tokens, reasoning_tokens, cost_usd
            ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            ON CONFLICT (session_id, agent_id, vendor) DO UPDATE SET
                input_tokens = EXCLUDED.input_tokens,
                output_tokens = EXCLUDED.output_tokens,
                cache_read_tokens = EXCLUDED.cache_read_tokens,
                cache_write_tokens = EXCLUDED.cache_write_tokens,
                reasoning_tokens = EXCLUDED.reasoning_tokens,
                cost_usd = EXCLUDED.cost_usd;", connection, transaction);
        command.Parameters.AddWithValue(@event.SessionId);
        command.Parameters.AddWithValue(@event.AgentId ?? string.Empty);
        command.Parameters.AddWithValue(@event.Vendor);
        command.Parameters.AddWithValue(@event.InputTokens);
        command.Parameters.AddWithValue(@event.OutputTokens);
        command.Parameters.AddWithValue(@event.CacheReadTokens);
        command.Parameters.AddWithValue(@event.CacheWriteTokens);
        command.Parameters.AddWithValue((object?)@event.ReasoningTokens ?? DBNull.Value);
        command.Parameters.AddWithValue(@event.CostUsd);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record UsageCheckpoint(
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        long? ReasoningTokens,
        decimal CostUsd);

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
