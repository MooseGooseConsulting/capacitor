using System.Globalization;
using Microsoft.Data.Sqlite;
using Capacitor.Server.Data;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Ingest.Tests.Unit;

public sealed class IngestRepositoryTests : IDisposable {
    private readonly SqliteConnection _connection;
    private readonly SqliteEventStoreRepository _eventStore;
    private readonly SqliteWatermarkRepository _watermarks;
    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteMachineRepository _machines;

    public IngestRepositoryTests() {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        SqliteDatabaseInitializer.InitializeAsync(_connection).GetAwaiter().GetResult();

        _eventStore = new SqliteEventStoreRepository(_connection);
        _watermarks = new SqliteWatermarkRepository(_connection);
        _sessions = new SqliteSessionRepository(_connection);
        _machines = new SqliteMachineRepository(_connection);
    }

    public void Dispose() {
        _connection.Dispose();
    }

    [Test]
    public async Task AppendEvents_is_strictly_idempotent_on_position() {
        var sessionId = "70dc37b2b3b14f139c153858abbe88a8";
        var events = new List<SessionEventRecord> {
            new() { SessionId = sessionId, LineNumber = 1, EventType = "SessionStarted", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow },
            new() { SessionId = sessionId, LineNumber = 2, EventType = "UserMessage", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, Content = "Hello" },
            new() { SessionId = sessionId, LineNumber = 3, EventType = "ToolCall", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow, ToolName = "bash" }
        };

        // First append
        var inserted1 = await _eventStore.AppendEventsAsync(events);
        await Assert.That(inserted1).IsEqualTo(3);

        // Second append (exact duplicate batch)
        var inserted2 = await _eventStore.AppendEventsAsync(events);
        await Assert.That(inserted2).IsGreaterThanOrEqualTo(0);

        var totalCount = await _eventStore.GetEventCountAsync(sessionId);
        await Assert.That(totalCount).IsEqualTo(3);
    }

    [Test]
    public async Task AppendEvents_supports_mutable_tool_result_upsert() {
        var sessionId = "70dc37b2b3b14f139c153858abbe88a8";
        var initial = new List<SessionEventRecord> {
            new() {
                SessionId = sessionId,
                LineNumber = 1,
                EventType = "ToolCall",
                Vendor = "claude",
                Timestamp = DateTimeOffset.UtcNow,
                ToolName = "bash",
                ToolInput = "ls",
                ToolOutput = null,
                ToolExitCode = null
            }
        };

        await _eventStore.AppendEventsAsync(initial);

        // Update arriving later with completed execution result
        var updated = new List<SessionEventRecord> {
            new() {
                SessionId = sessionId,
                LineNumber = 1,
                EventType = "ToolCall",
                Vendor = "claude",
                Timestamp = DateTimeOffset.UtcNow,
                ToolName = "bash",
                ToolInput = "ls",
                ToolOutput = "file1.txt\nfile2.txt",
                ToolExitCode = 0
            }
        };

        await _eventStore.AppendEventsAsync(updated);

        var retrieved = await _eventStore.GetEventsAsync(sessionId);
        await Assert.That(retrieved.Count).IsEqualTo(1);
        await Assert.That(retrieved[0].ToolOutput).IsEqualTo("file1.txt\nfile2.txt");
        await Assert.That(retrieved[0].ToolExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Watermark_advances_monotonically() {
        var sessionId = "sess-100";
        await _watermarks.UpdateWatermarkAsync(sessionId, "", 10, 1024);
        var mark1 = await _watermarks.GetLastLineNumberAsync(sessionId);
        await Assert.That(mark1).IsEqualTo(10);

        // Regressive update does not lower watermark
        await _watermarks.UpdateWatermarkAsync(sessionId, "", 5, 512);
        var mark2 = await _watermarks.GetLastLineNumberAsync(sessionId);
        await Assert.That(mark2).IsEqualTo(10);

        // Higher update advances watermark
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
        await Assert.That(placeholder.Visibility).IsEqualTo("project");

        var retrieved = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(retrieved).IsNotNull();
        await Assert.That(retrieved!.SessionId).IsEqualTo(sessionId);
        await Assert.That(retrieved.Visibility).IsEqualTo("project");
    }

    [Test]
    public async Task UpdateSession_reconciles_placeholder_owner_and_start_time() {
        var sessionId = "sess-transcript-first";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude");

        var placeholder = await _sessions.GetSessionAsync(sessionId);
        var realStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var reconciled = placeholder! with {
            OwnerUserId = "real-user",
            StartedAt = realStartedAt
        };
        await _sessions.UpdateSessionAsync(reconciled);

        var retrieved = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(retrieved!.OwnerUserId).IsEqualTo("real-user");
        await Assert.That(retrieved.StartedAt).IsEqualTo(realStartedAt);
    }

    [Test]
    public async Task GetEvents_default_fromLine_includes_line_zero() {
        var sessionId = "sess-line-zero-events";
        var events = new List<SessionEventRecord> {
            new() { SessionId = sessionId, LineNumber = 0, EventType = "SessionStarted", Vendor = "claude", Timestamp = DateTimeOffset.UtcNow }
        };
        await _eventStore.AppendEventsAsync(events);

        var retrieved = await _eventStore.GetEventsAsync(sessionId);
        await Assert.That(retrieved.Count).IsEqualTo(1);
        await Assert.That(retrieved[0].LineNumber).IsEqualTo(0);
    }

    [Test]
    public async Task GetEvents_orders_same_line_by_agent_id() {
        var sessionId = "sess-two-agents";
        var now = DateTimeOffset.UtcNow;
        var events = new List<SessionEventRecord> {
            new() { SessionId = sessionId, AgentId = "beta", LineNumber = 1, EventType = "UserMessage", Vendor = "claude", Timestamp = now, Content = "b" },
            new() { SessionId = sessionId, AgentId = "alpha", LineNumber = 1, EventType = "UserMessage", Vendor = "claude", Timestamp = now, Content = "a" }
        };
        await _eventStore.AppendEventsAsync(events);

        var retrieved = await _eventStore.GetEventsAsync(sessionId);
        await Assert.That(retrieved.Count).IsEqualTo(2);
        await Assert.That(retrieved[0].AgentId).IsEqualTo("alpha");
        await Assert.That(retrieved[1].AgentId).IsEqualTo("beta");
    }

    [Test]
    public async Task Placeholder_honours_an_explicit_default_visibility() {
        var sessionId = "sess-private";
        var placeholder = await _sessions.GetOrCreatePlaceholderAsync(sessionId, "codex", "user-1", "owner");

        await Assert.That(placeholder.Visibility).IsEqualTo("owner");
    }

    [Test]
    public async Task Heartbeat_resolves_the_machine_that_owns_the_enrolled_token() {
        var now = DateTimeOffset.UtcNow;
        await _machines.EnrollAsync("mach-1", "hephastus", "linux", "x64", MachineTokenHasher.Hash("tok-1"), now);

        var resolved = await _machines.HeartbeatAsync(MachineTokenHasher.Hash("tok-1"), now.AddMinutes(1));

        await Assert.That(resolved).IsEqualTo("mach-1");
    }

    [Test]
    public async Task Enroll_rejects_an_already_enrolled_machine_id_without_replacing_its_token() {
        var now = DateTimeOffset.UtcNow;
        var first = await _machines.EnrollAsync("mach-1", "hephastus", "linux", "x64", MachineTokenHasher.Hash("tok-1"), now);
        var second = await _machines.EnrollAsync("mach-1", "impostor", "linux", "arm64", MachineTokenHasher.Hash("tok-2"), now.AddMinutes(1));

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(await _machines.HeartbeatAsync(MachineTokenHasher.Hash("tok-1"), now.AddMinutes(2))).IsEqualTo("mach-1");
        await Assert.That(await _machines.HeartbeatAsync(MachineTokenHasher.Hash("tok-2"), now.AddMinutes(2))).IsNull();
    }

    [Test]
    public async Task UpdateRollup_does_not_regress_when_a_stale_projection_writes_later() {
        var sessionId = "sess-rollup-monotonic";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "user-1");
        await _sessions.UpdateRollupAsync(sessionId, 5, 2, 1000, 0.5m, 12m, DateTimeOffset.UtcNow);

        await _sessions.UpdateRollupAsync(sessionId, 1, 0, 10, 0.01m, 1m, DateTimeOffset.UtcNow.AddMinutes(-10));

        var retrieved = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(retrieved!.EventCount).IsEqualTo(5);
        await Assert.That(retrieved.ToolCount).IsEqualTo(2);
        await Assert.That(retrieved.TotalTokens).IsEqualTo(1000);
        await Assert.That(retrieved.TotalCostUsd).IsEqualTo(0.5m);
        await Assert.That(retrieved.DurationMin).IsEqualTo(12m);
    }

    [Test]
    public async Task Heartbeat_rejects_a_token_no_enrollment_issued() {
        var resolved = await _machines.HeartbeatAsync(MachineTokenHasher.Hash("never-enrolled"), DateTimeOffset.UtcNow);

        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task UpdateRepositoryMetadata_does_not_reset_completed_status() {
        var sessionId = "sess-repo-race";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude");
        var completed = (await _sessions.GetSessionAsync(sessionId))! with {
            Status = "completed",
            EndedAt = DateTimeOffset.UtcNow
        };
        await _sessions.UpdateSessionAsync(completed);

        await _sessions.UpdateRepositoryMetadataAsync(
            sessionId, "abc123def4567890", "acme", "widget", "main",
            14, "title", "https://example.test/pr/14", "head");

        var retrieved = await _sessions.GetSessionAsync(sessionId);
        await Assert.That(retrieved!.Status).IsEqualTo("completed");
        await Assert.That(retrieved.RepoHash).IsEqualTo("abc123def4567890");
        await Assert.That(retrieved.RepoOwner).IsEqualTo("acme");
        await Assert.That(retrieved.RepoName).IsEqualTo("widget");
        await Assert.That(retrieved.EndedAt).IsEqualTo(completed.EndedAt);
    }

    [Test]
    public async Task AppendEventsAndAdvanceWatermark_advances_watermark_for_empty_batches() {
        var sessionId = "sess-empty-accepted";
        await _eventStore.AppendEventsAndAdvanceWatermarkAsync([], sessionId, "", 4);

        var line = await _watermarks.GetLastLineNumberAsync(sessionId, "");
        await Assert.That(line).IsEqualTo(4);
        await Assert.That(await _eventStore.GetEventCountAsync(sessionId)).IsEqualTo(0);
    }

    [Test]
    public async Task PersistEvalRun_writes_run_and_verdicts() {
        var sessionId = "sess-eval-1";
        await _sessions.GetOrCreatePlaceholderAsync(sessionId, "claude");
        var run = new EvalRunRecord {
            EvalRunId = "run-1",
            SessionId = sessionId,
            JudgeModel = "test-model",
            OverallScore = 80,
            Summary = "ok",
            EvaluatedAt = DateTimeOffset.UtcNow
        };
        var verdicts = new List<EvalVerdictRecord> {
            new() {
                EvalRunId = "run-1",
                Category = "safety",
                QuestionId = "destructive_commands",
                Score = 80,
                Verdict = "pass",
                Finding = "none"
            }
        };

        await _sessions.PersistEvalRunAsync(run, verdicts);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM eval_runs WHERE eval_run_id = 'run-1';";
        await Assert.That(Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture)).IsEqualTo(1);
        cmd.CommandText = "SELECT COUNT(*) FROM eval_verdicts WHERE eval_run_id = 'run-1';";
        await Assert.That(Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture)).IsEqualTo(1);
    }
}
