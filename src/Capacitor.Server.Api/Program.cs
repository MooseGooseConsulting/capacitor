using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Capacitor.Cli.Core;
using Capacitor.Server.Data;
using Capacitor.Server.Data.Entities;
using Capacitor.Server.Ingest;
using Capacitor.Server.Normalizers;
using Capacitor.Server.Analytics;
using Capacitor.Server.Api;

var builder = WebApplication.CreateBuilder(args);

// Database & Persistence
var dbPath = builder.Configuration["Database:Path"] ?? "capacitor.db";
var connectionString = $"Data Source={dbPath}";
var connection = new SqliteConnection(connectionString);
connection.Open();
await SqliteDatabaseInitializer.InitializeAsync(connection);

builder.Services.AddSingleton(connection);
builder.Services.AddSingleton<IEventStoreRepository, SqliteEventStoreRepository>();
builder.Services.AddSingleton<ISessionWatermarkRepository, SqliteWatermarkRepository>();
builder.Services.AddSingleton<ISessionRepository, SqliteSessionRepository>();
builder.Services.AddSingleton<NormalizerRouter>();
builder.Services.AddSingleton<SessionRollupProjector>();
builder.Services.AddSingleton<SqliteAnalyticsService>();

var app = builder.Build();

// Health Check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", time = DateTimeOffset.UtcNow }));

// Ingestion Hooks
app.MapPost("/hooks/session-start/{vendor}", async (
    string vendor,
    [FromBody] ApiSessionStartPayload payload,
    ISessionRepository sessions) => {
    var sessionId = payload.SessionId.Replace("-", "");
    await sessions.GetOrCreatePlaceholderAsync(sessionId, vendor, payload.UserId);
    return Results.Ok(new { status = "started", session_id = sessionId });
});

app.MapPost("/hooks/session-end/{vendor}", async (
    string vendor,
    [FromBody] ApiSessionEndPayload payload,
    ISessionRepository sessions,
    SessionRollupProjector projector) => {
    var sessionId = payload.SessionId.Replace("-", "");
    var session = await sessions.GetSessionAsync(sessionId);
    if (session != null) {
        var updated = session with {
            Status = "completed",
            EndedAt = payload.EndedAt ?? DateTimeOffset.UtcNow
        };
        await sessions.UpdateSessionAsync(updated);
        await projector.ProjectSessionRollupAsync(sessionId);
    }
    return Results.Ok(new { status = "ended", session_id = sessionId });
});

app.MapPost("/hooks/transcript", async (
    [FromBody] TranscriptBatch batch,
    ISessionRepository sessions,
    ISessionWatermarkRepository watermarks,
    IEventStoreRepository eventStore,
    NormalizerRouter router,
    SessionRollupProjector projector) => {
    var sessionId = batch.SessionId.Replace("-", "");
    var vendor = batch.Vendor ?? "claude";

    // Ensure session exists
    await sessions.GetOrCreatePlaceholderAsync(sessionId, vendor);

    var agentId = batch.AgentId ?? string.Empty;
    var events = new List<SessionEventRecord>();
    var highestLine = 0;

    for (var i = 0; i < batch.Lines.Length; i++) {
        var lineNum = batch.LineNumbers != null && i < batch.LineNumbers.Length
            ? batch.LineNumbers[i]
            : i + 1;
        if (lineNum > highestLine) highestLine = lineNum;

        var ev = router.Normalize(vendor, sessionId, agentId, lineNum, batch.Lines[i]);
        events.Add(ev);
    }

    if (events.Count > 0) {
        await eventStore.AppendEventsAsync(events);
        await watermarks.UpdateWatermarkAsync(sessionId, agentId, highestLine);
        await projector.ProjectSessionRollupAsync(sessionId);
    }

    return Results.Ok(new { status = "ingested", count = events.Count, highest_line = highestLine });
});

app.MapGet("/watermarks", async (
    [FromQuery] string session_id,
    [FromQuery] string? agent_id,
    ISessionWatermarkRepository watermarks) => {
    var sessionId = session_id.Replace("-", "");
    var line = await watermarks.GetLastLineNumberAsync(sessionId, agent_id ?? "");
    return Results.Ok(new { session_id = sessionId, agent_id = agent_id ?? "", last_line_number = line });
});

// Evals & Catalog
app.MapGet("/api/eval/catalog", () => Results.Ok(EvalCatalogDefinition.GetCatalog()));

app.MapGet("/api/sessions/{id}/eval-context", async (
    string id,
    ISessionRepository sessions,
    IEventStoreRepository eventStore) => {
    var sessionId = id.Replace("-", "");
    var session = await sessions.GetSessionAsync(sessionId);
    if (session == null) return Results.NotFound();

    var events = await eventStore.GetEventsAsync(sessionId);
    return Results.Ok(new {
        session = session,
        events = events,
        event_count = events.Count
    });
});

// Analytics Views
app.MapGet("/api/analytics/schema", async (SqliteConnection conn) => {
    var views = new List<string>();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'view' AND name LIKE 'v_an_%';";
    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
        views.Add(reader.GetString(0));
    }
    return Results.Ok(new { views = views, count = views.Count });
});

app.MapPost("/api/analytics/query", async (
    [FromBody] ApiAnalyticsQueryRequest request,
    SqliteAnalyticsService analytics) => {
    var rows = await analytics.ExecuteGovernedQueryAsync(request.Query);
    return Results.Ok(new { rows = rows, count = rows.Count });
});

app.Run();

namespace Capacitor.Server.Api {
    public record ApiAnalyticsQueryRequest(string Query);
    public record ApiSessionStartPayload(string SessionId, string? UserId = null);
    public record ApiSessionEndPayload(string SessionId, DateTimeOffset? EndedAt = null);
}
