using System.Globalization;
using Npgsql;
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
                last_line_number = GREATEST(session_watermarks.last_line_number, EXCLUDED.last_line_number),
                byte_offset = GREATEST(session_watermarks.byte_offset, EXCLUDED.byte_offset),
                updated_at = EXCLUDED.updated_at;
        ";

        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(agentId ?? string.Empty);
        cmd.Parameters.AddWithValue(lastLineNumber);
        cmd.Parameters.AddWithValue(byteOffset);
        cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public class PostgresSessionRepository : ISessionRepository {
    private readonly NpgsqlDataSource _dataSource;

    public PostgresSessionRepository(NpgsqlDataSource dataSource) {
        _dataSource = dataSource;
    }

    public async Task<SessionHeaderRecord> GetOrCreatePlaceholderAsync(string sessionId, string vendor, string? ownerUserId = null, string? defaultVisibility = null, CancellationToken ct = default) {
        var existing = await GetSessionAsync(sessionId, ct);
        if (existing != null) return existing;

        var now = DateTimeOffset.UtcNow;
        var placeholder = new SessionHeaderRecord {
            SessionId = sessionId,
            Vendor = vendor,
            OwnerUserId = ownerUserId ?? "anonymous",
            StartedAt = now,
            Status = "active",
            Visibility = string.IsNullOrWhiteSpace(defaultVisibility) ? "project" : defaultVisibility
        };

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO sessions (
                session_id, vendor, owner_user_id, status, visibility, started_at
            ) VALUES (
                $1, $2, $3, $4, $5, $6
            )
            ON CONFLICT(session_id) DO NOTHING
            RETURNING session_id;
        ";

        cmd.Parameters.AddWithValue(placeholder.SessionId);
        cmd.Parameters.AddWithValue(placeholder.Vendor);
        cmd.Parameters.AddWithValue(placeholder.OwnerUserId);
        cmd.Parameters.AddWithValue(placeholder.Status);
        cmd.Parameters.AddWithValue(placeholder.Visibility);
        cmd.Parameters.AddWithValue(placeholder.StartedAt.ToString("o", CultureInfo.InvariantCulture));

        var wonRace = await cmd.ExecuteScalarAsync(ct) != null;
        if (wonRace) return placeholder;

        // Someone else's insert landed first: report the row that actually exists, not this local guess.
        return await GetSessionAsync(sessionId, ct) ?? placeholder;
    }

    public async Task<SessionHeaderRecord?> GetSessionAsync(string sessionId, CancellationToken ct = default) {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT session_id, title, slug, vendor, model, status, visibility, owner_user_id, machine_id, daemon_id,
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
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE sessions SET
                title = COALESCE($2, title),
                slug = COALESCE($3, slug),
                model = COALESCE($4, model),
                status = $5,
                visibility = $6,
                owner_user_id = COALESCE($7, owner_user_id),
                machine_id = COALESCE($8, machine_id),
                daemon_id = COALESCE($9, daemon_id),
                repo_hash = COALESCE($10, repo_hash),
                repo_owner = COALESCE($11, repo_owner),
                repo_name = COALESCE($12, repo_name),
                branch = COALESCE($13, branch),
                pr_number = COALESCE($14, pr_number),
                pr_title = COALESCE($15, pr_title),
                pr_url = COALESCE($16, pr_url),
                pr_head_ref = COALESCE($17, pr_head_ref),
                started_at = $18,
                ended_at = COALESCE($19, ended_at),
                last_event_at = COALESCE($20, last_event_at),
                duration_min = $21,
                event_count = $22,
                tool_count = $23,
                total_tokens = $24,
                total_cost_usd = $25,
                previous_session_id = $26,
                next_session_id = $27,
                primary_phase = $28,
                secondary_phase = $29,
                classification_confidence = $30,
                classification_source = $31
            WHERE session_id = $1;
        ";

        cmd.Parameters.AddWithValue(session.SessionId);
        cmd.Parameters.AddWithValue((object?)session.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)session.Slug ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)session.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue(session.Status);
        cmd.Parameters.AddWithValue(session.Visibility);
        cmd.Parameters.AddWithValue((object?)session.OwnerUserId ?? DBNull.Value);
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
        cmd.Parameters.AddWithValue(session.StartedAt.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue(session.EndedAt.HasValue ? session.EndedAt.Value.ToString("o", CultureInfo.InvariantCulture) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue(session.LastEventAt.HasValue ? session.LastEventAt.Value.ToString("o", CultureInfo.InvariantCulture) : (object)DBNull.Value);
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
                last_event_at = COALESCE($2, last_event_at),
                duration_min = $3,
                event_count = $4,
                tool_count = $5,
                total_tokens = $6,
                total_cost_usd = $7
            WHERE session_id = $1;
        ";

        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(lastEventAt.HasValue ? lastEventAt.Value.ToString("o", CultureInfo.InvariantCulture) : (object)DBNull.Value);
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
        cmd.Parameters.AddWithValue(now.ToString("o", CultureInfo.InvariantCulture));

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
        cmd.Parameters.AddWithValue(now.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue(tokenHash);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }
}
