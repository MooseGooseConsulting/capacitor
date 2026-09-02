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
builder.Services.AddSingleton<ITranscriptIngest, PostgresTranscriptIngestService>();
builder.Services.AddSingleton<NormalizerRouter>();
builder.Services.AddSingleton<SessionRollupProjector>();

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
    var sourceVisibility = string.IsNullOrWhiteSpace(payload.DefaultVisibility)
        ? null
        : payload.DefaultVisibility.Trim().ToLowerInvariant();
    if (sourceVisibility is not null and not ("none" or "private" or "project" or "org_public" or "public")) {
        return Results.BadRequest(new { detail = "default_visibility must be one of: none, private, project, org_public, public." });
    }

    var session = await sessions.GetOrCreatePlaceholderAsync(sessionId, vendor, payload.UserId, sourceVisibility);
    var repo = payload.Repository;
    var repoHash = repo is { Owner: { Length: > 0 }, RepoName: { Length: > 0 } }
        ? RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName)
        : null;
    await sessions.PatchSessionStartAsync(sessionId, payload.UserId, sourceVisibility, new SessionStartPatch(
        payload.StartedAt,
        payload.Model,
        payload.Slug,
        string.IsNullOrWhiteSpace(payload.PreviousSessionId)
            ? null
            : IdCanonicalizer.Canonicalize(payload.PreviousSessionId),
        repoHash,
        repo?.Owner,
        repo?.RepoName,
        repo?.Branch,
        repo?.PrNumber,
        repo?.PrTitle,
        repo?.PrUrl,
        repo?.PrHeadRef));
    session = await sessions.GetSessionAsync(sessionId) ?? session;

    return Results.Ok(new { status = "started", session_id = session.SessionId });
}

async Task<IResult> HandleSessionEnd(string vendor, ApiSessionEndPayload payload, ISessionRepository sessions, SessionRollupProjector projector) {
    var sessionId = IdCanonicalizer.Canonicalize(payload.SessionId);
    // A missing session must not read as delivered — the client's spool retries a non-2xx but
    // treats 200 as final, so an out-of-order session-end would otherwise be lost for good.
    // This narrow write must not overwrite concurrently projected transcript aggregates.
    if (!await sessions.CompleteSessionAsync(sessionId, payload.EndedAt ?? DateTimeOffset.UtcNow)) return Results.NotFound();
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

async Task<IResult> HandleSessionTitle(ApiSessionTitlePayload payload, ISessionRepository sessions) {
    var sessionId = IdCanonicalizer.Canonicalize(payload.SessionId);
    if (string.IsNullOrWhiteSpace(payload.Title)) return Results.BadRequest(new { detail = "title is required." });
    if (await sessions.GetSessionAsync(sessionId) is null) return Results.NotFound();
    await sessions.UpdateSessionTitleAsync(sessionId, payload.Title.Trim());
    return Results.Ok(new { status = "updated", session_id = sessionId });
}

app.MapPost("/hooks/set-title", ([FromBody] ApiSessionTitlePayload payload, ISessionRepository sessions)
    => HandleSessionTitle(payload, sessions));
app.MapPost("/hooks/session-title", ([FromBody] ApiSessionTitlePayload payload, ISessionRepository sessions)
    => HandleSessionTitle(payload, sessions));

// Importers do not stream a child transcript until this returns 2xx, so the ACK is only emitted
// after the corresponding lifecycle record is durable.
app.MapPost("/hooks/subagent-start", async ([FromBody] ApiSubagentStartPayload payload, ISessionRepository sessions) => {
    var sessionId = IdCanonicalizer.Canonicalize(payload.SessionId);
    var agentId = IdCanonicalizer.Canonicalize(payload.AgentId);
    if (sessionId.Length == 0 || agentId.Length == 0) {
        return Results.BadRequest(new { detail = "session_id and agent_id are required." });
    }

    await sessions.GetOrCreatePlaceholderAsync(sessionId, "claude");
    await sessions.UpsertSubagentRunAsync(new SubagentRunRecord {
        ParentSessionId = sessionId,
        AgentId = agentId,
        AgentType = payload.AgentType,
        Role = payload.Role,
        Prompt = payload.Prompt,
        SpawnedAt = payload.StartedAt ?? DateTimeOffset.UtcNow
    });
    return Results.Ok(new { status = "started", session_id = sessionId, agent_id = agentId });
});

app.MapPost("/hooks/subagent-stop", async ([FromBody] ApiSubagentStopPayload payload, ISessionRepository sessions) => {
    var sessionId = IdCanonicalizer.Canonicalize(payload.SessionId);
    var agentId = IdCanonicalizer.Canonicalize(payload.AgentId);
    if (sessionId.Length == 0 || agentId.Length == 0) {
        return Results.BadRequest(new { detail = "session_id and agent_id are required." });
    }

    return await sessions.CompleteSubagentRunAsync(
        sessionId, agentId, payload.StoppedAt ?? DateTimeOffset.UtcNow, payload.ExitStatus)
        ? Results.Ok(new { status = "stopped", session_id = sessionId, agent_id = agentId })
        : Results.NotFound();
});

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
    var acceptedSourceLines = new List<TranscriptSourceLine>();
    var rejectedSourceLines = new List<RejectedTranscriptSourceLine>();
    var highestLine = 0;
    var failedCount = 0;

    for (var i = 0; i < batch.Lines.Length; i++) {
        var lineNum = batch.LineNumbers != null ? batch.LineNumbers[i] : i + 1;
        if (lineNum > highestLine) highestLine = lineNum;

        var lineEvents = router.Normalize(vendor, sessionId, agentId, lineNum, batch.Lines[i], out var lineFailed);
        if (lineFailed) {
            failedCount++;
            rejectedSourceLines.Add(new RejectedTranscriptSourceLine(
                sessionId, agentId, lineNum, vendor, batch.Lines[i], "normalization failed"));
            // A rejected source line must not occupy its event identity. A corrected replay uses
            // the same stream coordinates and must be able to persist its normalized result.
            continue;
        }

        acceptedSourceLines.Add(new TranscriptSourceLine(sessionId, agentId, lineNum));
        foreach (var ev in lineEvents) {
            events.Add(ev with { Vendor = vendor, AgentId = agentId, SessionId = sessionId });
        }
    }

    if (events.Count > 0 || acceptedSourceLines.Count > 0 || rejectedSourceLines.Count > 0) {
        await ingest.IngestAsync(
            events,
            firstLineNumber: batch.LineNumbers is null ? 1 : 0,
            acceptedSourceLines: acceptedSourceLines,
            rejectedSourceLines: rejectedSourceLines,
            inferOmittedSourceLines: batch.LineNumbers is not null);
        await projector.ProjectSessionRollupAsync(sessionId);
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

app.MapGet("/api/sessions/search", async (
    [FromQuery] string? query,
    [FromQuery(Name = "q")] string? q,
    [FromQuery] string? author,
    [FromQuery(Name = "author_github_id")] long? authorGithubId,
    [FromQuery] string? repo,
    [FromQuery] string? vendor,
    [FromQuery] string? status,
    [FromQuery] int? limit,
    [FromQuery] int? offset,
    ISessionRepository sessions,
    CancellationToken ct) => {
    if (authorGithubId is not null) {
        return Results.Problem(
            statusCode: StatusCodes.Status501NotImplemented,
            detail: "author_github_id requires an identity source that the PostgreSQL capture foundation does not store.");
    }
    var page = await sessions.SearchSessionsAsync(new SessionSearchQuery(
        q ?? query, author, repo, vendor, status, limit ?? 50, offset ?? 0), ct);
    return Results.Ok(new { hits = page.Sessions, total = page.Total });
});

// Session-start retains the source visibility label as metadata. Altering access visibility is
// intentionally unavailable until this recovery API has an authenticated policy evaluator.
app.MapPut("/api/sessions/{id}/visibility", (string id) => Results.Problem(
    statusCode: StatusCodes.Status501NotImplemented,
    detail: "Session visibility policy requires authenticated caller and repository context; the recovery capture API stores source labels but does not enforce access control."));

async Task<IResult> RequireSessionAsync(
    string id,
    ISessionRepository sessions,
    CancellationToken ct) {
    var session = await sessions.GetSessionAsync(IdCanonicalizer.Canonicalize(id), ct);
    return session is null ? Results.NotFound() : Results.Ok(session);
}

app.MapGet("/api/sessions/{id}", async (
    string id,
    ISessionRepository sessions,
    IEventStoreRepository eventStore,
    CancellationToken ct) => {
    var sessionId = IdCanonicalizer.Canonicalize(id);
    var session = await sessions.GetSessionAsync(sessionId, ct);
    if (session is null) return Results.NotFound();

    var events = await eventStore.GetEventsAsync(sessionId, ct: ct);
    return Results.Ok(new SessionDashboardDetail(session, events, SessionTraceComposer.Compose(events)));
});

app.MapGet("/api/sessions/{id}/overview", (
    string id,
    ISessionRepository sessions,
    CancellationToken ct) => RequireSessionAsync(id, sessions, ct));

app.MapGet("/api/sessions/{id}/details", (
    string id,
    ISessionRepository sessions,
    CancellationToken ct) => RequireSessionAsync(id, sessions, ct));

app.MapGet("/api/sessions/{id}/events", async (
    string id,
    [FromQuery] string? agentId,
    [FromQuery] int? fromLine,
    ISessionRepository sessions,
    IEventStoreRepository eventStore,
    CancellationToken ct) => {
    var sessionId = IdCanonicalizer.Canonicalize(id);
    if (await sessions.GetSessionAsync(sessionId, ct) is null) return Results.NotFound();
    var events = await eventStore.GetEventsAsync(
        sessionId,
        string.IsNullOrWhiteSpace(agentId) ? null : IdCanonicalizer.Canonicalize(agentId),
        Math.Max(0, fromLine ?? 0),
        ct);
    return Results.Ok(new { session_id = sessionId, events });
});

app.MapGet("/api/sessions/{id}/transcript", async (
    string id,
    [FromQuery(Name = "agent_id")] string? agentId,
    [FromQuery(Name = "around_event")] int? aroundEvent,
    [FromQuery] int? before,
    [FromQuery] int? after,
    [FromQuery] int? limit,
    [FromQuery] int? offset,
    [FromQuery(Name = "include_thinking")] bool? includeThinking,
    ISessionRepository sessions,
    IEventStoreRepository eventStore,
    CancellationToken ct) => {
    var sessionId = IdCanonicalizer.Canonicalize(id);
    var session = await sessions.GetSessionAsync(sessionId, ct);
    if (session is null) return Results.NotFound();
    var events = await eventStore.GetEventsAsync(
        sessionId,
        string.IsNullOrWhiteSpace(agentId) ? null : IdCanonicalizer.Canonicalize(agentId),
        ct: ct);
    var visibleEvents = includeThinking == true
        ? events
        : events.Where(@event => !@event.EventType.Contains("Thinking", StringComparison.OrdinalIgnoreCase)).ToArray();
    IReadOnlyList<SessionEventRecord> window;
    if (aroundEvent is int center) {
        var beforeCount = Math.Max(0, before ?? 5);
        var afterCount = Math.Max(0, after ?? 15);
        var start = Math.Clamp(center - beforeCount, 0, visibleEvents.Count);
        window = visibleEvents.Skip(start).Take(beforeCount + afterCount + 1).ToArray();
    } else {
        window = visibleEvents.Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 50, 1, 500)).ToArray();
    }
    return Results.Ok(new { session, events = window });
});

app.MapGet("/api/sessions/{id}/turns", async (
    string id,
    ISessionRepository sessions,
    IEventStoreRepository eventStore,
    CancellationToken ct) => {
    var sessionId = IdCanonicalizer.Canonicalize(id);
    if (await sessions.GetSessionAsync(sessionId, ct) is null) return Results.NotFound();
    var events = await eventStore.GetEventsAsync(sessionId, ct: ct);
    return Results.Ok(SessionTraceComposer.SummarizeTurns(events));
});

app.MapGet("/api/sessions/{id}/turns/{turnIndex:int}", async (
    string id,
    int turnIndex,
    ISessionRepository sessions,
    IEventStoreRepository eventStore,
    CancellationToken ct) => {
    var sessionId = IdCanonicalizer.Canonicalize(id);
    if (await sessions.GetSessionAsync(sessionId, ct) is null) return Results.NotFound();
    var events = await eventStore.GetEventsAsync(sessionId, ct: ct);
    var turn = SessionTraceComposer.GetTurn(events, turnIndex);
    return turn is null ? Results.NotFound() : Results.Ok(turn);
});

app.Run();

namespace Capacitor.Server.Api {
    using System.Text.Json.Serialization;

    public sealed record SessionDashboardDetail(
        SessionHeaderRecord Session,
        IReadOnlyList<SessionEventRecord> Events,
        SessionTraceDocument Trace);

    public record ApiSessionStartPayload {
        [JsonPropertyName("session_id")]
        public required string SessionId { get; init; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; init; }

        [JsonPropertyName("default_visibility")]
        public string? DefaultVisibility { get; init; }

        [JsonPropertyName("started_at")]
        public DateTimeOffset? StartedAt { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("slug")]
        public string? Slug { get; init; }

        [JsonPropertyName("previous_session_id")]
        public string? PreviousSessionId { get; init; }

        [JsonPropertyName("repository")]
        public ApiRepositoryPayload? Repository { get; init; }
    }

    public record ApiRepositoryPayload {
        [JsonPropertyName("owner")]
        public string? Owner { get; init; }

        [JsonPropertyName("repo_name")]
        public string? RepoName { get; init; }

        [JsonPropertyName("branch")]
        public string? Branch { get; init; }

        [JsonPropertyName("pr_number")]
        public int? PrNumber { get; init; }

        [JsonPropertyName("pr_title")]
        public string? PrTitle { get; init; }

        [JsonPropertyName("pr_url")]
        public string? PrUrl { get; init; }

        [JsonPropertyName("pr_head_ref")]
        public string? PrHeadRef { get; init; }
    }

    public record ApiSessionEndPayload {
        [JsonPropertyName("session_id")]
        public required string SessionId { get; init; }

        [JsonPropertyName("ended_at")]
        public DateTimeOffset? EndedAt { get; init; }
    }

    public record ApiSessionTitlePayload {
        [JsonPropertyName("session_id")]
        public required string SessionId { get; init; }

        [JsonPropertyName("title")]
        public required string Title { get; init; }
    }

    public record ApiSubagentStartPayload {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }

        [JsonPropertyName("agent_id")]
        public string? AgentId { get; init; }

        [JsonPropertyName("agent_type")]
        public string? AgentType { get; init; }

        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; init; }

        [JsonPropertyName("started_at")]
        public DateTimeOffset? StartedAt { get; init; }
    }

    public record ApiSubagentStopPayload {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }

        [JsonPropertyName("agent_id")]
        public string? AgentId { get; init; }

        [JsonPropertyName("stopped_at")]
        public DateTimeOffset? StoppedAt { get; init; }

        [JsonPropertyName("exit_status")]
        public string? ExitStatus { get; init; }
    }
}

public partial class Program { }
