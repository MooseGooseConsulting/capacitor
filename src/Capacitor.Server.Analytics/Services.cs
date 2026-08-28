using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Capacitor.Server.Ingest;

namespace Capacitor.Server.Analytics;

/// <summary>Aggregates session_events through the repository abstraction so one implementation serves every provider.</summary>
public class SessionRollupProjector {
    private readonly IEventStoreRepository _eventStore;
    private readonly ISessionRepository _sessionRepo;

    public SessionRollupProjector(IEventStoreRepository eventStore, ISessionRepository sessionRepo) {
        _eventStore = eventStore;
        _sessionRepo = sessionRepo;
    }

    public async Task ProjectSessionRollupAsync(string sessionId, CancellationToken ct = default) {
        var existing = await _sessionRepo.GetSessionAsync(sessionId, ct);
        if (existing == null) return;

        var events = await _eventStore.GetEventsAsync(sessionId, ct: ct);
        if (events.Count == 0) return;

        var eventCount = events.Count;
        var toolCount = 0;
        long totalTokens = 0;
        decimal totalCost = 0m;
        var first = events[0].Timestamp;
        var last = events[0].Timestamp;

        foreach (var ev in events) {
            if (ev.ToolName != null) toolCount++;
            totalTokens += ev.InputTokens + ev.OutputTokens + ev.CacheReadTokens + ev.CacheWriteTokens;
            totalCost += ev.CostUsd;
            if (ev.Timestamp < first) first = ev.Timestamp;
            if (ev.Timestamp > last) last = ev.Timestamp;
        }

        // Rollup-only write: never touches status/ended_at, so a concurrent session-end can't
        // be clobbered by a rollup projection racing it on the read-modify-write.
        await _sessionRepo.UpdateRollupAsync(
            sessionId,
            eventCount,
            toolCount,
            totalTokens,
            totalCost,
            (decimal)Math.Round((last - first).TotalMinutes, 2),
            last,
            ct);
    }
}

public readonly record struct GovernedQueryResult(List<Dictionary<string, object?>> Rows, bool Truncated, int MaxRows);

public class SqliteAnalyticsService {
    public const int DefaultMaxRows = 1000;
    public const int HardMaxRows = 5000;

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
    private readonly SqliteGate _gate;

    public SqliteAnalyticsService(SqliteConnection connection, SqliteGate? gate = null) {
        _connection = connection;
        _gate = gate ?? new SqliteGate();
    }

    public Task<GovernedQueryResult> ExecuteGovernedQueryAsync(
            string sql,
            IReadOnlyList<string>? repos = null,
            int? maxRows = null,
            CancellationToken ct = default
        ) {
        var scopedSql = ValidateAndScopeQuery(sql, repos, out var repoParams);
        var effectiveMaxRows = Math.Clamp(maxRows ?? DefaultMaxRows, 1, HardMaxRows);

        return _gate.RunAsync(async () => {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM ({scopedSql}) AS governed_query LIMIT $max_rows;";
            foreach (var (name, value) in repoParams) cmd.Parameters.AddWithValue(name, value);
            // Ask for one extra row so a full page can be told apart from a truncated one
            // without a separate COUNT(*) query.
            cmd.Parameters.AddWithValue("$max_rows", effectiveMaxRows + 1);

            var results = new List<Dictionary<string, object?>>();
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

            var truncated = results.Count > effectiveMaxRows;
            if (truncated) results.RemoveAt(results.Count - 1);

            return new GovernedQueryResult(results, truncated, effectiveMaxRows);
        }, ct);
    }

    // Rejects a second ;-separated statement (Microsoft.Data.Sqlite runs every statement in
    // CommandText, so "SELECT 1; DROP TABLE sessions;" would otherwise reach the connection),
    // then rewrites every FROM/JOIN'd view into a repo-scoped subquery — scoping happens
    // before any GROUP BY collapses repo_hash out of the caller's own projection, which an
    // outer WHERE on the finished result set could not do for an aggregation query.
    private static string ValidateAndScopeQuery(string sql, IReadOnlyList<string>? repos, out List<(string Name, object Value)> repoParams) {
        var trimmed = sql.Trim();
        var body = trimmed.EndsWith(';') ? trimmed[..^1].TrimEnd() : trimmed;

        if (body.Contains(';', StringComparison.Ordinal)) {
            throw new InvalidOperationException("Governed analytics query must be a single statement.");
        }
        var bodyUpper = body.ToUpperInvariant();
        if (!bodyUpper.StartsWith("SELECT", StringComparison.Ordinal) &&
            !bodyUpper.StartsWith("WITH", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Governed analytics query must be a read-only SELECT statement.");
        }

        // CTE names shadow the view they read from -- exempt them from the
        // allowlist, but never rewrite them: the view underneath was already
        // scoped when it was matched inside the CTE body.
        var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in CteNamePattern.Matches(body)) {
            cteNames.Add(m.Groups[1].Value);
        }

        var paramNames = repos is { Count: > 0 }
            ? Enumerable.Range(0, repos.Count).Select(i => $"$repo{i}").ToList()
            : [];
        var repoFilter = paramNames.Count > 0 ? $"repo_hash IN ({string.Join(", ", paramNames)})" : "1 = 1";

        var scopedSql = TableRefPattern.Replace(body, m => {
            var view = m.Groups["view"].Value;
            if (!GovernedViews.Contains(view)) {
                if (cteNames.Contains(view)) return m.Value;
                throw new InvalidOperationException(
                    $"Governed analytics query referenced a non-governed table or view: {view}");
            }

            var alias = m.Groups["alias"].Success ? m.Groups["alias"].Value : view;
            return $"{m.Groups["kw"].Value} (SELECT * FROM {view} WHERE {repoFilter}) {alias}";
        });

        repoParams = repos is { Count: > 0 }
            ? paramNames.Select((name, i) => (name, (object)repos[i])).ToList()
            : [];
        return scopedSql;
    }
}
