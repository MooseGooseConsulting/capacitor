using Microsoft.Data.Sqlite;
using Capacitor.Server.Data;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Ingest.Tests.Unit;

public sealed class IngestRepositoryTests : IDisposable {
    private readonly SqliteConnection _connection;
    private readonly SqliteEventStoreRepository _eventStore;
    private readonly SqliteWatermarkRepository _watermarks;
    private readonly SqliteSessionRepository _sessions;
    private readonly TranscriptIngestEngine _ingest;

    public IngestRepositoryTests() {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        SqliteDatabaseInitializer.InitializeAsync(_connection).GetAwaiter().GetResult();

        _eventStore = new SqliteEventStoreRepository(_connection);
        _watermarks = new SqliteWatermarkRepository(_connection);
        _sessions = new SqliteSessionRepository(_connection);
        _ingest = new TranscriptIngestEngine(_eventStore, _watermarks, _sessions);
    }

    public void Dispose() {
        _connection.Dispose();
    }

    [Test]
    public async Task AppendEvents_is_strictly_idempotent_on_position() {
        var sessionId = "70dc37b2b3b14f139c153858abbe88a8";
        var events = new List<SessionEventRecord> {
            new() { SessionId = sessionId, LineNumber = 1, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, Content = "Hello" },
            new() { SessionId = sessionId, LineNumber = 2, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, Content = "World" },
            new() { SessionId = sessionId, LineNumber = 3, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, ToolName = "bash" }
        };

        var inserted1 = await _eventStore.AppendEventsAsync(events);
        await Assert.That(inserted1).IsEqualTo(3);

        var replay = new List<SessionEventRecord> {
            new() { SessionId = sessionId, LineNumber = 1, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, Content = "mutated" },
            new() { SessionId = sessionId, LineNumber = 2, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, Content = "World" },
            new() { SessionId = sessionId, LineNumber = 3, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, ToolName = "bash", ToolOutput = "should-not-land" }
        };
        var inserted2 = await _eventStore.AppendEventsAsync(replay);
        await Assert.That(inserted2).IsEqualTo(0);

        var totalCount = await _eventStore.GetEventCountAsync(sessionId);
        await Assert.That(totalCount).IsEqualTo(3);

        var stored = await _eventStore.GetEventsAsync(sessionId);
        await Assert.That(stored[0].Content).IsEqualTo("Hello");
        await Assert.That(stored[2].ToolOutput).IsNull();
    }

    [Test]
    public async Task Watermark_matches_last_line_per_session_and_agent() {
        var sessionId = "sess-watermark";
        var events = new List<SessionEventRecord> {
            new() { SessionId = sessionId, AgentId = "", LineNumber = 0, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, RawPayload = "line-0" },
            new() { SessionId = sessionId, AgentId = "", LineNumber = 1, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, RawPayload = "line-1" },
            new() { SessionId = sessionId, AgentId = "sub-1", LineNumber = 0, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, RawPayload = "child-0" },
            new() { SessionId = sessionId, AgentId = "sub-1", LineNumber = 4, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, RawPayload = "child-4" }
        };

        await _ingest.IngestAsync(events);

        var parentMark = await _watermarks.GetLastLineNumberAsync(sessionId, "");
        await Assert.That(parentMark).IsEqualTo(1);

        var childMark = await _watermarks.GetLastLineNumberAsync(sessionId, "sub-1");
        await Assert.That(childMark).IsEqualTo(0);

        await _ingest.IngestAsync([
            new() { SessionId = sessionId, AgentId = "sub-1", LineNumber = 1, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, RawPayload = "child-1" },
            new() { SessionId = sessionId, AgentId = "sub-1", LineNumber = 2, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, RawPayload = "child-2" },
            new() { SessionId = sessionId, AgentId = "sub-1", LineNumber = 3, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, RawPayload = "child-3" }
        ]);
        await Assert.That(await _watermarks.GetLastLineNumberAsync(sessionId, "sub-1")).IsEqualTo(4);

        await _ingest.IngestAsync(events);
        var parentAfterReplay = await _watermarks.GetLastLineNumberAsync(sessionId, "");
        await Assert.That(parentAfterReplay).IsEqualTo(1);
        await Assert.That(await _eventStore.GetEventCountAsync(sessionId)).IsEqualTo(7);
    }

    [Test]
    public async Task Watermark_advances_as_a_single_frontier() {
        var sessionId = "sess-100";
        await _watermarks.UpdateWatermarkAsync(sessionId, "", 10, 1024);
        var mark1 = await _watermarks.GetLastLineNumberAsync(sessionId);
        await Assert.That(mark1).IsEqualTo(10);

        await _watermarks.UpdateWatermarkAsync(sessionId, "", 5, 4096);
        var mark2 = await _watermarks.GetLastLineNumberAsync(sessionId);
        await Assert.That(mark2).IsEqualTo(10);

        using (var cmd = _connection.CreateCommand()) {
            cmd.CommandText = "SELECT byte_offset FROM session_watermarks WHERE session_id = $session_id AND agent_id = '';";
            cmd.Parameters.AddWithValue("$session_id", sessionId);
            var offset = Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            await Assert.That(offset).IsEqualTo(1024);
        }

        await _watermarks.UpdateWatermarkAsync(sessionId, "", 25, 4096);
        var mark3 = await _watermarks.GetLastLineNumberAsync(sessionId);
        await Assert.That(mark3).IsEqualTo(25);
    }

    [Test]
    public async Task GetLastLineNumber_returns_null_when_no_watermark_row_exists() {
        var mark = await _watermarks.GetLastLineNumberAsync("sess-never-ingested");
        await Assert.That(mark).IsNull();
    }

    [Test]
    public async Task GetLastLineNumber_distinguishes_line_zero_from_no_row() {
        var sessionId = "sess-line-zero";
        await _watermarks.UpdateWatermarkAsync(sessionId, "", 0, 0);
        var mark = await _watermarks.GetLastLineNumberAsync(sessionId);
        await Assert.That(mark).IsEqualTo(0);
    }

    [Test]
    public async Task Placeholder_session_is_created_on_demand() {
        var sessionId = "sess-orphan";
        var placeholder = await _sessions.GetOrCreatePlaceholderAsync(sessionId, "codex", "user-1");

        await Assert.That(placeholder.SessionId).IsEqualTo(sessionId);
        await Assert.That(placeholder.Vendor).IsEqualTo("codex");
        await Assert.That(placeholder.Status).IsEqualTo("active");
        await Assert.That(placeholder.Visibility).IsEqualTo("private");
        await Assert.That(placeholder.OwnerUserId).IsEqualTo("user-1");

        var retrieved = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(retrieved).IsNotNull();
        await Assert.That(retrieved!.SessionId).IsEqualTo(sessionId);
        await Assert.That(retrieved.Visibility).IsEqualTo("private");
    }

    [Test]
    public async Task Transcript_before_session_start_creates_placeholder_and_keeps_the_batch() {
        var sessionId = "sess-transcript-first";
        var events = new List<SessionEventRecord> {
            new() { SessionId = sessionId, LineNumber = 0, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, RawPayload = "{\"type\":\"user\"}" },
            new() { SessionId = sessionId, LineNumber = 1, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, RawPayload = "{\"type\":\"assistant\"}" }
        };

        var inserted = await _ingest.IngestAsync(events);
        await Assert.That(inserted).IsEqualTo(2);

        var placeholder = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(placeholder).IsNotNull();
        await Assert.That(placeholder!.Visibility).IsEqualTo("private");
        await Assert.That(placeholder.OwnerUserId).IsEqualTo("anonymous");
        await Assert.That(await _eventStore.GetEventCountAsync(sessionId)).IsEqualTo(2);
        await Assert.That(await _watermarks.GetLastLineNumberAsync(sessionId, "")).IsEqualTo(1);

        var realStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _sessions.UpdateSessionAsync(placeholder with {
            OwnerUserId = "real-user",
            Vendor = "claude",
            StartedAt = realStartedAt,
            Visibility = "private"
        });

        var retrieved = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(retrieved!.OwnerUserId).IsEqualTo("real-user");
        await Assert.That(retrieved.StartedAt.ToUniversalTime()).IsEqualTo(realStartedAt.ToUniversalTime());
        await Assert.That(await _eventStore.GetEventCountAsync(sessionId)).IsEqualTo(2);
        await Assert.That((await _eventStore.GetEventsAsync(sessionId))[0].RawPayload).IsEqualTo("{\"type\":\"user\"}");
    }

    [Test]
    public async Task GetEvents_default_fromLine_includes_line_zero() {
        var sessionId = "sess-line-zero-events";
        var events = new List<SessionEventRecord> {
            new() { SessionId = sessionId, LineNumber = 0, EventType = "Raw", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow }
        };
        await _eventStore.AppendEventsAsync(events);

        var retrieved = await _eventStore.GetEventsAsync(sessionId);
        await Assert.That(retrieved.Count).IsEqualTo(1);
        await Assert.That(retrieved[0].LineNumber).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateSession_writes_predecessor_next_session_id() {
        await _sessions.GetOrCreatePlaceholderAsync("sess-old", "claude", "user-1");
        await _sessions.GetOrCreatePlaceholderAsync("sess-new", "claude", "user-1");

        var newer = await _sessions.GetSessionAsync("sess-new");
        await _sessions.UpdateSessionAsync(newer! with { PreviousSessionId = "sess-old" });

        var older = await _sessions.GetSessionAsync("sess-old");
        var loaded = await _sessions.GetSessionAsync("sess-new");
        await Assert.That(loaded!.PreviousSessionId).IsEqualTo("sess-old");
        await Assert.That(older!.NextSessionId).IsEqualTo("sess-new");
    }

    [Test]
    public async Task GetOrCreatePlaceholder_returns_the_persisted_row_on_conflict() {
        var sessionId = "sess-race";
        var first = await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "owner-a");
        var second = await _sessions.GetOrCreatePlaceholderAsync(sessionId, "codex", "owner-b");

        await Assert.That(second.OwnerUserId).IsEqualTo(first.OwnerUserId);
        await Assert.That(second.Vendor).IsEqualTo("claude");
        await Assert.That(second.Visibility).IsEqualTo("private");
    }

    [Test]
    public async Task AppendEvents_maps_item_id_and_token_columns_when_present() {
        var sessionId = "sess-envelope-fields";
        await _eventStore.AppendEventsAsync([
            new() {
                SessionId = sessionId,
                LineNumber = 0,
                EventType = "Raw",
                Vendor = "codex",
                Timestamp = DateTimeOffset.UtcNow,
                ItemId = "item-42",
                ReasoningTokens = 128,
                ContextUsedTokens = 900,
                ContextWindowTokens = 128000
            },
            new() {
                SessionId = sessionId,
                LineNumber = 1,
                EventType = "Raw",
                Vendor = "codex",
                Timestamp = DateTimeOffset.UtcNow
            }
        ]);

        var stored = await _eventStore.GetEventsAsync(sessionId);
        await Assert.That(stored[0].ItemId).IsEqualTo("item-42");
        await Assert.That(stored[0].ReasoningTokens).IsEqualTo(128);
        await Assert.That(stored[0].ContextUsedTokens).IsEqualTo(900);
        await Assert.That(stored[0].ContextWindowTokens).IsEqualTo(128000);
        await Assert.That(stored[1].ItemId).IsNull();
        await Assert.That(stored[1].ReasoningTokens).IsNull();
        await Assert.That(stored[1].ContextUsedTokens).IsNull();
        await Assert.That(stored[1].ContextWindowTokens).IsNull();
    }

    [Test]
    public async Task UpdateRepositoryMetadata_does_not_rewrite_status() {
        var sessionId = "sess-repo-meta";
        var created = await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "user-1");
        await _sessions.UpdateSessionAsync(created with { Status = "completed" });

        await _sessions.UpdateRepositoryMetadataAsync(
            sessionId, "hash-a", "acme", "cap", "main", 14, "title", "https://example/pr/14", "head");

        var stored = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(stored!.Status).IsEqualTo("completed");
        await Assert.That(stored.RepoHash).IsEqualTo("hash-a");
        await Assert.That(stored.RepoOwner).IsEqualTo("acme");
        await Assert.That(stored.RepoName).IsEqualTo("cap");
    }

    [Test]
    public async Task PatchSessionStart_does_not_rewrite_status() {
        var sessionId = "sess-patch-start";
        var created = await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude");
        await _sessions.UpdateSessionAsync(created with { Status = "completed" });

        await _sessions.PatchSessionStartAsync(sessionId, "owner-z", "private");

        var stored = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(stored!.Status).IsEqualTo("completed");
        await Assert.That(stored.OwnerUserId).IsEqualTo("owner-z");
        await Assert.That(stored.Visibility).IsEqualTo("private");
    }

    [Test]
    public async Task GetOrCreatePlaceholder_honors_default_visibility_on_insert_only() {
        var first = await _sessions.GetOrCreatePlaceholderAsync("sess-vis", "claude", "user-1", "private");
        await Assert.That(first.Visibility).IsEqualTo("private");

        var second = await _sessions.GetOrCreatePlaceholderAsync("sess-vis", "claude", "user-1", "project");
        await Assert.That(second.Visibility).IsEqualTo("private");
    }

    [Test]
    public async Task PersistEvalRun_replaces_verdicts_for_the_same_run() {
        var sessionId = "sess-eval";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "user-1");

        var run = new EvalRunRecord {
            EvalRunId = "run-1",
            SessionId = sessionId,
            JudgeModel = "gpt",
            OverallScore = 80,
            Summary = "ok",
            EvaluatedAt = DateTimeOffset.UtcNow
        };

        await _sessions.PersistEvalRunAsync(run, [
            new() {
                EvalRunId = "run-1", Category = "safety", QuestionId = "a",
                Score = 1, Verdict = "pass", Finding = "one"
            },
            new() {
                EvalRunId = "run-1", Category = "quality", QuestionId = "b",
                Score = 1, Verdict = "pass", Finding = "two"
            }
        ]);

        await _sessions.PersistEvalRunAsync(run with { OverallScore = 90, Summary = "retry" }, [
            new() {
                EvalRunId = "run-1", Category = "safety", QuestionId = "a",
                Score = 2, Verdict = "pass", Finding = "only"
            }
        ]);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MAX(finding), (SELECT overall_score FROM eval_runs WHERE eval_run_id = 'run-1') FROM eval_verdicts WHERE eval_run_id = 'run-1';";
        using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        await Assert.That(reader.GetInt64(0)).IsEqualTo(1);
        await Assert.That(reader.GetString(1)).IsEqualTo("only");
        await Assert.That(reader.GetInt64(2)).IsEqualTo(90);
    }
}
