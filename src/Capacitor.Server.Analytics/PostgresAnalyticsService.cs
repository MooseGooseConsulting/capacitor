using Npgsql;

namespace Capacitor.Server.Analytics;

public sealed class PostgresAnalyticsService {
    public const int DefaultMaxRows = 1000;

    private static readonly HashSet<string> GovernedViews = new(StringComparer.OrdinalIgnoreCase) {
        "v_an_sessions", "v_an_token_usage_by_model", "v_an_tool_usage",
        "v_an_eval_scores", "v_an_work_items", "v_an_cost", "v_an_session_steps",
        "v_an_prs", "v_an_repositories"
    };

    private readonly NpgsqlDataSource _dataSource;

    public PostgresAnalyticsService(NpgsqlDataSource dataSource) {
        _dataSource = dataSource;
    }

    public async Task<List<Dictionary<string, object?>>> ExecuteGovernedQueryAsync(
        string sql, string scope, int maxRows = DefaultMaxRows, CancellationToken ct = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var scopedSql = GovernedSql.Rewrite(sql, GovernedViews);
        var results = new List<Dictionary<string, object?>>();

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = scopedSql;
        command.Parameters.AddWithValue("scope", scope);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (results.Count < maxRows && await reader.ReadAsync(ct)) {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) {
                var name = reader.GetName(i);
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                if (!row.TryAdd(name, value)) {
                    var duplicate = 2;
                    while (!row.TryAdd($"{name}_{duplicate}", value)) {
                        duplicate++;
                    }
                }
            }

            results.Add(row);
        }

        return results;
    }

    public async Task<IReadOnlyList<(string Name, string Definition)>> GetViewDefinitionsAsync(CancellationToken ct = default) {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT viewname, definition
            FROM pg_views
            WHERE schemaname = current_schema()
              AND viewname LIKE 'v_an_%'
            ORDER BY viewname;";

        var views = new List<(string Name, string Definition)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            views.Add((reader.GetString(0), reader.GetString(1)));
        }

        return views;
    }
}
