using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Capacitor.Server.Analytics;

public class SessionRollupProjector {
    private readonly SqliteConnection _connection;

    public SessionRollupProjector(SqliteConnection connection) {
        _connection = connection;
    }

    public async Task ProjectSessionRollupAsync(string sessionId, CancellationToken ct = default) {
        using var tx = _connection.BeginTransaction(IsolationLevel.Serializable);

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT timestamp, tool_name,
                   input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
                   cost_usd
            FROM session_events
            WHERE session_id = $session_id;";
        cmd.Parameters.AddWithValue("$session_id", sessionId);

        long eventCount = 0;
        long toolCount = 0;
        long totalTokens = 0;
        decimal totalCost = 0m;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;

        using (var reader = await cmd.ExecuteReaderAsync(ct)) {
            while (await reader.ReadAsync(ct)) {
                eventCount++;
                if (!reader.IsDBNull(1)) {
                    toolCount++;
                }

                totalTokens += reader.GetInt64(2) + reader.GetInt64(3)
                    + reader.GetInt64(4) + reader.GetInt64(5);
                totalCost += reader.GetDecimal(6);

                var ts = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
                if (first is null || ts < first) {
                    first = ts;
                }

                if (last is null || ts > last) {
                    last = ts;
                }
            }
        }

        decimal durationMin = 0m;
        if (first is not null && last is not null) {
            durationMin = (decimal)Math.Round((last.Value - first.Value).TotalMinutes, 2);
        }

        using var update = _connection.CreateCommand();
        update.Transaction = tx;
        // Only rollup columns, and only if this snapshot is at least as complete
        // as whatever already landed — a delayed projector must not clobber a
        // newer aggregate or rewrite status/title/visibility.
        update.CommandText = @"
            UPDATE sessions SET
                event_count = $event_count,
                tool_count = $tool_count,
                total_tokens = $total_tokens,
                total_cost_usd = $total_cost_usd,
                duration_min = $duration_min,
                last_event_at = $last_event_at
            WHERE session_id = $session_id
              AND event_count <= $event_count;";
        update.Parameters.AddWithValue("$session_id", sessionId);
        update.Parameters.AddWithValue("$event_count", SaturateToInt(eventCount));
        update.Parameters.AddWithValue("$tool_count", SaturateToInt(toolCount));
        update.Parameters.AddWithValue("$total_tokens", totalTokens);
        update.Parameters.AddWithValue("$total_cost_usd", totalCost);
        update.Parameters.AddWithValue("$duration_min", durationMin);
        update.Parameters.AddWithValue(
            "$last_event_at",
            last is null ? DBNull.Value : last.Value.ToString("o", CultureInfo.InvariantCulture));

        await update.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }

    private static int SaturateToInt(long value) =>
        value > int.MaxValue ? int.MaxValue : (int)value;
}

public class SqliteAnalyticsService {
    private const int DefaultMaxRows = 1000;

    // Every view that ExecuteGovernedQueryAsync is allowed to read from. Keep in
    // sync with 002_analytics_views.sql -- anything not in here (raw tables like
    // session_events, which carries transcript content and tool output) is rejected.
    private static readonly HashSet<string> GovernedViews = new(StringComparer.OrdinalIgnoreCase) {
        "v_an_sessions", "v_an_token_usage_by_model", "v_an_tool_usage",
        "v_an_eval_scores", "v_an_work_items", "v_an_cost", "v_an_session_steps",
        "v_an_prs", "v_an_repositories"
    };

    private readonly SqliteConnection _connection;

    public SqliteAnalyticsService(SqliteConnection connection) {
        _connection = connection;
    }

    public async Task<List<Dictionary<string, object?>>> ExecuteGovernedQueryAsync(
        string sql, string scope, int maxRows = DefaultMaxRows, CancellationToken ct = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var scopedSql = GovernedSql.Rewrite(sql, GovernedViews);

        var results = new List<Dictionary<string, object?>>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = scopedSql;
        cmd.Parameters.AddWithValue("$scope", scope);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (results.Count < maxRows && await reader.ReadAsync(ct)) {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) {
                var name = reader.GetName(i);
                var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                if (!row.TryAdd(name, val)) {
                    var n = 2;
                    while (!row.TryAdd($"{name}_{n}", val)) {
                        n++;
                    }
                }
            }

            results.Add(row);
        }

        return results;
    }
}
