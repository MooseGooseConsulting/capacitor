using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Capacitor.Cli.Core;
using Capacitor.Server.Data;
using Capacitor.Server.Data.Entities;
using Capacitor.Server.Ingest;
using Capacitor.Server.Normalizers;
using Capacitor.Server.Analytics;
using Capacitor.Server.Api;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Capacitor")
    ?? throw new InvalidOperationException("ConnectionStrings:Capacitor must point at the PostgreSQL recovery database.");

builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddSingleton<IEventStoreRepository, PostgresEventStoreRepository>();
builder.Services.AddSingleton<ISessionWatermarkRepository, PostgresWatermarkRepository>();
builder.Services.AddSingleton<ISessionRepository, PostgresSessionRepository>();
builder.Services.AddSingleton<ITranscriptIngest, TranscriptIngestEngine>();
builder.Services.AddSingleton<NormalizerRouter>();
builder.Services.AddSingleton<SessionRollupProjector>();
builder.Services.AddSingleton<PostgresAnalyticsService>();

var app = builder.Build();

await PostgresDatabaseInitializer.InitializeAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

app.MapGet("/health", async (NpgsqlDataSource dataSource, CancellationToken ct) => {
    await using var connection = await dataSource.OpenConnectionAsync(ct);
    await using var command = new NpgsqlCommand("SELECT 1;", connection);
    await command.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "healthy", time = DateTimeOffset.UtcNow });
});

async Task<IResult> HandleSessionStart(string vendor, ApiSessionStartPayload payload, ISessionRepository sessions) {
    var sessionId = IdCanonicalizer.Canonicalize(payload.SessionId);
    var existed = await sessions.GetSessionAsync(sessionId) != null;
    var session = await sessions.GetOrCreatePlaceholderAsync(sessionId, vendor, payload.UserId, payload.DefaultVisibility);

    if (existed && (payload.UserId is { Length: > 0 } || payload.DefaultVisibility is { Length: > 0 })) {
        await sessions.PatchSessionStartAsync(sessionId, payload.UserId, payload.DefaultVisibility);
        session = await sessions.GetSessionAsync(sessionId) ?? session;
    }

    return Results.Ok(new { status = "started", session_id = session.SessionId });
}

async Task<IResult> HandleSessionEnd(string vendor, ApiSessionEndPayload payload, ISessionRepository sessions, SessionRollupProjector projector) {
    var sessionId = IdCanonicalizer.Canonicalize(payload.SessionId);
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
// every harness posts.
app.MapPost("/hooks/subagent-start", async ([FromBody] JsonElement payload, ISessionRepository sessions) => {
    var sessionId = IdCanonicalizer.Canonicalize(payload.Str("session_id"));
    if (sessionId.Length > 0) await sessions.GetOrCreatePlaceholderAsync(sessionId, "claude");
    return Results.Ok(new { status = "started" });
});

app.MapPost("/hooks/subagent-stop", ([FromBody] JsonElement payload) => Results.Ok(new { status = "stopped" }));

app.MapPost("/hooks/transcript", async (
    [FromBody] TranscriptBatch batch,
    ISessionRepository sessions,
    ISessionWatermarkRepository watermarks,
    ITranscriptIngest ingest,
    NormalizerRouter router,
    SessionRollupProjector projector) => {
    var sessionId = IdCanonicalizer.Canonicalize(batch.SessionId);
    var vendor = batch.Vendor ?? "claude";

    if (batch.LineNumbers != null) {
        if (batch.LineNumbers.Length != batch.Lines.Length) {
            return Results.BadRequest(new { detail = "line_numbers, when present, must have the same length as lines." });
        }

        if (batch.LineNumbers.Distinct().Count() != batch.LineNumbers.Length) {
            return Results.BadRequest(new { detail = "line_numbers must not contain duplicates." });
        }
    }

    await sessions.GetOrCreatePlaceholderAsync(sessionId, vendor);
    if (batch.Repository is { } repo) {
        var repoHash = repo.Owner is { Length: > 0 } && repo.RepoName is { Length: > 0 }
            ? RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName)
            : null;
        await sessions.UpdateRepositoryMetadataAsync(
            sessionId, repoHash, repo.Owner, repo.RepoName, repo.Branch,
            repo.PrNumber, repo.PrTitle, repo.PrUrl, repo.PrHeadRef);
    }

    var agentId = IdCanonicalizer.Canonicalize(batch.AgentId);
    var events = new List<SessionEventRecord>();
    var highestLine = 0;
    var failedCount = 0;

    for (var i = 0; i < batch.Lines.Length; i++) {
        var lineNum = batch.LineNumbers != null ? batch.LineNumbers[i] : i + 1;
        if (lineNum > highestLine) highestLine = lineNum;

        var lineEvents = router.Normalize(vendor, sessionId, agentId, lineNum, batch.Lines[i], out var lineFailed);
        if (lineFailed) failedCount++;
        foreach (var ev in lineEvents) {
            events.Add(ev with { Vendor = vendor, AgentId = agentId, SessionId = sessionId });
        }
    }

    if (events.Count > 0) {
        await ingest.IngestAsync(events);
        await projector.ProjectSessionRollupAsync(sessionId);
    } else if (batch.Lines.Length > 0) {
        await watermarks.UpdateWatermarkAsync(sessionId, agentId, highestLine);
    }

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
    var sessionId = IdCanonicalizer.Canonicalize(id);
    var session = await sessions.GetSessionAsync(sessionId);
    if (session == null) return Results.NotFound();

    var line = await watermarks.GetLastLineNumberAsync(sessionId, IdCanonicalizer.Canonicalize(agentId));
    if (line is null) return Results.NoContent();

    return Results.Ok(new { session_id = sessionId, agent_id = IdCanonicalizer.Canonicalize(agentId), last_line_number = line.Value });
});

app.MapGet("/watermarks", async (
    [FromQuery] string session_id,
    [FromQuery] string? agent_id,
    ISessionWatermarkRepository watermarks) => {
    var sessionId = IdCanonicalizer.Canonicalize(session_id);
    var canonicalAgent = IdCanonicalizer.Canonicalize(agent_id);
    var line = await watermarks.GetLastLineNumberAsync(sessionId, canonicalAgent);
    return Results.Ok(new { session_id = sessionId, agent_id = canonicalAgent, last_line_number = line });
});

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
    var sessionId = IdCanonicalizer.Canonicalize(id);
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

    var events = new List<SessionEventRecord>();
    foreach (var chainedId in sessionChain) {
        events.AddRange(await eventStore.GetEventsAsync(chainedId));
    }

    var (trace, compaction) = EvalContextComposer.Compose(events, threshold ?? 2000);
    return Results.Ok(new {
        session_id    = sessionId,
        session_chain = sessionChain,
        trace         = trace,
        compaction    = compaction
    });
});

app.MapPost("/api/sessions/{id}/evals/v3", async (
    string id,
    [FromBody] SessionEvalCompletedPayloadV3 payload,
    ISessionRepository sessions) => {
    var sessionId = IdCanonicalizer.Canonicalize(id);
    var session = await sessions.GetSessionAsync(sessionId);
    if (session == null) return Results.NotFound();

    var retrospectiveJson = payload.Retrospective is null
        ? null
        : JsonSerializer.Serialize(payload.Retrospective, CapacitorJsonContext.Default.EvalRetrospectiveV2);

    var run = new EvalRunRecord {
        EvalRunId = payload.EvalRunId,
        SessionId = sessionId,
        JudgeModel = payload.JudgeModel,
        OverallScore = payload.OverallScore,
        Summary = payload.Summary,
        RetrospectiveJson = retrospectiveJson,
        RetrospectivePromptVersion = payload.RetrospectivePromptVersion,
        EvaluatedAt = DateTimeOffset.UtcNow
    };
    var verdicts = payload.Categories
        .SelectMany(c => c.Questions.Select(q => new EvalVerdictRecord {
            EvalRunId = payload.EvalRunId,
            Category = q.Category,
            QuestionId = q.QuestionId,
            Score = q.Score,
            Verdict = q.Verdict,
            Finding = q.Finding,
            Evidence = q.Evidence,
            Recommendation = q.Recommendation,
            ToolsUsed = q.ToolsUsed,
            PromptVersion = q.PromptVersion
        }))
        .ToList();

    await sessions.PersistEvalRunAsync(run, verdicts);
    return Results.Ok(new { status = "saved", eval_run_id = payload.EvalRunId, session_id = sessionId });
});

app.MapGet("/api/analytics/schema", async (PostgresAnalyticsService analytics, CancellationToken ct) => {
    var sb = new StringBuilder();
    sb.AppendLine("# Capacitor analytics views");
    sb.AppendLine();
    sb.AppendLine("Only single-statement SELECT queries over these v_an_* views are permitted.");
    sb.AppendLine("Every view carries repo_hash — governed queries are scoped to the caller's repos.");
    sb.AppendLine();

    var views = await analytics.GetViewDefinitionsAsync(ct);
    foreach (var (name, viewSql) in views) {
        sb.Append("## ").AppendLine(name);
        sb.AppendLine("```sql");
        sb.AppendLine(viewSql.Trim());
        sb.AppendLine("```");
        sb.AppendLine();
    }

    sb.AppendLine("Example: SELECT vendor, model, total_cost_usd FROM v_an_token_usage_by_model;");
    return Results.Ok(new { text = sb.ToString(), max_rows = PostgresAnalyticsService.DefaultMaxRows });
});

app.MapPost("/api/analytics/query", async (
    [FromBody] ApiAnalyticsQueryRequest request,
    PostgresAnalyticsService analytics,
    CancellationToken ct) => {
    try {
        var requested = request.MaxRows is int n && n > 0 ? n : PostgresAnalyticsService.DefaultMaxRows;
        var cap = Math.Min(requested, PostgresAnalyticsService.DefaultMaxRows);
        var scope = request.Repos is { Length: > 0 } && request.Repos[0] is { Length: > 0 } repo
            ? repo
            : "global";
        var rows = await analytics.ExecuteGovernedQueryAsync(request.Sql, scope, cap + 1, ct);
        var truncated = rows.Count > cap;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return Results.Ok(new { rows, truncated, max_rows = cap });
    } catch (InvalidOperationException ex) {
        return Results.Problem(statusCode: 400, detail: ex.Message);
    } catch (PostgresException ex) {
        return Results.Problem(statusCode: 400, detail: ex.Message);
    }
});

app.Run();

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
}

public partial class Program { }
