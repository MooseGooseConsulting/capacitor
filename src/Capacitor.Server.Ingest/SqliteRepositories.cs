using System.Globalization;
using Microsoft.Data.Sqlite;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Ingest;

public class SqliteWatermarkRepository : ISessionWatermarkRepository {
    private readonly SqliteConnection _connection;

    public SqliteWatermarkRepository(SqliteConnection connection) {
        _connection = connection;
    }

    public async Task<int?> GetLastLineNumberAsync(string sessionId, string agentId = "", CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT last_line_number FROM session_watermarks WHERE session_id = $session_id AND agent_id = $agent_id;";
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$agent_id", agentId ?? string.Empty);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result == null || result is DBNull) return null;
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task UpdateWatermarkAsync(string sessionId, string agentId, int lastLineNumber, long byteOffset = 0, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO session_watermarks (session_id, agent_id, last_line_number, byte_offset, updated_at)
            VALUES ($session_id, $agent_id, $last_line_number, $byte_offset, $updated_at)
            ON CONFLICT(session_id, agent_id) DO UPDATE SET
                last_line_number = excluded.last_line_number,
                byte_offset = excluded.byte_offset,
                updated_at = excluded.updated_at
            WHERE excluded.last_line_number > session_watermarks.last_line_number
               OR (excluded.last_line_number = session_watermarks.last_line_number
                   AND excluded.byte_offset > session_watermarks.byte_offset);
        ";

        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$agent_id", agentId ?? string.Empty);
        cmd.Parameters.AddWithValue("$last_line_number", lastLineNumber);
        cmd.Parameters.AddWithValue("$byte_offset", byteOffset);
        cmd.Parameters.AddWithValue("$updated_at", SqliteUtc.Format(DateTimeOffset.UtcNow));

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public class SqliteSessionRepository : ISessionRepository {
    private readonly SqliteConnection _connection;

    public SqliteSessionRepository(SqliteConnection connection) {
        _connection = connection;
    }

    public Task<SessionHeaderRecord> GetOrCreatePlaceholderAsync(string sessionId, string vendor, string? ownerUserId = null, CancellationToken ct = default) =>
        GetOrCreatePlaceholderAsync(sessionId, vendor, ownerUserId, defaultVisibility: null, ct);

    public async Task<SessionHeaderRecord> GetOrCreatePlaceholderAsync(string sessionId, string vendor, string? ownerUserId, string? defaultVisibility, CancellationToken ct = default) {
        var now = DateTimeOffset.UtcNow;
        var visibility = string.IsNullOrEmpty(defaultVisibility) ? "private" : defaultVisibility;
        using (var cmd = _connection.CreateCommand()) {
            cmd.CommandText = @"
                INSERT INTO sessions (
                    session_id, vendor, owner_user_id, status, visibility, started_at
                ) VALUES (
                    $session_id, $vendor, $owner_user_id, $status, $visibility, $started_at
                )
                ON CONFLICT(session_id) DO NOTHING;
            ";

            cmd.Parameters.AddWithValue("$session_id", sessionId);
            cmd.Parameters.AddWithValue("$vendor", vendor);
            cmd.Parameters.AddWithValue("$owner_user_id", ownerUserId ?? "anonymous");
            cmd.Parameters.AddWithValue("$status", "active");
            cmd.Parameters.AddWithValue("$visibility", visibility);
            cmd.Parameters.AddWithValue("$started_at", SqliteUtc.Format(now));

            await cmd.ExecuteNonQueryAsync(ct);
        }

        var persisted = await GetSessionAsync(sessionId, ct);
        if (persisted is null) {
            throw new InvalidOperationException($"Placeholder insert for session '{sessionId}' did not produce a row.");
        }

        return persisted;
    }

    public async Task<SessionHeaderRecord?> GetSessionAsync(string sessionId, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT session_id, title, slug, vendor, model, status, visibility, hidden_reason, disposition, owner_user_id, machine_id, daemon_id,
                   repo_hash, repo_owner, repo_name, branch, pr_number, pr_title, pr_url, pr_head_ref,
                   started_at, ended_at, last_event_at, duration_min, event_count, tool_count, total_tokens, total_cost_usd,
                   previous_session_id, next_session_id, primary_phase, secondary_phase, classification_confidence, classification_source
            FROM sessions
            WHERE session_id = $session_id;";
        cmd.Parameters.AddWithValue("$session_id", sessionId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
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

    public async Task UpdateSessionAsync(SessionHeaderRecord session, CancellationToken ct = default) {
        using var tx = _connection.BeginTransaction();
        try {
            using (var cmd = _connection.CreateCommand()) {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE sessions SET
                        title = COALESCE($title, title),
                        slug = COALESCE($slug, slug),
                        vendor = $vendor,
                        model = COALESCE($model, model),
                        status = $status,
                        visibility = $visibility,
                        hidden_reason = $hidden_reason,
                        disposition = $disposition,
                        owner_user_id = $owner_user_id,
                        machine_id = COALESCE($machine_id, machine_id),
                        daemon_id = COALESCE($daemon_id, daemon_id),
                        repo_hash = COALESCE($repo_hash, repo_hash),
                        repo_owner = COALESCE($repo_owner, repo_owner),
                        repo_name = COALESCE($repo_name, repo_name),
                        branch = COALESCE($branch, branch),
                        pr_number = COALESCE($pr_number, pr_number),
                        pr_title = COALESCE($pr_title, pr_title),
                        pr_url = COALESCE($pr_url, pr_url),
                        pr_head_ref = COALESCE($pr_head_ref, pr_head_ref),
                        started_at = $started_at,
                        ended_at = COALESCE($ended_at, ended_at),
                        last_event_at = COALESCE($last_event_at, last_event_at),
                        duration_min = $duration_min,
                        event_count = $event_count,
                        tool_count = $tool_count,
                        total_tokens = $total_tokens,
                        total_cost_usd = $total_cost_usd,
                        previous_session_id = $previous_session_id,
                        next_session_id = $next_session_id,
                        primary_phase = $primary_phase,
                        secondary_phase = $secondary_phase,
                        classification_confidence = $classification_confidence,
                        classification_source = $classification_source
                    WHERE session_id = $session_id;
                ";

                cmd.Parameters.AddWithValue("$session_id", session.SessionId);
                cmd.Parameters.AddWithValue("$title", (object?)session.Title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$slug", (object?)session.Slug ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$vendor", session.Vendor);
                cmd.Parameters.AddWithValue("$model", (object?)session.Model ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$status", session.Status);
                cmd.Parameters.AddWithValue("$visibility", session.Visibility);
                cmd.Parameters.AddWithValue("$hidden_reason", (object?)session.HiddenReason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$disposition", (object?)session.Disposition ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$owner_user_id", session.OwnerUserId);
                cmd.Parameters.AddWithValue("$machine_id", (object?)session.MachineId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$daemon_id", (object?)session.DaemonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$repo_hash", (object?)session.RepoHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$repo_owner", (object?)session.RepoOwner ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$repo_name", (object?)session.RepoName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$branch", (object?)session.Branch ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pr_number", (object?)session.PrNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pr_title", (object?)session.PrTitle ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pr_url", (object?)session.PrUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pr_head_ref", (object?)session.PrHeadRef ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$started_at", SqliteUtc.Format(session.StartedAt));
                cmd.Parameters.AddWithValue("$ended_at", session.EndedAt.HasValue ? SqliteUtc.Format(session.EndedAt.Value) : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$last_event_at", session.LastEventAt.HasValue ? SqliteUtc.Format(session.LastEventAt.Value) : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$duration_min", session.DurationMin);
                cmd.Parameters.AddWithValue("$event_count", session.EventCount);
                cmd.Parameters.AddWithValue("$tool_count", session.ToolCount);
                cmd.Parameters.AddWithValue("$total_tokens", session.TotalTokens);
                cmd.Parameters.AddWithValue("$total_cost_usd", session.TotalCostUsd);
                cmd.Parameters.AddWithValue("$previous_session_id", (object?)session.PreviousSessionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$next_session_id", (object?)session.NextSessionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$primary_phase", (object?)session.PrimaryPhase ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$secondary_phase", (object?)session.SecondaryPhase ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$classification_confidence", (object?)session.ClassificationConfidence ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$classification_source", (object?)session.ClassificationSource ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (session.PreviousSessionId is { Length: > 0 } previousId) {
                using var link = _connection.CreateCommand();
                link.Transaction = tx;
                link.CommandText = @"
                    UPDATE sessions SET next_session_id = $session_id
                    WHERE session_id = $previous_session_id
                      AND (next_session_id IS NULL OR next_session_id = $session_id);
                ";
                link.Parameters.AddWithValue("$session_id", session.SessionId);
                link.Parameters.AddWithValue("$previous_session_id", previousId);
                await link.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        } catch {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task PatchSessionStartAsync(string sessionId, string? ownerUserId, string? defaultVisibility, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE sessions SET
                owner_user_id = CASE
                    WHEN $owner_user_id IS NOT NULL THEN $owner_user_id
                    ELSE owner_user_id
                END,
                visibility = CASE
                    WHEN $visibility IS NOT NULL THEN $visibility
                    ELSE visibility
                END
            WHERE session_id = $session_id;
        ";
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$owner_user_id", string.IsNullOrEmpty(ownerUserId) ? DBNull.Value : ownerUserId);
        cmd.Parameters.AddWithValue("$visibility", string.IsNullOrEmpty(defaultVisibility) ? DBNull.Value : defaultVisibility);
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
            CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE sessions SET
                repo_hash = COALESCE($repo_hash, repo_hash),
                repo_owner = COALESCE($repo_owner, repo_owner),
                repo_name = COALESCE($repo_name, repo_name),
                branch = COALESCE($branch, branch),
                pr_number = COALESCE($pr_number, pr_number),
                pr_title = COALESCE($pr_title, pr_title),
                pr_url = COALESCE($pr_url, pr_url),
                pr_head_ref = COALESCE($pr_head_ref, pr_head_ref)
            WHERE session_id = $session_id;
        ";
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$repo_hash", (object?)repoHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repo_owner", (object?)repoOwner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repo_name", (object?)repoName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$branch", (object?)branch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr_number", (object?)prNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr_title", (object?)prTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr_url", (object?)prUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr_head_ref", (object?)prHeadRef ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PersistEvalRunAsync(EvalRunRecord run, IReadOnlyList<EvalVerdictRecord> verdicts, CancellationToken ct = default) {
        using var tx = _connection.BeginTransaction();
        try {
            using (var cmd = _connection.CreateCommand()) {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO eval_runs (
                        eval_run_id, session_id, judge_model, overall_score, summary,
                        retrospective_json, retrospective_prompt_version, evaluated_at
                    ) VALUES (
                        $eval_run_id, $session_id, $judge_model, $overall_score, $summary,
                        $retrospective_json, $retrospective_prompt_version, $evaluated_at
                    )
                    ON CONFLICT(eval_run_id) DO UPDATE SET
                        overall_score = excluded.overall_score,
                        summary = excluded.summary,
                        retrospective_json = excluded.retrospective_json,
                        retrospective_prompt_version = excluded.retrospective_prompt_version,
                        evaluated_at = excluded.evaluated_at;
                ";
                cmd.Parameters.AddWithValue("$eval_run_id", run.EvalRunId);
                cmd.Parameters.AddWithValue("$session_id", run.SessionId);
                cmd.Parameters.AddWithValue("$judge_model", run.JudgeModel);
                cmd.Parameters.AddWithValue("$overall_score", run.OverallScore);
                cmd.Parameters.AddWithValue("$summary", run.Summary);
                cmd.Parameters.AddWithValue("$retrospective_json", (object?)run.RetrospectiveJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$retrospective_prompt_version", (object?)run.RetrospectivePromptVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$evaluated_at", SqliteUtc.Format(run.EvaluatedAt));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using (var clear = _connection.CreateCommand()) {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM eval_verdicts WHERE eval_run_id = $eval_run_id;";
                clear.Parameters.AddWithValue("$eval_run_id", run.EvalRunId);
                await clear.ExecuteNonQueryAsync(ct);
            }

            foreach (var verdict in verdicts) {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO eval_verdicts (
                        eval_run_id, category, question_id, score, verdict, finding,
                        evidence, recommendation, tools_used, prompt_version
                    ) VALUES (
                        $eval_run_id, $category, $question_id, $score, $verdict, $finding,
                        $evidence, $recommendation, $tools_used, $prompt_version
                    );
                ";
                cmd.Parameters.AddWithValue("$eval_run_id", verdict.EvalRunId);
                cmd.Parameters.AddWithValue("$category", verdict.Category);
                cmd.Parameters.AddWithValue("$question_id", verdict.QuestionId);
                cmd.Parameters.AddWithValue("$score", verdict.Score);
                cmd.Parameters.AddWithValue("$verdict", verdict.Verdict);
                cmd.Parameters.AddWithValue("$finding", verdict.Finding);
                cmd.Parameters.AddWithValue("$evidence", (object?)verdict.Evidence ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$recommendation", (object?)verdict.Recommendation ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tools_used", (object?)verdict.ToolsUsed ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$prompt_version", (object?)verdict.PromptVersion ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        } catch {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
