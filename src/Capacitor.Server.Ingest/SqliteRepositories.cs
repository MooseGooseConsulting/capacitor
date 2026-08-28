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
                last_line_number = MAX(session_watermarks.last_line_number, excluded.last_line_number),
                byte_offset = MAX(session_watermarks.byte_offset, excluded.byte_offset),
                updated_at = excluded.updated_at;
        ";

        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$agent_id", agentId ?? string.Empty);
        cmd.Parameters.AddWithValue("$last_line_number", lastLineNumber);
        cmd.Parameters.AddWithValue("$byte_offset", byteOffset);
        cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public class SqliteSessionRepository : ISessionRepository {
    private readonly SqliteConnection _connection;

    public SqliteSessionRepository(SqliteConnection connection) {
        _connection = connection;
    }

    public async Task<SessionHeaderRecord> GetOrCreatePlaceholderAsync(string sessionId, string vendor, string? ownerUserId = null, CancellationToken ct = default) {
        var existing = await GetSessionAsync(sessionId, ct);
        if (existing != null) return existing;

        var now = DateTimeOffset.UtcNow;
        var placeholder = new SessionHeaderRecord {
            SessionId = sessionId,
            Vendor = vendor,
            OwnerUserId = ownerUserId ?? "anonymous",
            StartedAt = now,
            Status = "active",
            Visibility = "private"
        };

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO sessions (
                session_id, vendor, owner_user_id, status, visibility, started_at
            ) VALUES (
                $session_id, $vendor, $owner_user_id, $status, $visibility, $started_at
            )
            ON CONFLICT(session_id) DO NOTHING;
        ";

        cmd.Parameters.AddWithValue("$session_id", placeholder.SessionId);
        cmd.Parameters.AddWithValue("$vendor", placeholder.Vendor);
        cmd.Parameters.AddWithValue("$owner_user_id", placeholder.OwnerUserId);
        cmd.Parameters.AddWithValue("$status", placeholder.Status);
        cmd.Parameters.AddWithValue("$visibility", placeholder.Visibility);
        cmd.Parameters.AddWithValue("$started_at", placeholder.StartedAt.ToString("o", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(ct);
        return placeholder;
    }

    public async Task<SessionHeaderRecord?> GetSessionAsync(string sessionId, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT session_id, title, slug, vendor, model, status, visibility, owner_user_id, machine_id, daemon_id,
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
            OwnerUserId = reader.GetString(7),
            MachineId = reader.IsDBNull(8) ? null : reader.GetString(8),
            DaemonId = reader.IsDBNull(9) ? null : reader.GetString(9),
            RepoHash = reader.IsDBNull(10) ? null : reader.GetString(10),
            RepoOwner = reader.IsDBNull(11) ? null : reader.GetString(11),
            RepoName = reader.IsDBNull(12) ? null : reader.GetString(12),
            Branch = reader.IsDBNull(13) ? null : reader.GetString(13),
            PrNumber = reader.IsDBNull(14) ? null : reader.GetInt32(14),
            PrTitle = reader.IsDBNull(15) ? null : reader.GetString(15),
            PrUrl = reader.IsDBNull(16) ? null : reader.GetString(16),
            PrHeadRef = reader.IsDBNull(17) ? null : reader.GetString(17),
            StartedAt = DateTimeOffset.Parse(reader.GetString(18), CultureInfo.InvariantCulture),
            EndedAt = reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture),
            LastEventAt = reader.IsDBNull(20) ? null : DateTimeOffset.Parse(reader.GetString(20), CultureInfo.InvariantCulture),
            DurationMin = reader.GetDecimal(21),
            EventCount = reader.GetInt32(22),
            ToolCount = reader.GetInt32(23),
            TotalTokens = reader.GetInt64(24),
            TotalCostUsd = reader.GetDecimal(25),
            PreviousSessionId = reader.IsDBNull(26) ? null : reader.GetString(26),
            NextSessionId = reader.IsDBNull(27) ? null : reader.GetString(27),
            PrimaryPhase = reader.IsDBNull(28) ? null : reader.GetString(28),
            SecondaryPhase = reader.IsDBNull(29) ? null : reader.GetString(29),
            ClassificationConfidence = reader.IsDBNull(30) ? null : reader.GetDecimal(30),
            ClassificationSource = reader.IsDBNull(31) ? null : reader.GetString(31)
        };
    }

    public async Task UpdateSessionAsync(SessionHeaderRecord session, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE sessions SET
                title = COALESCE($title, title),
                slug = COALESCE($slug, slug),
                model = COALESCE($model, model),
                status = $status,
                visibility = $visibility,
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
        cmd.Parameters.AddWithValue("$model", (object?)session.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", session.Status);
        cmd.Parameters.AddWithValue("$visibility", session.Visibility);
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
        cmd.Parameters.AddWithValue("$started_at", session.StartedAt.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$ended_at", session.EndedAt.HasValue ? session.EndedAt.Value.ToString("o", CultureInfo.InvariantCulture) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$last_event_at", session.LastEventAt.HasValue ? session.LastEventAt.Value.ToString("o", CultureInfo.InvariantCulture) : (object)DBNull.Value);
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
}
