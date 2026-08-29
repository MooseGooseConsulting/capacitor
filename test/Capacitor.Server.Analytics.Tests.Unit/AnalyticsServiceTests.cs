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
        _projector = new SessionRollupProjector(_connection);
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
    public async Task SessionRollupProjector_orders_mixed_offset_timestamps_by_instant() {
        var sessionId = "sess-tz-1";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "dev-user");

        var earlierUtc = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var laterEastern = new DateTimeOffset(2024, 6, 1, 8, 0, 0, TimeSpan.FromHours(-5));

        await _eventStore.AppendEventsAsync([
            new() {
                SessionId = sessionId,
                LineNumber = 1,
                EventType = "UserMessage",
                Vendor = "claude",
                Timestamp = laterEastern
            },
            new() {
                SessionId = sessionId,
                LineNumber = 2,
                EventType = "AssistantMessage",
                Vendor = "claude",
                Timestamp = earlierUtc
            }
        ]);

        await _projector.ProjectSessionRollupAsync(sessionId);

        var session = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(session).IsNotNull();
        await Assert.That(session!.LastEventAt).IsEqualTo(laterEastern);
        await Assert.That(session.DurationMin).IsEqualTo(60m);
    }

    [Test]
    public async Task SessionRollupProjector_does_not_rewrite_status_or_title() {
        var sessionId = "sess-meta-1";
        var placeholder = await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "dev-user");
        await _sessions.UpdateSessionAsync(placeholder with {
            Status = "completed",
            Title = "Keep this title",
            Visibility = "project"
        });

        await _eventStore.AppendEventsAsync([
            new() {
                SessionId = sessionId,
                LineNumber = 1,
                EventType = "UserMessage",
                Vendor = "claude",
                Timestamp = DateTimeOffset.UtcNow
            }
        ]);
        await _projector.ProjectSessionRollupAsync(sessionId);

        var session = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(session).IsNotNull();
        await Assert.That(session!.Status).IsEqualTo("completed");
        await Assert.That(session.Title).IsEqualTo("Keep this title");
        await Assert.That(session.Visibility).IsEqualTo("project");
        await Assert.That(session.EventCount).IsEqualTo(1);
    }

    [Test]
    public async Task SessionRollupProjector_rejects_stale_lower_event_count() {
        var sessionId = "sess-stale-1";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "dev-user");
        await _eventStore.AppendEventsAsync([
            new() {
                SessionId = sessionId,
                LineNumber = 1,
                EventType = "UserMessage",
                Vendor = "claude",
                Timestamp = DateTimeOffset.UtcNow
            },
            new() {
                SessionId = sessionId,
                LineNumber = 2,
                EventType = "AssistantMessage",
                Vendor = "claude",
                Timestamp = DateTimeOffset.UtcNow
            }
        ]);
        await _projector.ProjectSessionRollupAsync(sessionId);

        using (var bump = _connection.CreateCommand()) {
            bump.CommandText = "UPDATE sessions SET event_count = 99 WHERE session_id = $id;";
            bump.Parameters.AddWithValue("$id", sessionId);
            await bump.ExecuteNonQueryAsync();
        }

        await _projector.ProjectSessionRollupAsync(sessionId);

        var session = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(session!.EventCount).IsEqualTo(99);
    }

    [Test]
    public async Task GovernedAnalytics_queries_views_successfully() {
        var sessionId = "sess-views-1";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "dev-user");

        var rows = await _analytics.ExecuteGovernedQueryAsync(
            "SELECT * FROM v_an_sessions WHERE session_id = 'sess-views-1';", scope: "global");
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0]["vendor"]).IsEqualTo("claude");
        await Assert.That(rows[0]["owner_user_id"]).IsEqualTo("dev-user");
    }

    [Test]
    public async Task GovernedAnalytics_rejects_raw_table_access() {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _analytics.ExecuteGovernedQueryAsync(
                "SELECT content, raw_payload FROM session_events;", scope: "global"));
    }

    [Test]
    public async Task GovernedAnalytics_rejects_comment_hidden_base_table() {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _analytics.ExecuteGovernedQueryAsync(
                "SELECT content FROM/**/session_events;", scope: "global"));
    }

    [Test]
    public async Task GovernedAnalytics_rejects_stacked_statements() {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _analytics.ExecuteGovernedQueryAsync(
                "SELECT * FROM v_an_sessions; SELECT content FROM session_events;", scope: "global"));
    }

    [Test]
    public async Task GovernedAnalytics_accepts_leading_with_clause() {
        var sessionId = "sess-with-1";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "dev-user");

        var rows = await _analytics.ExecuteGovernedQueryAsync(
            @"WITH mine AS (SELECT * FROM v_an_sessions WHERE session_id = 'sess-with-1')
              SELECT * FROM mine;",
            scope: "global");
        await Assert.That(rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GovernedAnalytics_scope_filters_out_other_repos() {
        var sessionId = "sess-scope-1";
        var placeholder = await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "dev-user");
        await _sessions.UpdateSessionAsync(placeholder with { RepoHash = "repo-a" });

        var sameRepo = await _analytics.ExecuteGovernedQueryAsync(
            "SELECT * FROM v_an_sessions WHERE session_id = 'sess-scope-1';", scope: "repo-a");
        await Assert.That(sameRepo.Count).IsEqualTo(1);

        var otherRepo = await _analytics.ExecuteGovernedQueryAsync(
            "SELECT * FROM v_an_sessions WHERE session_id = 'sess-scope-1';", scope: "repo-b");
        await Assert.That(otherRepo.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GovernedAnalytics_scopes_comma_joined_views() {
        var left = await _sessions.GetOrCreatePlaceholderAsync("sess-comma-a", "claude", "dev-user");
        await _sessions.UpdateSessionAsync(left with { RepoHash = "repo-a" });
        var right = await _sessions.GetOrCreatePlaceholderAsync("sess-comma-b", "claude", "dev-user");
        await _sessions.UpdateSessionAsync(right with { RepoHash = "repo-b" });

        var rows = await _analytics.ExecuteGovernedQueryAsync(
            "SELECT b.session_id AS sid FROM v_an_sessions a, v_an_sessions b;",
            scope: "repo-a");
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0]["sid"]).IsEqualTo("sess-comma-a");
    }

    [Test]
    public async Task GovernedAnalytics_caps_result_rows() {
        for (var i = 0; i < 5; i++) {
            await _sessions.GetOrCreatePlaceholderAsync($"sess-cap-{i}", "claude", "dev-user");
        }

        var rows = await _analytics.ExecuteGovernedQueryAsync(
            "SELECT * FROM v_an_sessions;", scope: "global", maxRows: 3);
        await Assert.That(rows.Count).IsEqualTo(3);
    }

    [Test]
    public async Task GovernedAnalytics_disambiguates_duplicate_column_labels() {
        await _sessions.GetOrCreatePlaceholderAsync("sess-dup-1", "claude", "dev-user");

        var rows = await _analytics.ExecuteGovernedQueryAsync(
            "SELECT vendor, vendor FROM v_an_sessions;", scope: "global");
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].ContainsKey("vendor")).IsTrue();
        await Assert.That(rows[0].ContainsKey("vendor_2")).IsTrue();
        await Assert.That(rows[0]["vendor"]).IsEqualTo("claude");
        await Assert.That(rows[0]["vendor_2"]).IsEqualTo("claude");
    }

    [Test]
    public async Task GovernedAnalytics_session_steps_expose_agent_identity() {
        var sessionId = "sess-steps-1";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "dev-user");
        await _eventStore.AppendEventsAsync([
            new() {
                SessionId = sessionId,
                AgentId = "root",
                LineNumber = 1,
                LogicalSeq = 10,
                EventType = "UserMessage",
                Vendor = "claude",
                Timestamp = DateTimeOffset.UtcNow
            },
            new() {
                SessionId = sessionId,
                AgentId = "sub-1",
                LineNumber = 1,
                LogicalSeq = 11,
                EventType = "ToolCall",
                Vendor = "claude",
                Timestamp = DateTimeOffset.UtcNow,
                ToolName = "bash"
            }
        ]);

        var rows = await _analytics.ExecuteGovernedQueryAsync(
            "SELECT agent_id, line_number, logical_seq FROM v_an_session_steps ORDER BY logical_seq;",
            scope: "global");
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0]["agent_id"]).IsEqualTo("root");
        await Assert.That(rows[0]["logical_seq"]).IsEqualTo(10L);
        await Assert.That(rows[1]["agent_id"]).IsEqualTo("sub-1");
        await Assert.That(rows[1]["line_number"]).IsEqualTo(1L);
    }

    [Test]
    public async Task GovernedAnalytics_prs_collapse_metadata_snapshots() {
        var older = await _sessions.GetOrCreatePlaceholderAsync("sess-pr-old", "claude", "dev-user");
        await _sessions.UpdateSessionAsync(older with {
            RepoHash = "repo-a",
            PrNumber = 13,
            PrTitle = "Old title",
            LastEventAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
        });
        var newer = await _sessions.GetOrCreatePlaceholderAsync("sess-pr-new", "claude", "dev-user");
        await _sessions.UpdateSessionAsync(newer with {
            RepoHash = "repo-a",
            PrNumber = 13,
            PrTitle = "New title",
            LastEventAt = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero)
        });

        var rows = await _analytics.ExecuteGovernedQueryAsync(
            "SELECT pr_number, pr_title, session_count FROM v_an_prs;",
            scope: "repo-a");
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0]["pr_number"]).IsEqualTo(13L);
        await Assert.That(rows[0]["pr_title"]).IsEqualTo("New title");
        await Assert.That(rows[0]["session_count"]).IsEqualTo(2L);
    }
}
