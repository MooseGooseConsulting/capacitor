using System.Globalization;
using Microsoft.Data.Sqlite;
using Capacitor.Server.Ingest;

namespace Capacitor.Server.Analytics;

public class SessionRollupProjector {
    private readonly SqliteConnection _connection;
    private readonly ISessionRepository _sessionRepo;

    public SessionRollupProjector(SqliteConnection connection, ISessionRepository sessionRepo) {
        _connection = connection;
        _sessionRepo = sessionRepo;
    }

    public async Task ProjectSessionRollupAsync(string sessionId, CancellationToken ct = default) {
        var existing = await _sessionRepo.GetSessionAsync(sessionId, ct);
        if (existing == null) return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*) AS event_count,
                SUM(CASE WHEN tool_name IS NOT NULL THEN 1 ELSE 0 END) AS tool_count,
                SUM(input_tokens + output_tokens + cache_read_tokens + cache_write_tokens) AS total_tokens,
                SUM(cost_usd) AS total_cost_usd,
                MIN(timestamp) AS first_event,
                MAX(timestamp) AS last_event
            FROM session_events
            WHERE session_id = $session_id;";
        cmd.Parameters.AddWithValue("$session_id", sessionId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct)) {
            var eventCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var toolCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var totalTokens = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            var totalCost = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3);
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

            var updated = existing with {
                EventCount = eventCount,
                ToolCount = toolCount,
                TotalTokens = totalTokens,
                TotalCostUsd = totalCost,
                DurationMin = durationMin,
                LastEventAt = lastEventAt
            };

            await _sessionRepo.UpdateSessionAsync(updated, ct);
        }
    }
}

public class SqliteAnalyticsService {
    private readonly SqliteConnection _connection;

    public SqliteAnalyticsService(SqliteConnection connection) {
        _connection = connection;
    }

    public async Task<List<Dictionary<string, object?>>> ExecuteGovernedQueryAsync(string sql, CancellationToken ct = default) {
        var trimmed = sql.Trim().ToUpperInvariant();
        if (!trimmed.StartsWith("SELECT", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Governed analytics query must be a read-only SELECT statement.");
        }

        var results = new List<Dictionary<string, object?>>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) {
                var name = reader.GetName(i);
                var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                row[name] = val;
            }
            results.Add(row);
        }

        return results;
    }
}
