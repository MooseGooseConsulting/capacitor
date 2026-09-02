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
        GetOrCreatePlaceholderAsync(sessionId, vendor, ownerUserId, defaultVisibility: null, observedStartedAt: null, ct: ct);

    public async Task<SessionHeaderRecord> GetOrCreatePlaceholderAsync(string sessionId, string vendor, string? ownerUserId, string? defaultVisibility, CancellationToken ct = default) {
        return await GetOrCreatePlaceholderAsync(sessionId, vendor, ownerUserId, defaultVisibility, observedStartedAt: null, ct: ct);
    }

    public async Task<SessionHeaderRecord> GetOrCreatePlaceholderAsync(
        string sessionId,
        string vendor,
        string? ownerUserId,
        string? defaultVisibility,
        DateTimeOffset? observedStartedAt,
        CancellationToken ct = default) {
        var startedAt = observedStartedAt ?? DateTimeOffset.UtcNow;
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
            cmd.Parameters.AddWithValue("$started_at", SqliteUtc.Format(startedAt));

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
            ToolCount = reader.IsDBNull(25) ? null : reader.GetInt32(25),
            TotalTokens = reader.IsDBNull(26) ? null : reader.GetInt64(26),
            TotalCostUsd = reader.IsDBNull(27) ? null : reader.GetDecimal(27),
            PreviousSessionId = reader.IsDBNull(28) ? null : reader.GetString(28),
            NextSessionId = reader.IsDBNull(29) ? null : reader.GetString(29),
            PrimaryPhase = reader.IsDBNull(30) ? null : reader.GetString(30),
            SecondaryPhase = reader.IsDBNull(31) ? null : reader.GetString(31),
            ClassificationConfidence = reader.IsDBNull(32) ? null : reader.GetDecimal(32),
            ClassificationSource = reader.IsDBNull(33) ? null : reader.GetString(33)
        };
    }

    public async Task<bool> CompleteSessionAsync(string sessionId, DateTimeOffset endedAt, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE sessions SET
                status = 'completed',
                ended_at = COALESCE(ended_at, $ended_at)
            WHERE session_id = $session_id;";
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$ended_at", SqliteUtc.Format(endedAt));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<SessionSearchPage> SearchSessionsAsync(SessionSearchQuery query, CancellationToken ct = default) {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var offset = Math.Max(query.Offset, 0);
        var text = string.IsNullOrWhiteSpace(query.Query) ? null : query.Query.Trim();
        var author = string.IsNullOrWhiteSpace(query.Author) ? null : query.Author.Trim();
        var repo = string.IsNullOrWhiteSpace(query.Repo) ? null : query.Repo.Trim();
        var vendor = string.IsNullOrWhiteSpace(query.Vendor) ? null : query.Vendor.Trim();
        var status = string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim();

        const string where = @"
            ($vendor IS NULL OR vendor = $vendor)
            AND ($status IS NULL OR status = $status)
            AND ($repo IS NULL OR EXISTS (
                SELECT 1
                FROM session_repositories associations
                WHERE associations.session_id = sessions.session_id
                  AND (associations.repo_hash = $repo
                       OR (associations.repo_owner || '/' || associations.repo_name) = $repo)
            ))
            AND ($author IS NULL OR owner_user_id LIKE '%' || $author || '%')
            AND ($query IS NULL
                 OR title LIKE '%' || $query || '%'
                 OR EXISTS (
                     SELECT 1 FROM session_events
                     WHERE session_events.session_id = sessions.session_id
                       AND content LIKE '%' || $query || '%'
                 ))";

        using var count = _connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM sessions WHERE {where};";
        AddSearchParameters(count, text, author, repo, vendor, status);
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

        var ids = new List<string>();
        using (var select = _connection.CreateCommand()) {
            select.CommandText = $@"
                SELECT session_id FROM sessions
                WHERE {where}
                ORDER BY COALESCE(last_event_at, started_at) DESC, session_id DESC
                LIMIT $limit OFFSET $offset;";
            AddSearchParameters(select, text, author, repo, vendor, status);
            select.Parameters.AddWithValue("$limit", limit);
            select.Parameters.AddWithValue("$offset", offset);
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        }

        var sessions = new List<SessionHeaderRecord>(ids.Count);
        foreach (var id in ids) {
            var session = await GetSessionAsync(id, ct);
            if (session is not null) sessions.Add(session);
        }
        return new SessionSearchPage(sessions, total);
    }

    private static void AddSearchParameters(
        SqliteCommand command,
        string? text,
        string? author,
        string? repo,
        string? vendor,
        string? status) {
        command.Parameters.AddWithValue("$query", (object?)text ?? DBNull.Value);
        command.Parameters.AddWithValue("$author", (object?)author ?? DBNull.Value);
        command.Parameters.AddWithValue("$repo", (object?)repo ?? DBNull.Value);
        command.Parameters.AddWithValue("$vendor", (object?)vendor ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
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
                cmd.Parameters.AddWithValue("$tool_count", (object?)session.ToolCount ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$total_tokens", (object?)session.TotalTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$total_cost_usd", (object?)session.TotalCostUsd ?? DBNull.Value);
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

    public async Task UpdateSessionTitleAsync(string sessionId, string title, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET title = $title WHERE session_id = $session_id;";
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$title", title);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertSubagentRunAsync(SubagentRunRecord run, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO subagent_runs (
                parent_session_id, agent_id, agent_type, role, prompt, spawned_at
            ) VALUES (
                $parent_session_id, $agent_id, $agent_type, $role, $prompt, $spawned_at
            ) ON CONFLICT(parent_session_id, agent_id) DO UPDATE SET
                agent_type = COALESCE(NULLIF(excluded.agent_type, ''), subagent_runs.agent_type),
                role = COALESCE(NULLIF(excluded.role, ''), subagent_runs.role),
                prompt = COALESCE(NULLIF(excluded.prompt, ''), subagent_runs.prompt);";
        cmd.Parameters.AddWithValue("$parent_session_id", run.ParentSessionId);
        cmd.Parameters.AddWithValue("$agent_id", run.AgentId);
        cmd.Parameters.AddWithValue("$agent_type", (object?)run.AgentType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$role", (object?)run.Role ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prompt", (object?)run.Prompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$spawned_at", SqliteUtc.Format(run.SpawnedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> CompleteSubagentRunAsync(
        string parentSessionId,
        string agentId,
        DateTimeOffset stoppedAt,
        string? exitStatus,
        CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE subagent_runs SET
                stopped_at = $stopped_at,
                duration_ms = MAX(0, CAST((julianday($stopped_at) - julianday(spawned_at)) * 86400000 AS INTEGER)),
                exit_status = COALESCE($exit_status, exit_status)
            WHERE parent_session_id = $parent_session_id AND agent_id = $agent_id;";
        cmd.Parameters.AddWithValue("$parent_session_id", parentSessionId);
        cmd.Parameters.AddWithValue("$agent_id", agentId);
        cmd.Parameters.AddWithValue("$stopped_at", SqliteUtc.Format(stoppedAt));
        cmd.Parameters.AddWithValue("$exit_status", (object?)exitStatus ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task PatchSessionStartAsync(
        string sessionId,
        string? ownerUserId,
        string? defaultVisibility,
        SessionStartPatch? patch = null,
        CancellationToken ct = default) {
        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            UPDATE sessions SET
                owner_user_id = CASE
                    WHEN $owner_user_id IS NOT NULL THEN $owner_user_id
                    ELSE owner_user_id
                END,
                visibility = CASE
                    WHEN visibility = '' THEN COALESCE($visibility, visibility)
                    ELSE visibility
                END,
                started_at = COALESCE(started_at, $started_at),
                model = COALESCE($model, model),
                slug = COALESCE($slug, slug),
                previous_session_id = COALESCE($previous_session_id, previous_session_id),
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
        cmd.Parameters.AddWithValue("$owner_user_id", string.IsNullOrEmpty(ownerUserId) ? DBNull.Value : ownerUserId);
        cmd.Parameters.AddWithValue("$visibility", string.IsNullOrEmpty(defaultVisibility) ? DBNull.Value : defaultVisibility);
        cmd.Parameters.AddWithValue("$started_at", patch?.StartedAt is { } startedAt ? EventTimestamp.ToUtcString(startedAt) : DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)patch?.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$slug", (object?)patch?.Slug ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$previous_session_id", (object?)patch?.PreviousSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repo_hash", (object?)patch?.RepoHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repo_owner", (object?)patch?.RepoOwner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repo_name", (object?)patch?.RepoName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$branch", (object?)patch?.Branch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr_number", (object?)patch?.PrNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr_title", (object?)patch?.PrTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr_url", (object?)patch?.PrUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr_head_ref", (object?)patch?.PrHeadRef ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        if (patch?.PreviousSessionId is { Length: > 0 } previousSessionId) {
            using var link = _connection.CreateCommand();
            link.Transaction = tx;
            link.CommandText = @"
                UPDATE sessions SET next_session_id = $session_id
                WHERE session_id = $previous_session_id
                  AND (next_session_id IS NULL OR next_session_id = $session_id);";
            link.Parameters.AddWithValue("$session_id", sessionId);
            link.Parameters.AddWithValue("$previous_session_id", previousSessionId);
            await link.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
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

    public async Task RecordRepositoryAssociationAsync(
        string sessionId,
        RepositoryEvidence evidence,
        CancellationToken ct = default) {
        if (!evidence.HasRepository) return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO session_repositories (
                session_id, repo_hash, repo_owner, repo_name, event_count, is_primary, created_at, updated_at
            ) VALUES ($session_id, $repo_hash, $repo_owner, $repo_name, 0, 0, $now, $now)
            ON CONFLICT (session_id, repo_hash) DO UPDATE SET
                repo_owner = COALESCE(excluded.repo_owner, session_repositories.repo_owner),
                repo_name = COALESCE(excluded.repo_name, session_repositories.repo_name),
                updated_at = excluded.updated_at;";
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$repo_hash", evidence.RepoHash!);
        cmd.Parameters.AddWithValue("$repo_owner", (object?)evidence.RepoOwner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repo_name", (object?)evidence.RepoName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", SqliteUtc.Format(DateTimeOffset.UtcNow));
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

    public async Task<SessionEvaluation?> GetLatestEvaluationAsync(string sessionId, CancellationToken ct = default) {
        EvalRunRecord? run = null;
        using (var runCommand = _connection.CreateCommand()) {
            runCommand.CommandText = @"
                SELECT eval_run_id, session_id, judge_model, overall_score, summary,
                       retrospective_json, retrospective_prompt_version, evaluated_at
                FROM eval_runs
                WHERE session_id = $session_id
                ORDER BY evaluated_at DESC, eval_run_id DESC
                LIMIT 1;";
            runCommand.Parameters.AddWithValue("$session_id", sessionId);
            using var reader = await runCommand.ExecuteReaderAsync(ct);
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
        using (var verdictCommand = _connection.CreateCommand()) {
            verdictCommand.CommandText = @"
                SELECT eval_run_id, category, question_id, score, verdict, finding,
                       evidence, recommendation, tools_used, prompt_version
                FROM eval_verdicts
                WHERE eval_run_id = $eval_run_id
                ORDER BY category, question_id;";
            verdictCommand.Parameters.AddWithValue("$eval_run_id", run.EvalRunId);
            using var reader = await verdictCommand.ExecuteReaderAsync(ct);
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
            int? toolCount,
            long? totalTokens,
            decimal? totalCostUsd,
            decimal durationMin,
            DateTimeOffset? lastEventAt,
            CancellationToken ct = default
        ) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE sessions SET
                last_event_at = CASE
                    WHEN $last_event_at IS NULL THEN last_event_at
                    WHEN last_event_at IS NULL OR $last_event_at > last_event_at THEN $last_event_at
                    ELSE last_event_at
                END,
                duration_min = CASE WHEN $duration_min > duration_min THEN $duration_min ELSE duration_min END,
                event_count = CASE WHEN $event_count > event_count THEN $event_count ELSE event_count END,
                tool_count = CASE
                    WHEN $tool_count IS NULL THEN tool_count
                    WHEN tool_count IS NULL OR $tool_count > tool_count THEN $tool_count
                    ELSE tool_count
                END,
                total_tokens = CASE
                    WHEN $total_tokens IS NULL THEN total_tokens
                    WHEN total_tokens IS NULL OR $total_tokens > total_tokens THEN $total_tokens
                    ELSE total_tokens
                END,
                total_cost_usd = CASE
                    WHEN $total_cost_usd IS NULL THEN total_cost_usd
                    WHEN total_cost_usd IS NULL OR $total_cost_usd > total_cost_usd THEN $total_cost_usd
                    ELSE total_cost_usd
                END
            WHERE session_id = $session_id;
        ";

        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$last_event_at", lastEventAt.HasValue ? SqliteUtc.Format(lastEventAt.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$duration_min", durationMin);
        cmd.Parameters.AddWithValue("$event_count", eventCount);
        cmd.Parameters.AddWithValue("$tool_count", (object?)toolCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$total_tokens", (object?)totalTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$total_cost_usd", (object?)totalCostUsd ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public class SqliteMachineRepository : IMachineRepository {
    private readonly SqliteConnection _connection;

    public SqliteMachineRepository(SqliteConnection connection) {
        _connection = connection;
    }

    public async Task<bool> EnrollAsync(string machineId, string hostname, string os, string arch, string tokenHash, DateTimeOffset now, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO machines (machine_id, hostname, os, arch, client_id, registered_at, last_heartbeat)
            VALUES ($machine_id, $hostname, $os, $arch, $client_id, $registered_at, $registered_at)
            ON CONFLICT(machine_id) DO NOTHING
            RETURNING machine_id;
        ";
        cmd.Parameters.AddWithValue("$machine_id", machineId);
        cmd.Parameters.AddWithValue("$hostname", hostname);
        cmd.Parameters.AddWithValue("$os", os);
        cmd.Parameters.AddWithValue("$arch", arch);
        cmd.Parameters.AddWithValue("$client_id", tokenHash);
        cmd.Parameters.AddWithValue("$registered_at", SqliteUtc.Format(now));

        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    public async Task<string?> HeartbeatAsync(string tokenHash, DateTimeOffset now, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE machines SET last_heartbeat = $now
            WHERE client_id = $client_id
            RETURNING machine_id;
        ";
        cmd.Parameters.AddWithValue("$now", SqliteUtc.Format(now));
        cmd.Parameters.AddWithValue("$client_id", tokenHash);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? reader.GetString(0) : null;
    }
}
