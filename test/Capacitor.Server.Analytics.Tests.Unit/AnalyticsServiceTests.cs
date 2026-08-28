using Microsoft.Data.Sqlite;
using Capacitor.Server.Data;
using Capacitor.Server.Data.Entities;
using Capacitor.Server.Ingest;

namespace Capacitor.Server.Analytics.Tests.Unit;

public sealed class AnalyticsServiceTests : IDisposable {
    private readonly SqliteConnection _connection;
    private readonly SqliteEventStoreRepository _eventStore;
    private readonly SqliteSessionRepository _sessions;
    private readonly SessionRollupProjector _projector;
    private readonly SqliteAnalyticsService _analytics;

    public AnalyticsServiceTests() {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        SqliteDatabaseInitializer.InitializeAsync(_connection).GetAwaiter().GetResult();

        _eventStore = new SqliteEventStoreRepository(_connection);
        _sessions = new SqliteSessionRepository(_connection);
        _projector = new SessionRollupProjector(_connection, _sessions);
        _analytics = new SqliteAnalyticsService(_connection);
    }

    public void Dispose() {
        _connection.Dispose();
    }

    [Test]
    public async Task SessionRollupProjector_computes_metrics_and_updates_header() {
        var sessionId = "70dc37b2b3b14f139c153858abbe88a8";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "dev-user");

        var now = DateTimeOffset.UtcNow;
        var events = new List<SessionEventRecord> {
            new() {
                SessionId = sessionId,
                LineNumber = 1,
                EventType = "UserMessage",
                Vendor = "claude",
                Timestamp = now.AddMinutes(-5),
                Content = "Run tests"
            },
            new() {
                SessionId = sessionId,
                LineNumber = 2,
                EventType = "ToolCall",
                Vendor = "claude",
                Model = "claude-3-5-sonnet",
                Timestamp = now,
                InputTokens = 2000,
                OutputTokens = 500,
                CostUsd = 0.009m,
                ToolName = "bash"
            }
        };

        await _eventStore.AppendEventsAsync(events);
        await _projector.ProjectSessionRollupAsync(sessionId);

        var session = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(session).IsNotNull();
        await Assert.That(session!.EventCount).IsEqualTo(2);
        await Assert.That(session.ToolCount).IsEqualTo(1);
        await Assert.That(session.TotalTokens).IsEqualTo(2500);
        await Assert.That(session.TotalCostUsd).IsEqualTo(0.009m);
        await Assert.That(session.DurationMin).IsGreaterThanOrEqualTo(4.9m);
    }

    [Test]
    public async Task GovernedAnalytics_queries_views_successfully() {
        var sessionId = "sess-views-1";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "dev-user");

        var result = await _analytics.ExecuteGovernedQueryAsync("SELECT * FROM v_an_sessions WHERE session_id = 'sess-views-1';");
        await Assert.That(result.Rows.Count).IsEqualTo(1);
        await Assert.That(result.Truncated).IsFalse();
        await Assert.That(result.Rows[0]["vendor"]).IsEqualTo("claude");
        await Assert.That(result.Rows[0]["owner_user_id"]).IsEqualTo("dev-user");
    }

    [Test]
    public async Task GovernedAnalytics_rejects_a_second_statement() {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _analytics.ExecuteGovernedQueryAsync("SELECT * FROM v_an_sessions; DROP TABLE sessions;"));
    }

    [Test]
    public async Task GovernedAnalytics_rejects_a_non_view_table() {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _analytics.ExecuteGovernedQueryAsync("SELECT * FROM sessions;"));
    }

    [Test]
    public async Task GovernedAnalytics_rejects_raw_table_access() {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _analytics.ExecuteGovernedQueryAsync("SELECT content, raw_payload FROM session_events;"));
    }

    [Test]
    public async Task GovernedAnalytics_accepts_leading_with_clause() {
        var sessionId = "sess-with-1";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "dev-user");

        var result = await _analytics.ExecuteGovernedQueryAsync(
            @"WITH mine AS (SELECT * FROM v_an_sessions WHERE session_id = 'sess-with-1')
              SELECT * FROM mine;");
        await Assert.That(result.Rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GovernedAnalytics_scopes_results_to_the_given_repos() {
        var inRepo = "sess-repo-a";
        var outOfRepo = "sess-repo-b";
        await _sessions.GetOrCreatePlaceholderAsync(inRepo, "claude", "dev-user");
        await _sessions.GetOrCreatePlaceholderAsync(outOfRepo, "claude", "dev-user");
        await _sessions.UpdateSessionAsync((await _sessions.GetSessionAsync(inRepo))! with { RepoHash = "repo-a" });
        await _sessions.UpdateSessionAsync((await _sessions.GetSessionAsync(outOfRepo))! with { RepoHash = "repo-b" });

        var result = await _analytics.ExecuteGovernedQueryAsync("SELECT * FROM v_an_sessions;", repos: ["repo-a"]);

        await Assert.That(result.Rows.Select(r => r["session_id"])).Contains(inRepo);
        await Assert.That(result.Rows.Select(r => r["session_id"])).DoesNotContain(outOfRepo);
    }

    [Test]
    public async Task GovernedAnalytics_reports_truncation_past_max_rows() {
        for (var i = 0; i < 3; i++) {
            await _sessions.GetOrCreatePlaceholderAsync($"sess-cap-{i}", "claude", "dev-user");
        }

        var result = await _analytics.ExecuteGovernedQueryAsync("SELECT * FROM v_an_sessions;", maxRows: 2);

        await Assert.That(result.Rows.Count).IsEqualTo(2);
        await Assert.That(result.Truncated).IsTrue();
        await Assert.That(result.MaxRows).IsEqualTo(2);
    }
}
