using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Npgsql;
using Capacitor.Cli.Core;
using Capacitor.Server.Data;
using Capacitor.Server.Data.Entities;
using Capacitor.Server.Ingest;
using Capacitor.Server.Normalizers;
using Capacitor.Server.Analytics;
using Capacitor.Server.Api;

var builder = WebApplication.CreateBuilder(args);

var dbProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connString = builder.Configuration["Database:ConnectionString"] ?? "Data Source=capacitor.db";

if (string.Equals(dbProvider, "Postgres", StringComparison.OrdinalIgnoreCase)) {
    var dataSource = NpgsqlDataSource.Create(connString);
    builder.Services.AddSingleton(dataSource);
    builder.Services.AddSingleton<IEventStoreRepository, PostgresEventStoreRepository>();
    builder.Services.AddSingleton<ISessionWatermarkRepository, PostgresWatermarkRepository>();
    builder.Services.AddSingleton<ISessionRepository, PostgresSessionRepository>();
} else {
    var sqliteConn = new SqliteConnection(connString);
    sqliteConn.Open();
    await SqliteDatabaseInitializer.InitializeAsync(sqliteConn);

    builder.Services.AddSingleton(sqliteConn);
    // Every repository/service below shares this one connection; SqliteGate serializes access to
    // it so two concurrent requests never run commands on it at the same time (Microsoft.Data.Sqlite
    // connections aren't thread-safe). One singleton instance for the whole app — DI hands the same
    // gate to every repository, which is what makes the serialization apply across all of them.
    builder.Services.AddSingleton<SqliteGate>();
    builder.Services.AddSingleton<IEventStoreRepository, SqliteEventStoreRepository>();
    builder.Services.AddSingleton<ISessionWatermarkRepository, SqliteWatermarkRepository>();
    builder.Services.AddSingleton<ISessionRepository, SqliteSessionRepository>();
    builder.Services.AddSingleton<SqliteAnalyticsService>();
}

builder.Services.AddSingleton<NormalizerRouter>();
builder.Services.AddSingleton<SessionRollupProjector>();

var app = builder.Build();

// Health Check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", provider = dbProvider, time = DateTimeOffset.UtcNow }));

// Ingestion Hooks

async Task<IResult> HandleSessionStart(string vendor, ApiSessionStartPayload payload, ISessionRepository sessions) {
    var sessionId = payload.SessionId.Replace("-", "");
    var existed = await sessions.GetSessionAsync(sessionId) != null;
    var session = await sessions.GetOrCreatePlaceholderAsync(sessionId, vendor, payload.UserId, payload.DefaultVisibility);

    // A transcript batch can create an anonymous placeholder before session-start arrives;
    // GetOrCreatePlaceholderAsync's short-circuit on an existing row would otherwise drop the
    // real owner this hook carries on the floor.
    if (existed && payload.UserId is { Length: > 0 } ownerUserId && session.OwnerUserId != ownerUserId) {
        await sessions.UpdateSessionAsync(session with { OwnerUserId = ownerUserId });
    }

    return Results.Ok(new { status = "started", session_id = sessionId });
}

async Task<IResult> HandleSessionEnd(string vendor, ApiSessionEndPayload payload, ISessionRepository sessions, SessionRollupProjector projector) {
    var sessionId = payload.SessionId.Replace("-", "");
    var session = await sessions.GetSessionAsync(sessionId);
    // A missing session must not read as delivered — the client's spool retries a non-2xx but
    // treats 200 as final, so an out-of-order session-end would otherwise be lost for good.
    if (session == null) return Results.NotFound();

    var updated = session with {
        Status = "completed",
        EndedAt = payload.EndedAt ?? DateTimeOffset.UtcNow
    };
    await sessions.UpdateSessionAsync(updated);
    await projector.ProjectSessionRollupAsync(sessionId);

    return Results.Ok(new { status = "ended", session_id = sessionId });
}

app.MapPost("/hooks/session-start/{vendor}", (string vendor, [FromBody] ApiSessionStartPayload payload, ISessionRepository sessions)
    => HandleSessionStart(vendor, payload, sessions));
app.MapPost("/hooks/session-start", ([FromBody] ApiSessionStartPayload payload, ISessionRepository sessions)
    => HandleSessionStart("claude", payload, sessions));

app.MapPost("/hooks/session-end/{vendor}", (string vendor, [FromBody] ApiSessionEndPayload payload, ISessionRepository sessions, SessionRollupProjector projector)
    => HandleSessionEnd(vendor, payload, sessions, projector));
app.MapPost("/hooks/session-end", ([FromBody] ApiSessionEndPayload payload, ISessionRepository sessions, SessionRollupProjector projector)
    => HandleSessionEnd("claude", payload, sessions, projector));

// Fail-closed clients (SessionImporter, WatchCommand) must see a 2xx before streaming a
// subagent's content — this endpoint's whole job is to be that ACK. Unqualified, matching what
// every harness posts (reference/SURFACE.md §4).
app.MapPost("/hooks/subagent-start", async ([FromBody] JsonElement payload, ISessionRepository sessions) => {
    var sessionId = payload.Str("session_id")?.Replace("-", "");
    if (sessionId is { Length: > 0 }) await sessions.GetOrCreatePlaceholderAsync(sessionId, "claude");
    return Results.Ok(new { status = "started" });
});

app.MapPost("/hooks/subagent-stop", ([FromBody] JsonElement payload) => Results.Ok(new { status = "stopped" }));

app.MapPost("/hooks/transcript", async (
    [FromBody] TranscriptBatch batch,
    ISessionRepository sessions,
    ISessionWatermarkRepository watermarks,
    IEventStoreRepository eventStore,
    NormalizerRouter router,
    SessionRollupProjector projector) => {
    var sessionId = batch.SessionId.Replace("-", "");
    var vendor = batch.Vendor ?? "claude";

    // A short line_numbers array used to fall back to a per-batch `i + 1`, which can collide
    // with an explicit number elsewhere in the SAME batch — the event store's upsert then
    // silently drops whichever insert lost the race. Reject the request instead of guessing.
    if (batch.LineNumbers != null && batch.LineNumbers.Length != batch.Lines.Length) {
        return Results.BadRequest(new { detail = "line_numbers, when present, must have the same length as lines." });
    }

    var placeholder = await sessions.GetOrCreatePlaceholderAsync(sessionId, vendor);
    if (batch.Repository is { } repo) {
        await sessions.UpdateSessionAsync(placeholder with {
            RepoOwner  = repo.Owner,
            RepoName   = repo.RepoName,
            Branch     = repo.Branch,
            PrNumber   = repo.PrNumber,
            PrTitle    = repo.PrTitle,
            PrUrl      = repo.PrUrl,
            PrHeadRef  = repo.PrHeadRef
        });
    }

    var agentId = batch.AgentId ?? string.Empty;
    var events = new List<SessionEventRecord>();
    var highestLine = 0;
    var failedCount = 0;

    for (var i = 0; i < batch.Lines.Length; i++) {
        var lineNum = batch.LineNumbers != null ? batch.LineNumbers[i] : i + 1;
        if (lineNum > highestLine) highestLine = lineNum;

        var lineEvents = router.Normalize(vendor, sessionId, agentId, lineNum, batch.Lines[i], out var lineFailed);
        if (lineFailed) failedCount++;
        events.AddRange(lineEvents);
    }

    if (events.Count > 0) {
        await eventStore.AppendEventsAsync(events);
        await watermarks.UpdateWatermarkAsync(sessionId, agentId, highestLine);
        await projector.ProjectSessionRollupAsync(sessionId);
    }

    // Strict callers want a non-2xx signal when any line silently fell back to a bare content
    // record instead of throwing — otherwise a fail-closed importer proceeds over dropped data.
    if (batch.Strict && failedCount > 0) {
        return Results.UnprocessableEntity(new { status = "partial", count = events.Count, failed = failedCount, highest_line = highestLine });
    }

    return Results.Ok(new { status = "ingested", count = events.Count, failed = failedCount, highest_line = highestLine });
});

app.MapGet("/api/sessions/{id}/last-line", async (
    string id,
    [FromQuery] string? agentId,
    ISessionRepository sessions,
    ISessionWatermarkRepository watermarks) => {
    var sessionId = id.Replace("-", "");
    var session = await sessions.GetSessionAsync(sessionId);
    if (session == null) return Results.NotFound();

    var line = await watermarks.GetLastLineNumberAsync(sessionId, agentId ?? "");
    if (line is null) return Results.NoContent();

    return Results.Ok(new { session_id = sessionId, agent_id = agentId ?? "", last_line_number = line.Value });
});

app.MapGet("/watermarks", async (
    [FromQuery] string session_id,
    [FromQuery] string? agent_id,
    ISessionWatermarkRepository watermarks) => {
    var sessionId = session_id.Replace("-", "");
    var line = await watermarks.GetLastLineNumberAsync(sessionId, agent_id ?? "");
    return Results.Ok(new { session_id = sessionId, agent_id = agent_id ?? "", last_line_number = line });
});

// Fleet Node Enrollment & Heartbeat
app.MapPost("/api/machines/enroll", (
    [FromBody] MachineEnrollmentRequest request) => {
    var machineId = string.IsNullOrWhiteSpace(request.MachineId)
        ? Guid.NewGuid().ToString("N")
        : request.MachineId;
    var token = $"kcap_node_{Guid.NewGuid():N}";
    return Results.Ok(new {
        machine_id = machineId,
        hostname = request.Hostname,
        auth_token = token,
        enrolled_at = DateTimeOffset.UtcNow
    });
});

app.MapPost("/api/machines/heartbeat", (
    [FromBody] MachineHeartbeatRequest request) => {
    return Results.Ok(new {
        machine_id = request.MachineId,
        status = "healthy",
        acknowledged_at = DateTimeOffset.UtcNow
    });
});

// Evals & Catalog
app.MapGet("/api/eval/catalog", () => Results.Ok(EvalCatalogDefinition.GetCatalog()));

// The CLI/daemon fetch this FIRST (EvalService.RunAsync) and abort the whole run on anything
// but success, before /api/eval/catalog is ever reached — see EvalQuestionCatalogClient.
app.MapGet("/api/eval/questions", () => {
    var catalog = EvalCatalogDefinition.GetCatalog();
    var questions = catalog.Questions.Select(q => new EvalQuestionDto {
        Category   = q.Category,
        Id         = q.Id,
        Text       = q.QuestionText,
        Prompt     = q.Prompt,
        NeedsTools = q.NeedsTools,
        PromptVersion = q.PromptVersion,
        RawText    = q.QuestionText
    }).ToList();
    return Results.Ok(questions);
});

app.MapGet("/api/sessions/{id}/eval-context", async (
    string id,
    [FromQuery] bool? chain,
    [FromQuery] int? threshold,
    ISessionRepository sessions,
    IEventStoreRepository eventStore) => {
    var sessionId = id.Replace("-", "");
    var session = await sessions.GetSessionAsync(sessionId);
    if (session == null) return Results.NotFound();

    var sessionChain = new List<string> { sessionId };
    if (chain == true) {
        var cursor = session.PreviousSessionId;
        var guard = 0;
        var ancestors = new List<string>();
        while (cursor is { Length: > 0 } && guard++ < 50) {
            ancestors.Add(cursor);
            var ancestor = await sessions.GetSessionAsync(cursor);
            cursor = ancestor?.PreviousSessionId;
        }
        ancestors.Reverse();
        sessionChain = [.. ancestors, sessionId];
    }

    var events = await eventStore.GetEventsAsync(sessionId);
    var trace = events.Select(e => new {
        kind      = EvalContextKind(e.EventType),
        timestamp = e.Timestamp,
        text      = e.Content ?? e.ToolOutput,
        tool      = e.ToolName
    }).ToList();
    var toolResultsTotal = events.Count(e => e.EventType == "ToolResult");

    return Results.Ok(new {
        session_id    = sessionId,
        session_chain = sessionChain,
        trace         = trace,
        compaction    = new {
            threshold_bytes        = threshold ?? 2000,
            entries                = trace.Count,
            tool_results_total     = toolResultsTotal,
            tool_results_truncated = 0,
            bytes_saved            = 0L
        }
    });
});

// Analytics Views
app.MapGet("/api/analytics/schema", async (SqliteConnection conn, SqliteGate gate) => {
    var text = await gate.RunAsync(async () => {
        var sb = new StringBuilder();
        sb.AppendLine("# Capacitor analytics views");
        sb.AppendLine();
        sb.AppendLine("Only single-statement SELECT queries over these v_an_* views are permitted.");
        sb.AppendLine("Every view carries repo_hash — governed queries are scoped to the caller's repos.");
        sb.AppendLine();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'view' AND name LIKE 'v_an_%' ORDER BY name;";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            var name = reader.GetString(0);
            var viewSql = reader.IsDBNull(1) ? "" : reader.GetString(1);
            sb.Append("## ").AppendLine(name);
            sb.AppendLine("```sql");
            sb.AppendLine(viewSql.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("Example: SELECT vendor, model, total_cost_usd FROM v_an_token_usage_by_model;");
        return sb.ToString();
    });

    return Results.Ok(new { text, max_rows = SqliteAnalyticsService.DefaultMaxRows });
});

app.MapPost("/api/analytics/query", async (
    [FromBody] ApiAnalyticsQueryRequest request,
    SqliteAnalyticsService analytics,
    CancellationToken ct) => {
    try {
        var result = await analytics.ExecuteGovernedQueryAsync(request.Sql, request.Repos, request.MaxRows, ct);
        return Results.Ok(new { rows = result.Rows, truncated = result.Truncated, max_rows = result.MaxRows });
    } catch (InvalidOperationException ex) {
        return Results.Problem(statusCode: 400, detail: ex.Message);
    } catch (SqliteException ex) {
        return Results.Problem(statusCode: 400, detail: ex.Message);
    }
});

app.Run();

static string EvalContextKind(string eventType) => eventType switch {
    "UserMessage"   => "user_message",
    "AssistantTurn" => "assistant_turn",
    "ToolCall"      => "tool_call",
    "ToolResult"    => "tool_result",
    _               => "event"
};

namespace Capacitor.Server.Api {
    using System.Text.Json.Serialization;

    public record ApiAnalyticsQueryRequest {
        [JsonPropertyName("sql")]
        public required string Sql { get; init; }

        [JsonPropertyName("repos")]
        public string[]? Repos { get; init; }

        [JsonPropertyName("max_rows")]
        public int? MaxRows { get; init; }
    }

    public record ApiSessionStartPayload {
        [JsonPropertyName("session_id")]
        public required string SessionId { get; init; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; init; }

        [JsonPropertyName("default_visibility")]
        public string? DefaultVisibility { get; init; }
    }

    public record ApiSessionEndPayload {
        [JsonPropertyName("session_id")]
        public required string SessionId { get; init; }

        [JsonPropertyName("ended_at")]
        public DateTimeOffset? EndedAt { get; init; }
    }

    public record MachineEnrollmentRequest(string? MachineId, string Hostname, string Os, string Arch);
    public record MachineHeartbeatRequest(string MachineId);
}
