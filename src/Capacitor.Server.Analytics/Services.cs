using System.Globalization;
using System.Text.RegularExpressions;
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
    private const int DefaultMaxRows = 1000;

    // Every view that ExecuteGovernedQueryAsync is allowed to read from. Keep in
    // sync with 002_analytics_views.sql -- anything not in here (raw tables like
    // session_events, which carries transcript content and tool output) is rejected.
    private static readonly HashSet<string> GovernedViews = new(StringComparer.OrdinalIgnoreCase) {
        "v_an_sessions", "v_an_token_usage_by_model", "v_an_tool_usage",
        "v_an_eval_scores", "v_an_work_items", "v_an_cost", "v_an_session_steps",
        "v_an_prs", "v_an_repositories"
    };

    private static readonly Regex CteNamePattern = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\s+AS\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Captures every FROM/JOIN table reference plus its alias (if any), skipping
    // over keywords (WHERE, ON, GROUP, ...) that can legally follow a bare
    // reference so they are never mistaken for an alias.
    private static readonly Regex TableRefPattern = new(
        @"\b(?<kw>FROM|JOIN)\s+(?<view>[A-Za-z_][A-Za-z0-9_]*)(?:\s+(?:AS\s+)?(?<alias>(?!(?:WHERE|ON|GROUP|ORDER|LEFT|INNER|RIGHT|FULL|CROSS|JOIN|UNION|LIMIT|HAVING|SET|WHEN|FROM)\b)[A-Za-z_][A-Za-z0-9_]*))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly SqliteConnection _connection;

    public SqliteAnalyticsService(SqliteConnection connection) {
        _connection = connection;
    }

    public async Task<List<Dictionary<string, object?>>> ExecuteGovernedQueryAsync(
        string sql, string scope, int maxRows = DefaultMaxRows, CancellationToken ct = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var trimmed = sql.Trim();
        var trimmedUpper = trimmed.ToUpperInvariant();
        if (!trimmedUpper.StartsWith("SELECT", StringComparison.Ordinal) &&
            !trimmedUpper.StartsWith("WITH", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Governed analytics query must be a read-only SELECT statement.");
        }

        // CTE names shadow the view they read from -- exempt them from the
        // allowlist, but never rewrite them: the view underneath was already
        // scoped when it was matched inside the CTE body.
        var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in CteNamePattern.Matches(trimmed)) {
            cteNames.Add(m.Groups[1].Value);
        }

        var scopedSql = TableRefPattern.Replace(trimmed, m => {
            var view = m.Groups["view"].Value;
            if (!GovernedViews.Contains(view)) {
                if (cteNames.Contains(view)) return m.Value;
                throw new InvalidOperationException(
                    $"Governed analytics query referenced a non-governed table or view: {view}");
            }

            var alias = m.Groups["alias"].Success ? m.Groups["alias"].Value : view;
            return $"{m.Groups["kw"].Value} (SELECT * FROM {view} WHERE repo_hash = $scope OR $scope = 'global') {alias}";
        });

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
                row[name] = val;
            }
            results.Add(row);
        }

        return results;
    }
}
