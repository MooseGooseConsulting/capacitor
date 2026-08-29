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
    public async Task Heartbeat_rejects_a_token_no_enrollment_issued() {
        var resolved = await _machines.HeartbeatAsync(MachineTokenHasher.Hash("never-enrolled"), DateTimeOffset.UtcNow);

        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task SearchSessions_matches_title_and_repo_and_honours_limit() {
        var first = await _sessions.GetOrCreatePlaceholderAsync("sess-search-a", "claude", "alice");
        await _sessions.UpdateSessionAsync(first with { Title = "refactor the ingest path", RepoOwner = "acme", RepoName = "cap" });
        var second = await _sessions.GetOrCreatePlaceholderAsync("sess-search-b", "codex", "bob");
        await _sessions.UpdateSessionAsync(second with { Title = "unrelated docs", RepoOwner = "acme", RepoName = "other" });

        var hits = await _sessions.SearchSessionsAsync("refactor", null, "acme/cap", 10, 0);
        await Assert.That(hits.Count).IsEqualTo(1);
        await Assert.That(hits[0].SessionId).IsEqualTo("sess-search-a");

        var limited = await _sessions.SearchSessionsAsync(null, null, null, 1, 0);
        await Assert.That(limited.Count).IsEqualTo(1);
    }
}
