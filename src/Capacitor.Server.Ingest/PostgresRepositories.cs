using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Ingest;

public class PostgresWatermarkRepository : ISessionWatermarkRepository {
    private readonly NpgsqlDataSource _dataSource;

    public PostgresWatermarkRepository(NpgsqlDataSource dataSource) {
        _dataSource = dataSource;
    }

    public async Task<int?> GetLastLineNumberAsync(string sessionId, string agentId = "", CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_line_number FROM session_watermarks WHERE session_id = $1 AND agent_id = $2;";
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(agentId ?? string.Empty);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result == null || result is DBNull) return null;
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task UpdateWatermarkAsync(string sessionId, string agentId, int lastLineNumber, long byteOffset = 0, CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO session_watermarks (session_id, agent_id, last_line_number, byte_offset, updated_at)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT(session_id, agent_id) DO UPDATE SET
                last_line_number = EXCLUDED.last_line_number,
                byte_offset = EXCLUDED.byte_offset,
                updated_at = EXCLUDED.updated_at
            WHERE EXCLUDED.last_line_number > session_watermarks.last_line_number
               OR (EXCLUDED.last_line_number = session_watermarks.last_line_number
                   AND EXCLUDED.byte_offset > session_watermarks.byte_offset);
        ";

        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(agentId ?? string.Empty);
        cmd.Parameters.AddWithValue(lastLineNumber);
        cmd.Parameters.AddWithValue(byteOffset);
        cmd.Parameters.AddWithValue(EventTimestamp.ToUtcString(DateTimeOffset.UtcNow));

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public class PostgresSessionRepository : ISessionRepository {
    private const string SessionColumns = @"
        session_id, title, slug, vendor, model, status, visibility, hidden_reason, disposition, owner_user_id, machine_id, daemon_id,
        repo_hash, repo_owner, repo_name, branch, pr_number, pr_title, pr_url, pr_head_ref,
        started_at, ended_at, last_event_at, duration_min, event_count, tool_count, total_tokens, total_cost_usd,
        previous_session_id, next_session_id, primary_phase, secondary_phase, classification_confidence, classification_source";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresSessionRepository(NpgsqlDataSource dataSource) {
        _dataSource = dataSource;
    }

    public Task<SessionHeaderRecord> GetOrCreatePlaceholderAsync(string sessionId, string vendor, string? ownerUserId = null, CancellationToken ct = default) =>
        GetOrCreatePlaceholderAsync(sessionId, vendor, ownerUserId, defaultVisibility: null, ct);

    public async Task<SessionHeaderRecord> GetOrCreatePlaceholderAsync(string sessionId, string vendor, string? ownerUserId, string? defaultVisibility, CancellationToken ct = default) {
        var now = DateTimeOffset.UtcNow;
        var visibility = string.IsNullOrEmpty(defaultVisibility) ? "private" : defaultVisibility;
        await using (var conn = await _dataSource.OpenConnectionAsync(ct)) {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO sessions (
                    session_id, vendor, owner_user_id, status, visibility, started_at
                ) VALUES (
                    $1, $2, $3, $4, $5, $6
                )
                ON CONFLICT(session_id) DO NOTHING;
            ";
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(vendor);
            cmd.Parameters.AddWithValue(ownerUserId ?? "anonymous");
            cmd.Parameters.AddWithValue("active");
            cmd.Parameters.AddWithValue(visibility);
            cmd.Parameters.AddWithValue(EventTimestamp.ToUtcString(now));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var persisted = await GetSessionAsync(sessionId, ct);
        if (persisted is null) {
            throw new InvalidOperationException($"Placeholder insert for session '{sessionId}' did not produce a row.");
        }

        return persisted;
    }

    public async Task<SessionHeaderRecord?> GetSessionAsync(string sessionId, CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT session_id, title, slug, vendor, model, status, visibility, hidden_reason, disposition, owner_user_id, machine_id, daemon_id,
                   repo_hash, repo_owner, repo_name, branch, pr_number, pr_title, pr_url, pr_head_ref,
                   started_at, ended_at, last_event_at, duration_min, event_count, tool_count, total_tokens, total_cost_usd,
                   previous_session_id, next_session_id, primary_phase, secondary_phase, classification_confidence, classification_source
            FROM sessions
            WHERE session_id = $1;";
        cmd.Parameters.AddWithValue(sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new SessionHeaderRecord {
            SessionId = reader.GetString(0),
            Title = reader.IsDBNull(1) ? null : reader.GetString(1),
            Slug = reader.IsDBNull(2) ? null : reader.GetString(2),
            Vendor = reader.GetString(3),
            Model = reader.IsDBNull(4) ? null : reader.GetString(4),
            Status = reader.GetString(5),
            Visibility = reader.GetString(6),
            HiddenReason = reader.IsDBNull(7) ? null : reader.GetString(7),
            Disposition = reader.IsDBNull(8) ? null : reader.GetString(8),
            OwnerUserId = reader.GetString(9),
            MachineId = reader.IsDBNull(10) ? null : reader.GetString(10),
            DaemonId = reader.IsDBNull(11) ? null : reader.GetString(11),
            RepoHash = reader.IsDBNull(12) ? null : reader.GetString(12),
            RepoOwner = reader.IsDBNull(13) ? null : reader.GetString(13),
            RepoName = reader.IsDBNull(14) ? null : reader.GetString(14),
            Branch = reader.IsDBNull(15) ? null : reader.GetString(15),
            PrNumber = reader.IsDBNull(16) ? null : reader.GetInt32(16),
            PrTitle = reader.IsDBNull(17) ? null : reader.GetString(17),
            PrUrl = reader.IsDBNull(18) ? null : reader.GetString(18),
            PrHeadRef = reader.IsDBNull(19) ? null : reader.GetString(19),
            StartedAt = DateTimeOffset.Parse(reader.GetString(20), CultureInfo.InvariantCulture),
            EndedAt = reader.IsDBNull(21) ? null : DateTimeOffset.Parse(reader.GetString(21), CultureInfo.InvariantCulture),
            LastEventAt = reader.IsDBNull(22) ? null : DateTimeOffset.Parse(reader.GetString(22), CultureInfo.InvariantCulture),
            DurationMin = reader.IsDBNull(23) ? 0 : reader.GetDecimal(23),
            EventCount = reader.IsDBNull(24) ? 0 : reader.GetInt32(24),
            ToolCount = reader.IsDBNull(25) ? 0 : reader.GetInt32(25),
            TotalTokens = reader.IsDBNull(26) ? 0 : reader.GetInt64(26),
            TotalCostUsd = reader.IsDBNull(27) ? 0 : reader.GetDecimal(27),
            PreviousSessionId = reader.IsDBNull(28) ? null : reader.GetString(28),
            NextSessionId = reader.IsDBNull(29) ? null : reader.GetString(29),
            PrimaryPhase = reader.IsDBNull(30) ? null : reader.GetString(30),
            SecondaryPhase = reader.IsDBNull(31) ? null : reader.GetString(31),
            ClassificationConfidence = reader.IsDBNull(32) ? null : reader.GetDecimal(32),
            ClassificationSource = reader.IsDBNull(33) ? null : reader.GetString(33)
        };
    }

    public async Task<SessionSearchPage> SearchSessionsAsync(SessionSearchQuery query, CancellationToken ct = default) {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var offset = Math.Max(query.Offset, 0);
        var text = string.IsNullOrWhiteSpace(query.Query) ? null : query.Query.Trim();
        var repo = string.IsNullOrWhiteSpace(query.Repo) ? null : query.Repo.Trim();
        var vendor = string.IsNullOrWhiteSpace(query.Vendor) ? null : query.Vendor.Trim();
        var status = string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        long total;
        await using (var count = conn.CreateCommand()) {
            count.CommandText = @"
            SELECT COUNT(*)
            FROM sessions
            WHERE ($1 IS NULL OR vendor = $1)
              AND ($2 IS NULL OR status = $2)
              AND ($3 IS NULL OR repo_hash = $3 OR (repo_owner || '/' || repo_name) = $3)
              AND ($4 IS NULL
                   OR title ILIKE '%' || $4 || '%'
                   OR EXISTS (
                       SELECT 1
                       FROM session_events
                       WHERE session_events.session_id = sessions.session_id
                         AND content ILIKE '%' || $4 || '%'
                   ));";
            AddSearchParameters(count, vendor, status, repo, text);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        if (total == 0 || offset >= total) return new SessionSearchPage([], total);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT {SessionColumns}
            FROM sessions
            WHERE ($1 IS NULL OR vendor = $1)
              AND ($2 IS NULL OR status = $2)
              AND ($3 IS NULL OR repo_hash = $3 OR (repo_owner || '/' || repo_name) = $3)
              AND ($4 IS NULL
                   OR title ILIKE '%' || $4 || '%'
                   OR EXISTS (
                       SELECT 1
                       FROM session_events
                       WHERE session_events.session_id = sessions.session_id
                         AND content ILIKE '%' || $4 || '%'
                   ))
            ORDER BY COALESCE(last_event_at, started_at) DESC, session_id DESC
            LIMIT $5 OFFSET $6;";
        AddSearchParameters(cmd, vendor, status, repo, text);
        cmd.Parameters.AddWithValue(limit);
        cmd.Parameters.AddWithValue(offset);

        var sessions = new List<SessionHeaderRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            sessions.Add(ReadSession(reader));
        }

        return new SessionSearchPage(sessions, total);
    }

    private static void AddSearchParameters(
        NpgsqlCommand command,
        string? vendor,
        string? status,
        string? repo,
        string? text) {
        // PostgreSQL cannot infer the type of a null parameter when it appears in
        // an `IS NULL OR column = parameter` predicate. Keep optional filters text-typed.
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)vendor ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)status ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)repo ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)text ?? DBNull.Value });
    }

    private static SessionHeaderRecord ReadSession(NpgsqlDataReader reader) => new() {
        SessionId = reader.GetString(0),
        Title = reader.IsDBNull(1) ? null : reader.GetString(1),
        Slug = reader.IsDBNull(2) ? null : reader.GetString(2),
        Vendor = reader.GetString(3),
        Model = reader.IsDBNull(4) ? null : reader.GetString(4),
        Status = reader.GetString(5),
        Visibility = reader.GetString(6),
        HiddenReason = reader.IsDBNull(7) ? null : reader.GetString(7),
        Disposition = reader.IsDBNull(8) ? null : reader.GetString(8),
        OwnerUserId = reader.GetString(9),
        MachineId = reader.IsDBNull(10) ? null : reader.GetString(10),
        DaemonId = reader.IsDBNull(11) ? null : reader.GetString(11),
        RepoHash = reader.IsDBNull(12) ? null : reader.GetString(12),
        RepoOwner = reader.IsDBNull(13) ? null : reader.GetString(13),
        RepoName = reader.IsDBNull(14) ? null : reader.GetString(14),
        Branch = reader.IsDBNull(15) ? null : reader.GetString(15),
        PrNumber = reader.IsDBNull(16) ? null : reader.GetInt32(16),
        PrTitle = reader.IsDBNull(17) ? null : reader.GetString(17),
        PrUrl = reader.IsDBNull(18) ? null : reader.GetString(18),
        PrHeadRef = reader.IsDBNull(19) ? null : reader.GetString(19),
        StartedAt = DateTimeOffset.Parse(reader.GetString(20), CultureInfo.InvariantCulture),
        EndedAt = reader.IsDBNull(21) ? null : DateTimeOffset.Parse(reader.GetString(21), CultureInfo.InvariantCulture),
        LastEventAt = reader.IsDBNull(22) ? null : DateTimeOffset.Parse(reader.GetString(22), CultureInfo.InvariantCulture),
        DurationMin = reader.IsDBNull(23) ? 0 : reader.GetDecimal(23),
        EventCount = reader.IsDBNull(24) ? 0 : reader.GetInt32(24),
        ToolCount = reader.IsDBNull(25) ? 0 : reader.GetInt32(25),
        TotalTokens = reader.IsDBNull(26) ? 0 : reader.GetInt64(26),
        TotalCostUsd = reader.IsDBNull(27) ? 0 : reader.GetDecimal(27),
        PreviousSessionId = reader.IsDBNull(28) ? null : reader.GetString(28),
        NextSessionId = reader.IsDBNull(29) ? null : reader.GetString(29),
        PrimaryPhase = reader.IsDBNull(30) ? null : reader.GetString(30),
        SecondaryPhase = reader.IsDBNull(31) ? null : reader.GetString(31),
        ClassificationConfidence = reader.IsDBNull(32) ? null : reader.GetDecimal(32),
        ClassificationSource = reader.IsDBNull(33) ? null : reader.GetString(33)
    };

    public async Task UpdateSessionAsync(SessionHeaderRecord session, CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try {
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE sessions SET
                    title = COALESCE($2, title),
                    slug = COALESCE($3, slug),
                    vendor = $4,
                    model = COALESCE($5, model),
                    status = $6,
                    visibility = $7,
                    hidden_reason = $8,
                    disposition = $9,
                    owner_user_id = $10,
                    machine_id = COALESCE($11, machine_id),
                    daemon_id = COALESCE($12, daemon_id),
                    repo_hash = COALESCE($13, repo_hash),
                    repo_owner = COALESCE($14, repo_owner),
                    repo_name = COALESCE($15, repo_name),
                    branch = COALESCE($16, branch),
                    pr_number = COALESCE($17, pr_number),
                    pr_title = COALESCE($18, pr_title),
                    pr_url = COALESCE($19, pr_url),
                    pr_head_ref = COALESCE($20, pr_head_ref),
                    started_at = $21,
                    ended_at = COALESCE($22, ended_at),
                    last_event_at = COALESCE($23, last_event_at),
                    duration_min = $24,
                    event_count = $25,
                    tool_count = $26,
                    total_tokens = $27,
                    total_cost_usd = $28,
                    previous_session_id = $29,
                    next_session_id = $30,
                    primary_phase = $31,
                    secondary_phase = $32,
                    classification_confidence = $33,
                    classification_source = $34
                WHERE session_id = $1;
            ", conn, tx)) {
                cmd.Parameters.AddWithValue(session.SessionId);
                cmd.Parameters.AddWithValue((object?)session.Title ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.Slug ?? DBNull.Value);
                cmd.Parameters.AddWithValue(session.Vendor);
                cmd.Parameters.AddWithValue((object?)session.Model ?? DBNull.Value);
                cmd.Parameters.AddWithValue(session.Status);
                cmd.Parameters.AddWithValue(session.Visibility);
                cmd.Parameters.AddWithValue((object?)session.HiddenReason ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.Disposition ?? DBNull.Value);
                cmd.Parameters.AddWithValue(session.OwnerUserId);
                cmd.Parameters.AddWithValue((object?)session.MachineId ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.DaemonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.RepoHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.RepoOwner ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.RepoName ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.Branch ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.PrNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.PrTitle ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.PrUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.PrHeadRef ?? DBNull.Value);
                cmd.Parameters.AddWithValue(EventTimestamp.ToUtcString(session.StartedAt));
                cmd.Parameters.AddWithValue(session.EndedAt.HasValue ? EventTimestamp.ToUtcString(session.EndedAt.Value) : (object)DBNull.Value);
                cmd.Parameters.AddWithValue(session.LastEventAt.HasValue ? EventTimestamp.ToUtcString(session.LastEventAt.Value) : (object)DBNull.Value);
                cmd.Parameters.AddWithValue(session.DurationMin);
                cmd.Parameters.AddWithValue(session.EventCount);
                cmd.Parameters.AddWithValue(session.ToolCount);
                cmd.Parameters.AddWithValue(session.TotalTokens);
                cmd.Parameters.AddWithValue(session.TotalCostUsd);
                cmd.Parameters.AddWithValue((object?)session.PreviousSessionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.NextSessionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.PrimaryPhase ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.SecondaryPhase ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.ClassificationConfidence ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)session.ClassificationSource ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (session.PreviousSessionId is { Length: > 0 } previousId) {
                await using var link = new NpgsqlCommand(@"
                    UPDATE sessions SET next_session_id = $1
                    WHERE session_id = $2
                      AND (next_session_id IS NULL OR next_session_id = $1);
                ", conn, tx);
                link.Parameters.AddWithValue(session.SessionId);
                link.Parameters.AddWithValue(previousId);
                await link.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        } catch {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task UpdateSessionTitleAsync(string sessionId, string title, CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE sessions SET title = $2 WHERE session_id = $1;", conn);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(title);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PatchSessionStartAsync(
        string sessionId,
        string? ownerUserId,
        string? defaultVisibility,
        SessionStartPatch? patch = null,
        CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE sessions SET
                owner_user_id = CASE
                    WHEN $2 IS NOT NULL THEN $2
                    ELSE owner_user_id
                END,
                visibility = CASE
                    WHEN $3 IS NOT NULL THEN $3
                    ELSE visibility
                END,
                started_at = COALESCE($4, started_at),
                model = COALESCE($5, model),
                slug = COALESCE($6, slug),
                previous_session_id = COALESCE($7, previous_session_id),
                repo_hash = COALESCE($8, repo_hash),
                repo_owner = COALESCE($9, repo_owner),
                repo_name = COALESCE($10, repo_name),
                branch = COALESCE($11, branch),
                pr_number = COALESCE($12, pr_number),
                pr_title = COALESCE($13, pr_title),
                pr_url = COALESCE($14, pr_url),
                pr_head_ref = COALESCE($15, pr_head_ref)
            WHERE session_id = $1;
        ";
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(ownerUserId) ? DBNull.Value : ownerUserId);
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(defaultVisibility) ? DBNull.Value : defaultVisibility);
        cmd.Parameters.AddWithValue(patch?.StartedAt is { } startedAt ? EventTimestamp.ToUtcString(startedAt) : DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch?.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch?.Slug ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch?.PreviousSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch?.RepoHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch?.RepoOwner ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch?.RepoName ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch?.Branch ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch?.PrNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch?.PrTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch?.PrUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch?.PrHeadRef ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateRepositoryMetadataAsync(
            string sessionId,
            string? repoHash,
            string? repoOwner,
            string? repoName,
            string? branch,
            int? prNumber,
            string? prTitle,
            string? prUrl,
            string? prHeadRef,
            CancellationToken ct = default
        ) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE sessions SET
                repo_hash = COALESCE($2, repo_hash),
                repo_owner = COALESCE($3, repo_owner),
                repo_name = COALESCE($4, repo_name),
                branch = COALESCE($5, branch),
                pr_number = COALESCE($6, pr_number),
                pr_title = COALESCE($7, pr_title),
                pr_url = COALESCE($8, pr_url),
                pr_head_ref = COALESCE($9, pr_head_ref)
            WHERE session_id = $1;
        ";
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue((object?)repoHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)repoOwner ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)repoName ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)branch ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)prNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)prTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)prUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)prHeadRef ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PersistEvalRunAsync(EvalRunRecord run, IReadOnlyList<EvalVerdictRecord> verdicts, CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try {
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO eval_runs (
                    eval_run_id, session_id, judge_model, overall_score, summary,
                    retrospective_json, retrospective_prompt_version, evaluated_at
                ) VALUES (
                    $1, $2, $3, $4, $5, $6, $7, $8
                )
                ON CONFLICT(eval_run_id) DO UPDATE SET
                    overall_score = EXCLUDED.overall_score,
                    summary = EXCLUDED.summary,
                    retrospective_json = EXCLUDED.retrospective_json,
                    retrospective_prompt_version = EXCLUDED.retrospective_prompt_version,
                    evaluated_at = EXCLUDED.evaluated_at;
            ", conn, tx)) {
                cmd.Parameters.AddWithValue(run.EvalRunId);
                cmd.Parameters.AddWithValue(run.SessionId);
                cmd.Parameters.AddWithValue(run.JudgeModel);
                cmd.Parameters.AddWithValue(run.OverallScore);
                cmd.Parameters.AddWithValue(run.Summary);
                cmd.Parameters.AddWithValue((object?)run.RetrospectiveJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)run.RetrospectivePromptVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue(EventTimestamp.ToUtcString(run.EvaluatedAt));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var clear = new NpgsqlCommand(
                "DELETE FROM eval_verdicts WHERE eval_run_id = $1;", conn, tx)) {
                clear.Parameters.AddWithValue(run.EvalRunId);
                await clear.ExecuteNonQueryAsync(ct);
            }

            foreach (var verdict in verdicts) {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO eval_verdicts (
                        eval_run_id, category, question_id, score, verdict, finding,
                        evidence, recommendation, tools_used, prompt_version
                    ) VALUES (
                        $1, $2, $3, $4, $5, $6, $7, $8, $9, $10
                    );
                ", conn, tx);
                cmd.Parameters.AddWithValue(verdict.EvalRunId);
                cmd.Parameters.AddWithValue(verdict.Category);
                cmd.Parameters.AddWithValue(verdict.QuestionId);
                cmd.Parameters.AddWithValue(verdict.Score);
                cmd.Parameters.AddWithValue(verdict.Verdict);
                cmd.Parameters.AddWithValue(verdict.Finding);
                cmd.Parameters.AddWithValue((object?)verdict.Evidence ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)verdict.Recommendation ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)verdict.ToolsUsed ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)verdict.PromptVersion ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        } catch {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<SessionEvaluation?> GetLatestEvaluationAsync(string sessionId, CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        EvalRunRecord? run = null;
        await using (var runCommand = conn.CreateCommand()) {
            runCommand.CommandText = @"
                SELECT eval_run_id, session_id, judge_model, overall_score, summary,
                       retrospective_json, retrospective_prompt_version, evaluated_at
                FROM eval_runs
                WHERE session_id = $1
                ORDER BY evaluated_at DESC, eval_run_id DESC
                LIMIT 1;";
            runCommand.Parameters.AddWithValue(sessionId);
            await using var reader = await runCommand.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) {
                run = new EvalRunRecord {
                    EvalRunId = reader.GetString(0),
                    SessionId = reader.GetString(1),
                    JudgeModel = reader.GetString(2),
                    OverallScore = reader.GetInt32(3),
                    Summary = reader.GetString(4),
                    RetrospectiveJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                    RetrospectivePromptVersion = reader.IsDBNull(6) ? null : reader.GetString(6),
                    EvaluatedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)
                };
            }
        }

        if (run is null) return null;

        var verdicts = new List<EvalVerdictRecord>();
        await using (var verdictCommand = conn.CreateCommand()) {
            verdictCommand.CommandText = @"
                SELECT eval_run_id, category, question_id, score, verdict, finding,
                       evidence, recommendation, tools_used, prompt_version
                FROM eval_verdicts
                WHERE eval_run_id = $1
                ORDER BY category, question_id;";
            verdictCommand.Parameters.AddWithValue(run.EvalRunId);
            await using var reader = await verdictCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                verdicts.Add(new EvalVerdictRecord {
                    EvalRunId = reader.GetString(0),
                    Category = reader.GetString(1),
                    QuestionId = reader.GetString(2),
                    Score = reader.GetInt32(3),
                    Verdict = reader.GetString(4),
                    Finding = reader.GetString(5),
                    Evidence = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Recommendation = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ToolsUsed = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    PromptVersion = reader.IsDBNull(9) ? null : reader.GetString(9)
                });
            }
        }

        return new SessionEvaluation(run, verdicts);
    }

    public async Task UpdateRollupAsync(
            string sessionId,
            int eventCount,
            int toolCount,
            long totalTokens,
            decimal totalCostUsd,
            decimal durationMin,
            DateTimeOffset? lastEventAt,
            CancellationToken ct = default
        ) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE sessions SET
                last_event_at = CASE
                    WHEN $2 IS NULL THEN last_event_at
                    WHEN last_event_at IS NULL OR $2 > last_event_at THEN $2
                    ELSE last_event_at
                END,
                duration_min = GREATEST(duration_min, $3),
                event_count = GREATEST(event_count, $4),
                tool_count = GREATEST(tool_count, $5),
                total_tokens = GREATEST(total_tokens, $6),
                total_cost_usd = GREATEST(total_cost_usd, $7)
            WHERE session_id = $1;
        ";

        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(lastEventAt.HasValue ? EventTimestamp.ToUtcString(lastEventAt.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue(durationMin);
        cmd.Parameters.AddWithValue(eventCount);
        cmd.Parameters.AddWithValue(toolCount);
        cmd.Parameters.AddWithValue(totalTokens);
        cmd.Parameters.AddWithValue(totalCostUsd);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public class PostgresMachineRepository : IMachineRepository {
    private readonly NpgsqlDataSource _dataSource;

    public PostgresMachineRepository(NpgsqlDataSource dataSource) {
        _dataSource = dataSource;
    }

    public async Task<bool> EnrollAsync(string machineId, string hostname, string os, string arch, string tokenHash, DateTimeOffset now, CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO machines (machine_id, hostname, os, arch, client_id, registered_at, last_heartbeat)
            VALUES ($1, $2, $3, $4, $5, $6, $6)
            ON CONFLICT(machine_id) DO NOTHING
            RETURNING machine_id;
        ";
        cmd.Parameters.AddWithValue(machineId);
        cmd.Parameters.AddWithValue(hostname);
        cmd.Parameters.AddWithValue(os);
        cmd.Parameters.AddWithValue(arch);
        cmd.Parameters.AddWithValue(tokenHash);
        cmd.Parameters.AddWithValue(EventTimestamp.ToUtcString(now));

        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    public async Task<string?> HeartbeatAsync(string tokenHash, DateTimeOffset now, CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE machines SET last_heartbeat = $1
            WHERE client_id = $2
            RETURNING machine_id;
        ";
        cmd.Parameters.AddWithValue(EventTimestamp.ToUtcString(now));
        cmd.Parameters.AddWithValue(tokenHash);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }
}
