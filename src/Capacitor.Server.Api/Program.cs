using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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

var isPostgres = string.Equals(dbProvider, "Postgres", StringComparison.OrdinalIgnoreCase);

if (isPostgres) {
    var dataSource = NpgsqlDataSource.Create(connString);
    await using (var initConn = await dataSource.OpenConnectionAsync()) {
        await PostgresDatabaseInitializer.InitializeAsync(initConn);
    }

    builder.Services.AddSingleton(dataSource);
    builder.Services.AddSingleton<IEventStoreRepository, PostgresEventStoreRepository>();
    builder.Services.AddSingleton<ISessionWatermarkRepository, PostgresWatermarkRepository>();
    builder.Services.AddSingleton<ISessionRepository, PostgresSessionRepository>();
    builder.Services.AddSingleton<IMachineRepository, PostgresMachineRepository>();
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
    builder.Services.AddSingleton<IMachineRepository, SqliteMachineRepository>();
    builder.Services.AddSingleton<SqliteAnalyticsService>();
}

builder.Services.AddSingleton<NormalizerRouter>();
builder.Services.AddSingleton<SessionRollupProjector>();
builder.Services.AddSignalR();

var app = builder.Build();

// Real-Time SignalR Hub
app.MapHub<CapacitorHub>("/hub/capacitor");

// Health Check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", provider = dbProvider, time = DateTimeOffset.UtcNow }));

// Ingestion Hooks

async Task<IResult> HandleSessionStart(string vendor, ApiSessionStartPayload payload, ISessionRepository sessions, IHubContext<CapacitorHub, ICapacitorHubClient> hub) {
    var sessionId = payload.SessionId.Replace("-", "");
    var existed = await sessions.GetSessionAsync(sessionId) != null;
    var session = await sessions.GetOrCreatePlaceholderAsync(sessionId, vendor, payload.UserId, payload.DefaultVisibility);

    // A transcript batch can create an anonymous placeholder before session-start arrives;
    // GetOrCreatePlaceholderAsync's short-circuit on an existing row would otherwise drop the
    // real owner this hook carries on the floor.
    if (existed && payload.UserId is { Length: > 0 } ownerUserId && session.OwnerUserId != ownerUserId) {
        await sessions.UpdateSessionAsync(session with { OwnerUserId = ownerUserId });
    }

    await hub.Clients.Group($"session_{sessionId}").OnSessionStarted(sessionId, vendor);
    return Results.Ok(new { status = "started", session_id = sessionId });
}

async Task<IResult> HandleSessionEnd(string vendor, ApiSessionEndPayload payload, ISessionRepository sessions, SessionRollupProjector projector, IHubContext<CapacitorHub, ICapacitorHubClient> hub) {
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
    await hub.Clients.Group($"session_{sessionId}").OnSessionEnded(sessionId);

    return Results.Ok(new { status = "ended", session_id = sessionId });
}

app.MapPost("/hooks/session-start/{vendor}", (string vendor, [FromBody] ApiSessionStartPayload payload, ISessionRepository sessions, IHubContext<CapacitorHub, ICapacitorHubClient> hub)
    => HandleSessionStart(vendor, payload, sessions, hub));
app.MapPost("/hooks/session-start", ([FromBody] ApiSessionStartPayload payload, ISessionRepository sessions, IHubContext<CapacitorHub, ICapacitorHubClient> hub)
    => HandleSessionStart("claude", payload, sessions, hub));

app.MapPost("/hooks/session-end/{vendor}", (string vendor, [FromBody] ApiSessionEndPayload payload, ISessionRepository sessions, SessionRollupProjector projector, IHubContext<CapacitorHub, ICapacitorHubClient> hub)
    => HandleSessionEnd(vendor, payload, sessions, projector, hub));
app.MapPost("/hooks/session-end", ([FromBody] ApiSessionEndPayload payload, ISessionRepository sessions, SessionRollupProjector projector, IHubContext<CapacitorHub, ICapacitorHubClient> hub)
    => HandleSessionEnd("claude", payload, sessions, projector, hub));

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
    SessionRollupProjector projector,
    IHubContext<CapacitorHub, ICapacitorHubClient> hub) => {
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

        // Real-time broadcast to connected SignalR subscribers
        foreach (var ev in events) {
            await hub.Clients.Group($"session_{sessionId}").OnEventAppended(ev);
        }
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
app.MapPost("/api/machines/enroll", async (
    [FromBody] MachineEnrollmentRequest request,
    IMachineRepository machines) => {
    var machineId = string.IsNullOrWhiteSpace(request.MachineId)
        ? Guid.NewGuid().ToString("N")
        : request.MachineId;
    var token = $"kcap_node_{Guid.NewGuid():N}";
    var now = DateTimeOffset.UtcNow;
    await machines.EnrollAsync(machineId, request.Hostname, request.Os, request.Arch, MachineTokenHasher.Hash(token), now);
    return Results.Ok(new {
        machine_id = machineId,
        hostname = request.Hostname,
        auth_token = token,
        enrolled_at = now
    });
});

app.MapPost("/api/machines/heartbeat", async (
    HttpRequest request,
    IMachineRepository machines) => {
    var header = request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();

    var token = header["Bearer ".Length..].Trim();
    var machineId = await machines.HeartbeatAsync(MachineTokenHasher.Hash(token), DateTimeOffset.UtcNow);
    if (machineId == null) return Results.Unauthorized();

    return Results.Ok(new {
        machine_id = machineId,
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

// Analytics Views — SqliteAnalyticsService reads the v_an_* views directly off the Sqlite
// connection, which the Postgres provider never opens.
if (isPostgres) {
    app.MapGet("/api/analytics/schema", () => Results.Problem("Analytics views are not yet available under Database:Provider=Postgres.", statusCode: 501));
    app.MapPost("/api/analytics/query", () => Results.Problem("Analytics queries are not yet available under Database:Provider=Postgres.", statusCode: 501));
} else {
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
}

// MCP Gateway Endpoints for Agent Integration
app.MapPost("/api/mcp/sessions", async (
    [FromBody] McpRequest request,
    ISessionRepository sessions,
    IEventStoreRepository eventStore) => {
    switch (request.Method) {
        case "search_sessions": {
            var query = McpString(request.Params, "query");
            var author = McpString(request.Params, "author") ?? McpString(request.Params, "author_github_id");
            var repoArg = McpString(request.Params, "repo");
            var repo = string.Equals(repoArg, "all", StringComparison.OrdinalIgnoreCase) ? null : repoArg;
            var limit = Math.Clamp(McpInt(request.Params, "limit", 10), 1, 50);
            var offset = Math.Max(0, McpInt(request.Params, "offset", 0));
            var found = await sessions.SearchSessionsAsync(query, author, repo, limit, offset);
            var hits = found.Select(s => (object)new {
                session_id = s.SessionId,
                title      = s.Title,
                owner      = s.OwnerUserId,
                snippet    = s.Title ?? s.Slug,
                repo       = s.RepoHash ?? (s.RepoOwner is null ? null : $"{s.RepoOwner}/{s.RepoName}")
            }).ToList();
            return Results.Ok(new { hits, limit, offset });
        }
        case "get_session_summary": {
            if (!TryMcpSessionId(request.Params, out var summaryId))
                return Results.Problem(statusCode: 400, detail: "session_id is required and must be a non-empty string.");
            var session = await sessions.GetSessionAsync(summaryId);
            if (session is null) return Results.NotFound();
            var events = await eventStore.GetEventsAsync(summaryId);
            return Results.Ok(McpSessionSummary(session, events));
        }
        case "get_session_transcript": {
            if (!TryMcpSessionId(request.Params, out var transcriptId))
                return Results.Problem(statusCode: 400, detail: "session_id is required and must be a non-empty string.");
            if (await sessions.GetSessionAsync(transcriptId) is null) return Results.NotFound();
            var agentId = McpString(request.Params, "agent_id");
            var events = await eventStore.GetEventsAsync(transcriptId, agentId);
            var window = McpEventWindow(events, request.Params);
            return Results.Ok(new {
                session_id = transcriptId,
                events = window.Select(e => new {
                    event_index = e.LineNumber,
                    agent_id    = e.AgentId,
                    event_type  = e.EventType,
                    text        = e.Content ?? e.ToolOutput,
                    tool        = e.ToolName,
                    timestamp   = e.Timestamp
                })
            });
        }
        case "list_turns": {
            if (!TryMcpSessionId(request.Params, out var turnsId))
                return Results.Problem(statusCode: 400, detail: "session_id is required and must be a non-empty string.");
            if (await sessions.GetSessionAsync(turnsId) is null) return Results.NotFound();
            var events = await eventStore.GetEventsAsync(turnsId);
            return Results.Ok(new { session_id = turnsId, turns = McpTurns(events) });
        }
        case "get_turn": {
            if (!TryMcpSessionId(request.Params, out var turnSessionId))
                return Results.Problem(statusCode: 400, detail: "session_id is required and must be a non-empty string.");
            if (!TryMcpInt(request.Params, "turn_index", out var turnIndex) || turnIndex < 0)
                return Results.Problem(statusCode: 400, detail: "turn_index is required and must be a non-negative integer.");
            if (await sessions.GetSessionAsync(turnSessionId) is null) return Results.NotFound();
            var events = await eventStore.GetEventsAsync(turnSessionId);
            var turnEvents = McpTurnEvents(events, turnIndex);
            if (turnEvents is null) return Results.NotFound();
            return Results.Ok(new { session_id = turnSessionId, turn_index = turnIndex, events = turnEvents });
        }
        default:
            return Results.Problem(statusCode: 400, detail: $"Unsupported MCP method '{request.Method}'.");
    }
});

app.MapPost("/api/mcp/analytics", async (
    [FromBody] McpRequest request,
    IServiceProvider services,
    CancellationToken ct) => {
    switch (request.Method) {
        case "get_analytics_schema":
        case "query_analytics":
            if (isPostgres)
                return Results.Problem("Analytics views are not yet available under Database:Provider=Postgres.", statusCode: 501);
            if (request.Method == "get_analytics_schema") {
                var conn = services.GetRequiredService<SqliteConnection>();
                var gate = services.GetRequiredService<SqliteGate>();
                var text = await gate.RunAsync(async () => {
                    var sb = new StringBuilder();
                    sb.AppendLine("# Capacitor analytics views");
                    sb.AppendLine();
                    sb.AppendLine("Only single-statement SELECT queries over these v_an_* views are permitted.");
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'view' AND name LIKE 'v_an_%' ORDER BY name;";
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) {
                        sb.Append("## ").AppendLine(reader.GetString(0));
                        sb.AppendLine("```sql");
                        sb.AppendLine(reader.IsDBNull(1) ? "" : reader.GetString(1).Trim());
                        sb.AppendLine("```");
                        sb.AppendLine();
                    }
                    return sb.ToString();
                }, ct);
                return Results.Ok(new { text, max_rows = SqliteAnalyticsService.DefaultMaxRows });
            } else {
                var sql = McpString(request.Params, "sql");
                if (sql is null)
                    return Results.Problem(statusCode: 400, detail: "sql is required and must be a non-empty string.");
                var analytics = services.GetRequiredService<SqliteAnalyticsService>();
                var repos = McpString(request.Params, "repo") is { } one
                    ? new[] { one }
                    : null;
                try {
                    var result = await analytics.ExecuteGovernedQueryAsync(sql, repos, McpInt(request.Params, "max_rows", 0) is var n && n > 0 ? n : null, ct);
                    return Results.Ok(new { rows = result.Rows, truncated = result.Truncated, max_rows = result.MaxRows });
                } catch (InvalidOperationException ex) {
                    return Results.Problem(statusCode: 400, detail: ex.Message);
                } catch (SqliteException ex) {
                    return Results.Problem(statusCode: 400, detail: ex.Message);
                }
            }
        default:
            return Results.Problem(statusCode: 400, detail: $"Unsupported MCP method '{request.Method}'.");
    }
});

var declaredWorkItems = new ConcurrentDictionary<string, List<object>>(StringComparer.Ordinal);
app.MapPost("/api/mcp/workitems", ([FromBody] McpRequest request) => {
    switch (request.Method) {
        case "declare_work_item": {
            if (!TryMcpSessionId(request.Params, out var sessionId))
                return Results.Problem(statusCode: 400, detail: "session_id is required and must be a non-empty string.");
            var item = new {
                work_item_id = Guid.NewGuid().ToString("N"),
                session_id   = sessionId,
                issue_key    = McpString(request.Params, "issue_key"),
                pr_number    = McpInt(request.Params, "pr_number", 0) is var pr && pr > 0 ? pr : (int?)null,
                title        = McpString(request.Params, "new_title") ?? McpString(request.Params, "issue_key")
            };
            declaredWorkItems.AddOrUpdate(
                sessionId,
                _ => new List<object> { item },
                (_, existing) => { existing.Add(item); return existing; });
            return Results.Ok(item);
        }
        case "get_session_work_items": {
            if (!TryMcpSessionId(request.Params, out var sessionId))
                return Results.Problem(statusCode: 400, detail: "session_id is required and must be a non-empty string.");
            declaredWorkItems.TryGetValue(sessionId, out var items);
            return Results.Ok(new { session_id = sessionId, work_items = (object)(items ?? new List<object>()) });
        }
        case "declare_work_breakdown":
        case "retract_work_breakdown":
        case "declare_work_relation":
        case "retract_work_relation":
        case "get_work_item_topology":
            return Results.Problem(statusCode: 501, detail: $"MCP method '{request.Method}' is not persisted yet.");
        default:
            return Results.Problem(statusCode: 400, detail: $"Unsupported MCP method '{request.Method}'.");
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

static string? McpString(Dictionary<string, object>? parameters, string key) {
    if (parameters is null || !parameters.TryGetValue(key, out var value) || value is null) return null;
    var text = value is JsonElement el
        ? el.ValueKind == JsonValueKind.String ? el.GetString() : el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : el.ToString()
        : value.ToString();
    return string.IsNullOrWhiteSpace(text) ? null : text;
}

static int McpInt(Dictionary<string, object>? parameters, string key, int fallback) =>
    TryMcpInt(parameters, key, out var value) ? value : fallback;

static bool TryMcpInt(Dictionary<string, object>? parameters, string key, out int value) {
    value = 0;
    if (parameters is null || !parameters.TryGetValue(key, out var raw) || raw is null) return false;
    if (raw is int i) { value = i; return true; }
    if (raw is long l) { value = (int)l; return true; }
    if (raw is JsonElement el && el.TryGetInt32(out var fromJson)) { value = fromJson; return true; }
    return int.TryParse(raw.ToString(), out value);
}

static bool TryMcpSessionId(Dictionary<string, object>? parameters, out string sessionId) {
    sessionId = McpString(parameters, "session_id")?.Replace("-", "") ?? "";
    return sessionId.Length > 0;
}

static object McpSessionSummary(SessionHeaderRecord session, IReadOnlyList<SessionEventRecord> events) {
    var narrative = events
        .Where(e => e.EventType is "UserMessage" or "AssistantTurn" && !string.IsNullOrWhiteSpace(e.Content))
        .Select(e => e.Content!)
        .Take(8)
        .ToList();
    var summaryText = string.Join("\n", narrative);
    if (string.IsNullOrWhiteSpace(summaryText))
        summaryText = session.Title ?? "";
    var plan = events.LastOrDefault(e =>
        e.EventType.Equals("Plan", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(e.Content))?.Content;
    return new { summary_text = summaryText, plan };
}

static IReadOnlyList<SessionEventRecord> McpEventWindow(
        IReadOnlyList<SessionEventRecord> events,
        Dictionary<string, object>? parameters) {
    if (TryMcpInt(parameters, "around_event", out var around)) {
        var before = Math.Max(0, McpInt(parameters, "before", 5));
        var after = Math.Max(0, McpInt(parameters, "after", 15));
        return events.Where(e => e.LineNumber >= around - before && e.LineNumber <= around + after).ToList();
    }

    var limit = Math.Clamp(McpInt(parameters, "limit", 50), 1, 500);
    var offset = Math.Max(0, McpInt(parameters, "offset", 0));
    return events.Skip(offset).Take(limit).ToList();
}

static List<object> McpTurns(IReadOnlyList<SessionEventRecord> events) {
    var turns = new List<object>();
    var turnIndex = -1;
    string? prompt = null;
    var tools = new List<string>();
    long tokens = 0;

    void Flush() {
        if (turnIndex < 0) return;
        turns.Add(new {
            turn_index  = turnIndex,
            user_prompt = prompt,
            tools,
            tokens,
            prose       = prompt
        });
        tools = new List<string>();
        tokens = 0;
        prompt = null;
    }

    foreach (var ev in events) {
        if (ev.EventType == "UserMessage") {
            Flush();
            turnIndex++;
            prompt = ev.Content;
        } else if (turnIndex >= 0) {
            if (ev.ToolName is { Length: > 0 } tool) tools.Add(tool);
            tokens += ev.InputTokens + ev.OutputTokens;
        }
    }

    Flush();
    return turns;
}

static List<SessionEventRecord>? McpTurnEvents(IReadOnlyList<SessionEventRecord> events, int turnIndex) {
    var current = -1;
    List<SessionEventRecord>? bucket = null;
    foreach (var ev in events) {
        if (ev.EventType == "UserMessage") {
            if (bucket is not null) return bucket;
            current++;
            if (current == turnIndex) bucket = new List<SessionEventRecord> { ev };
        } else if (bucket is not null) {
            bucket.Add(ev);
        }
    }
    return bucket;
}

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
    public record McpRequest(string Method, Dictionary<string, object>? Params = null);
}
