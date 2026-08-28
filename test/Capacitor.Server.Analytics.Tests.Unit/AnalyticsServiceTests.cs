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

        var rows = await _analytics.ExecuteGovernedQueryAsync("SELECT * FROM v_an_sessions WHERE session_id = 'sess-views-1';");
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0]["vendor"]).IsEqualTo("claude");
        await Assert.That(rows[0]["owner_user_id"]).IsEqualTo("dev-user");
    }
}
