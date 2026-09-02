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
        tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload,
        cwd, repo_hash, repo_owner, repo_name";

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

        var sourceLines = (acceptedSourceLines ?? [])
            .Select(line => (line.SessionId, line.Vendor))
            .Concat((rejectedSourceLines ?? []).Select(line => (line.SessionId, line.Vendor)));
        foreach (var session in events.Select(@event => (@event.SessionId, @event.Vendor))
            .Concat(sourceLines)
            .GroupBy(candidate => candidate.SessionId, StringComparer.Ordinal)) {
            await EnsurePlaceholderAsync(connection, transaction, session.Key, session.First().Vendor, ownerUserId, ct);
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
            await RecordReceiptAsync(connection, transaction, rejected, ct);
            await RecordRejectedLineAsync(connection, transaction, rejected, ct);
        }
        foreach (var accepted in acceptedSourceLines ?? []) {
            await RecordReceiptAsync(connection, transaction, accepted, ct);
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
                var staleSnapshot = checkpoint is not null && @event.LineNumber <= checkpoint.LastLineNumber;
                storedEvent = staleSnapshot
                    ? @event with {
                        InputTokens = 0,
                        OutputTokens = 0,
                        CacheReadTokens = 0,
                        CacheWriteTokens = 0,
                        ReasoningTokens = null,
                        CostUsd = 0m
                    }
                    : @event with {
                        InputTokens = Delta(@event.InputTokens, checkpoint?.InputTokens ?? 0, checkpoint is not null),
                        OutputTokens = Delta(@event.OutputTokens, checkpoint?.OutputTokens ?? 0, checkpoint is not null),
                        CacheReadTokens = Delta(@event.CacheReadTokens, checkpoint?.CacheReadTokens ?? 0, checkpoint is not null),
                        CacheWriteTokens = Delta(@event.CacheWriteTokens, checkpoint?.CacheWriteTokens ?? 0, checkpoint is not null),
                        ReasoningTokens = Delta(@event.ReasoningTokens, checkpoint?.ReasoningTokens, checkpoint is not null),
                        CostUsd = Delta(@event.CostUsd, checkpoint?.CostUsd ?? 0m, checkpoint is not null)
                    };
            }

            var eventInserted = await InsertEventAsync(connection, transaction, storedEvent, ct);
            inserted += eventInserted;
            if (eventInserted > 0 && usageSnapshot && (checkpoint is null || @event.LineNumber > checkpoint.LastLineNumber)) {
                await SaveUsageCheckpointAsync(connection, transaction, @event, ct);
            }
        }

        var repositoryEvidence = (acceptedSourceLines ?? [])
            .Select(line => (line.SessionId, line.RepositoryEvidence))
            .Concat((rejectedSourceLines ?? []).Select(line => (line.SessionId, line.RepositoryEvidence)))
            .Where(candidate => candidate.RepositoryEvidence?.HasRepository == true)
            .GroupBy(candidate => $"{candidate.SessionId}\u001f{candidate.RepositoryEvidence!.RepoHash}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        foreach (var (sessionId, evidence) in repositoryEvidence) {
            await RecordRepositoryAssociationAsync(connection, transaction, sessionId, evidence!, ct);
        }

        var repositorySessions = events
            .Where(@event => !string.IsNullOrWhiteSpace(@event.RepoHash))
            .Select(@event => @event.SessionId)
            .Concat(repositoryEvidence.Select(candidate => candidate.SessionId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var sessionId in repositorySessions) {
            await RefreshRepositoryProjectionAsync(connection, transaction, sessionId, ct);
        }

        foreach (var stream in streams) {
            var watermark = await GetLastLineNumberAsync(connection, transaction, stream.SessionId, stream.AgentId, ct);
            var startLine = watermark is int lastWatermark
                ? Math.Max(firstLineNumber, lastWatermark + 1)
                : firstLineNumber;

            var lastLine = inferOmittedSourceLines
                ? await GetSparseContiguousLastLineAsync(
                    connection, transaction, stream.SessionId, stream.AgentId, startLine, ct)
                : ContiguousLastLine(
                    await GetStoredLineNumbersAsync(connection, transaction, stream.SessionId, stream.AgentId, startLine, ct),
                    acceptedLines: null,
                    startLine: startLine);
            if (lastLine is int last) {
                await AdvanceWatermarkAsync(connection, transaction, stream.SessionId, stream.AgentId, last, ct);
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
                $19, $20, $21, $22, $23, $24, $25, $26, $27, $28, $29, $30)
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
        command.Parameters.AddWithValue((object?)@event.Cwd ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)@event.RepoHash ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)@event.RepoOwner ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)@event.RepoName ?? DBNull.Value);
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

    private static Task RecordReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TranscriptSourceLine accepted,
        CancellationToken ct) =>
        UpsertReceiptAsync(
            connection,
            transaction,
            accepted.SessionId,
            accepted.AgentId,
            accepted.LineNumber,
            accepted.Vendor,
            accepted.RawPayload,
            normalizationStatus: "accepted",
            failureReason: null,
            evidence: accepted.RepositoryEvidence,
            ct: ct);

    private static Task RecordReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RejectedTranscriptSourceLine rejected,
        CancellationToken ct) =>
        UpsertReceiptAsync(
            connection,
            transaction,
            rejected.SessionId,
            rejected.AgentId,
            rejected.LineNumber,
            rejected.Vendor,
            rejected.RawLine,
            normalizationStatus: "rejected",
            failureReason: rejected.ErrorReason,
            evidence: rejected.RepositoryEvidence,
            ct: ct);

    private static async Task UpsertReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionId,
        string agentId,
        int lineNumber,
        string vendor,
        string rawPayload,
        string normalizationStatus,
        string? failureReason,
        RepositoryEvidence? evidence,
        CancellationToken ct) {
        var now = EventTimestamp.ToUtcString(DateTimeOffset.UtcNow);
        await using var command = new NpgsqlCommand(@"
            INSERT INTO transcript_receipts (
                session_id, agent_id, line_number, vendor, raw_payload, normalization_status,
                failure_reason, cwd, repo_hash, repo_owner, repo_name, received_at, updated_at
            ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $12)
            ON CONFLICT (session_id, agent_id, line_number) DO UPDATE SET
                vendor = EXCLUDED.vendor,
                raw_payload = EXCLUDED.raw_payload,
                normalization_status = EXCLUDED.normalization_status,
                failure_reason = EXCLUDED.failure_reason,
                cwd = EXCLUDED.cwd,
                repo_hash = EXCLUDED.repo_hash,
                repo_owner = EXCLUDED.repo_owner,
                repo_name = EXCLUDED.repo_name,
                updated_at = EXCLUDED.updated_at;", connection, transaction);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(agentId);
        command.Parameters.AddWithValue(lineNumber);
        command.Parameters.AddWithValue(vendor);
        command.Parameters.AddWithValue(rawPayload);
        command.Parameters.AddWithValue(normalizationStatus);
        command.Parameters.AddWithValue((object?)failureReason ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)evidence?.Cwd ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)evidence?.RepoHash ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)evidence?.RepoOwner ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)evidence?.RepoName ?? DBNull.Value);
        command.Parameters.AddWithValue(now);
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
            SELECT line_number
            FROM transcript_receipts
            WHERE session_id = $1
              AND agent_id = $2
              AND normalization_status = 'accepted'
              AND line_number >= $3
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
        int startLine,
        CancellationToken ct) {
        await using var command = new NpgsqlCommand(@"
            SELECT
                MAX(line_number) FILTER (WHERE normalization_status = 'accepted'),
                MIN(line_number) FILTER (WHERE normalization_status = 'rejected')
            FROM transcript_receipts
            WHERE session_id = $1 AND agent_id = $2 AND line_number >= $3;", connection, transaction);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(agentId);
        command.Parameters.AddWithValue(startLine);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0)) return null;
        var lastAccepted = reader.GetInt32(0);
        int? firstRejected = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        var last = firstRejected is { } rejected && rejected <= lastAccepted ? rejected - 1 : lastAccepted;
        return last >= startLine ? last : null;
    }

    private static async Task RecordRepositoryAssociationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionId,
        RepositoryEvidence evidence,
        CancellationToken ct) {
        if (!evidence.HasRepository) return;

        var now = EventTimestamp.ToUtcString(DateTimeOffset.UtcNow);
        await using var command = new NpgsqlCommand(@"
            INSERT INTO session_repositories (
                session_id, repo_hash, repo_owner, repo_name, event_count, is_primary, created_at, updated_at
            ) VALUES ($1, $2, $3, $4, 0, FALSE, $5, $5)
            ON CONFLICT (session_id, repo_hash) DO UPDATE SET
                repo_owner = COALESCE(EXCLUDED.repo_owner, session_repositories.repo_owner),
                repo_name = COALESCE(EXCLUDED.repo_name, session_repositories.repo_name),
                updated_at = EXCLUDED.updated_at;", connection, transaction);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(evidence.RepoHash!);
        command.Parameters.AddWithValue((object?)evidence.RepoOwner ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)evidence.RepoName ?? DBNull.Value);
        command.Parameters.AddWithValue(now);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task RefreshRepositoryProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionId,
        CancellationToken ct) {
        var now = EventTimestamp.ToUtcString(DateTimeOffset.UtcNow);
        await using (var aggregate = new NpgsqlCommand(@"
            WITH evidence AS (
                SELECT
                    session_id,
                    repo_hash,
                    MAX(repo_owner) AS repo_owner,
                    MAX(repo_name) AS repo_name,
                    MIN(line_number) AS first_seen_line,
                    COUNT(*) AS event_count
                FROM session_events
                WHERE session_id = $1 AND repo_hash IS NOT NULL
                GROUP BY session_id, repo_hash
            )
            INSERT INTO session_repositories (
                session_id, repo_hash, repo_owner, repo_name, first_seen_line, event_count,
                is_primary, created_at, updated_at
            )
            SELECT session_id, repo_hash, repo_owner, repo_name, first_seen_line, event_count,
                   FALSE, $2, $2
            FROM evidence
            ON CONFLICT (session_id, repo_hash) DO UPDATE SET
                repo_owner = COALESCE(EXCLUDED.repo_owner, session_repositories.repo_owner),
                repo_name = COALESCE(EXCLUDED.repo_name, session_repositories.repo_name),
                first_seen_line = CASE
                    WHEN session_repositories.first_seen_line IS NULL THEN EXCLUDED.first_seen_line
                    WHEN EXCLUDED.first_seen_line < session_repositories.first_seen_line THEN EXCLUDED.first_seen_line
                    ELSE session_repositories.first_seen_line
                END,
                event_count = EXCLUDED.event_count,
                updated_at = EXCLUDED.updated_at;", connection, transaction)) {
            aggregate.Parameters.AddWithValue(sessionId);
            aggregate.Parameters.AddWithValue(now);
            await aggregate.ExecuteNonQueryAsync(ct);
        }

        await using (var clearPrimary = new NpgsqlCommand(
            "UPDATE session_repositories SET is_primary = FALSE WHERE session_id = $1 AND is_primary;",
            connection, transaction)) {
            clearPrimary.Parameters.AddWithValue(sessionId);
            await clearPrimary.ExecuteNonQueryAsync(ct);
        }

        await using (var selectPrimary = new NpgsqlCommand(@"
            WITH primary_repository AS (
                SELECT repo_hash
                FROM session_repositories
                WHERE session_id = $1 AND event_count > 0
                ORDER BY event_count DESC, first_seen_line ASC NULLS LAST, repo_hash ASC
                LIMIT 1
            )
            UPDATE session_repositories target
            SET is_primary = TRUE
            FROM primary_repository source
            WHERE target.session_id = $1 AND target.repo_hash = source.repo_hash;", connection, transaction)) {
            selectPrimary.Parameters.AddWithValue(sessionId);
            await selectPrimary.ExecuteNonQueryAsync(ct);
        }

        await using var updateSession = new NpgsqlCommand(@"
            UPDATE sessions target
            SET repo_hash = source.repo_hash,
                repo_owner = source.repo_owner,
                repo_name = source.repo_name
            FROM session_repositories source
            WHERE target.session_id = $1
              AND source.session_id = target.session_id
              AND source.is_primary;", connection, transaction);
        updateSession.Parameters.AddWithValue(sessionId);
        await updateSession.ExecuteNonQueryAsync(ct);
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

    private static long Delta(long current, long previous, bool checkpointExists) =>
        !checkpointExists || current < previous ? current : current - previous;

    private static long? Delta(long? current, long? previous, bool checkpointExists) =>
        current is null ? null
        : !checkpointExists || previous is null || current < previous ? current
        : current - previous;

    private static decimal Delta(decimal current, decimal previous, bool checkpointExists) =>
        !checkpointExists || current < previous ? current : current - previous;

    private static async Task<UsageCheckpoint?> GetUsageCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionEventRecord @event,
        CancellationToken ct) {
        await using var command = new NpgsqlCommand(@"
            SELECT input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, cost_usd, last_line_number
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
                reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetDecimal(5), reader.GetInt32(6))
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
                cache_write_tokens, reasoning_tokens, cost_usd, last_line_number
            ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            ON CONFLICT (session_id, agent_id, vendor) DO UPDATE SET
                input_tokens = EXCLUDED.input_tokens,
                output_tokens = EXCLUDED.output_tokens,
                cache_read_tokens = EXCLUDED.cache_read_tokens,
                cache_write_tokens = EXCLUDED.cache_write_tokens,
                reasoning_tokens = COALESCE(EXCLUDED.reasoning_tokens, session_usage_checkpoints.reasoning_tokens),
                cost_usd = EXCLUDED.cost_usd,
                last_line_number = EXCLUDED.last_line_number;", connection, transaction);
        command.Parameters.AddWithValue(@event.SessionId);
        command.Parameters.AddWithValue(@event.AgentId ?? string.Empty);
        command.Parameters.AddWithValue(@event.Vendor);
        command.Parameters.AddWithValue(@event.InputTokens);
        command.Parameters.AddWithValue(@event.OutputTokens);
        command.Parameters.AddWithValue(@event.CacheReadTokens);
        command.Parameters.AddWithValue(@event.CacheWriteTokens);
        command.Parameters.AddWithValue((object?)@event.ReasoningTokens ?? DBNull.Value);
        command.Parameters.AddWithValue(@event.CostUsd);
        command.Parameters.AddWithValue(@event.LineNumber);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record UsageCheckpoint(
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        long? ReasoningTokens,
        decimal CostUsd,
        int LastLineNumber);

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
