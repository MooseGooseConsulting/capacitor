using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Ingest;

public readonly record struct SessionRollupAggregate(
    int EventCount,
    int ToolCount,
    long TotalTokens,
    decimal TotalCostUsd,
    decimal DurationMin,
    DateTimeOffset? LastEventAt);

/// <summary>Filters for the repository-backed Sessions dashboard list.</summary>
public sealed record SessionSearchQuery(
    string? Query,
    string? Author,
    string? Repo,
    string? Vendor,
    string? Status,
    int Limit = 50,
    int Offset = 0);

public sealed record SessionSearchPage(
    IReadOnlyList<SessionHeaderRecord> Sessions,
    long Total);

/// <summary>
/// Repository and working-directory evidence supplied with one source line or
/// transcript batch. The repository remains nullable because several supported
/// vendors cannot provide one.
/// </summary>
public sealed record RepositoryEvidence(
    string? RepoHash,
    string? RepoOwner,
    string? RepoName,
    string? Cwd) {
    public bool HasRepository => !string.IsNullOrWhiteSpace(RepoHash);
}

public sealed record SessionStartPatch(
    DateTimeOffset? StartedAt,
    string? Model,
    string? Slug,
    string? PreviousSessionId,
    string? RepoHash,
    string? RepoOwner,
    string? RepoName,
    string? Branch,
    int? PrNumber,
    string? PrTitle,
    string? PrUrl,
    string? PrHeadRef);

/// <summary>
/// One source line received by the server, including a line accepted by a
/// normalizer that emits no display event. This is the durable resume boundary,
/// distinct from normalized events.
/// </summary>
public sealed record TranscriptSourceLine(
    string SessionId,
    string AgentId,
    int LineNumber,
    string Vendor = "claude",
    string RawPayload = "",
    RepositoryEvidence? RepositoryEvidence = null);

/// <summary>A source line the normalizer rejected and that must block watermark progress until replay succeeds.</summary>
public sealed record RejectedTranscriptSourceLine(
    string SessionId,
    string AgentId,
    int LineNumber,
    string Vendor,
    string RawLine,
    string ErrorReason,
    RepositoryEvidence? RepositoryEvidence = null);

public sealed record SessionEvaluation(
    EvalRunRecord Run,
    IReadOnlyList<EvalVerdictRecord> Verdicts);

public interface IEventStoreRepository {
    Task<int> AppendEventsAsync(IReadOnlyList<SessionEventRecord> events, CancellationToken ct = default);
    Task<IReadOnlyList<SessionEventRecord>> GetEventsAsync(string sessionId, string? agentId = null, int fromLine = 0, CancellationToken ct = default);
    Task<long> GetEventCountAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Null when the session has no events; otherwise the columns UpdateRollupAsync writes, computed in the store so a transcript batch never materializes prior rows.</summary>
    Task<SessionRollupAggregate?> GetRollupAggregateAsync(string sessionId, CancellationToken ct = default);
}

public interface ISessionWatermarkRepository {
    /// <summary>Null means no watermark row exists yet; 0 is a genuinely ingested line 0.</summary>
    Task<int?> GetLastLineNumberAsync(string sessionId, string agentId = "", CancellationToken ct = default);
    Task UpdateWatermarkAsync(string sessionId, string agentId, int lastLineNumber, long byteOffset = 0, CancellationToken ct = default);
}

public interface ISessionRepository {
    Task<SessionHeaderRecord> GetOrCreatePlaceholderAsync(string sessionId, string vendor, string? ownerUserId = null, CancellationToken ct = default);
    Task<SessionHeaderRecord> GetOrCreatePlaceholderAsync(string sessionId, string vendor, string? ownerUserId, string? defaultVisibility, CancellationToken ct = default);
    Task<SessionHeaderRecord?> GetSessionAsync(string sessionId, CancellationToken ct = default);
    Task<bool> CompleteSessionAsync(string sessionId, DateTimeOffset endedAt, CancellationToken ct = default);
    Task<SessionSearchPage> SearchSessionsAsync(SessionSearchQuery query, CancellationToken ct = default);
    Task UpdateSessionAsync(SessionHeaderRecord session, CancellationToken ct = default);
    Task UpdateSessionTitleAsync(string sessionId, string title, CancellationToken ct = default);
    Task UpsertSubagentRunAsync(SubagentRunRecord run, CancellationToken ct = default);
    Task<bool> CompleteSubagentRunAsync(
        string parentSessionId,
        string agentId,
        DateTimeOffset stoppedAt,
        string? exitStatus,
        CancellationToken ct = default);

    // Owner/visibility only. A concurrent session-end can commit completed between the
    // session-start handler's read and this write; a full-row UpdateSessionAsync would
    // resurrect the stale active status.
    Task PatchSessionStartAsync(
        string sessionId,
        string? ownerUserId,
        string? defaultVisibility,
        SessionStartPatch? patch = null,
        CancellationToken ct = default);

    // Repository metadata only. Must not write status/ended_at/aggregates — a concurrent
    // session-end can commit completed between the transcript handler's read of the placeholder
    // and this write, and a full-row UpdateSessionAsync would resurrect the stale active status.
    Task UpdateRepositoryMetadataAsync(
        string sessionId,
        string? repoHash,
        string? repoOwner,
        string? repoName,
        string? branch,
        int? prNumber,
        string? prTitle,
        string? prUrl,
        string? prHeadRef,
        CancellationToken ct = default);

    /// <summary>
    /// Retains a supplied repository association without selecting it as the
    /// primary repository. Primary selection is derived only from event evidence.
    /// </summary>
    Task RecordRepositoryAssociationAsync(
        string sessionId,
        RepositoryEvidence evidence,
        CancellationToken ct = default);

    Task PersistEvalRunAsync(EvalRunRecord run, IReadOnlyList<EvalVerdictRecord> verdicts, CancellationToken ct = default);
    Task<SessionEvaluation?> GetLatestEvaluationAsync(string sessionId, CancellationToken ct = default);

    // Rollup-only write: touches the aggregate columns exclusively. A concurrent session-end can
    // commit status="completed" between this projection's read and its write, so this must never
    // set status/ended_at — that stays UpdateSessionAsync's job for the handlers that intend it.
    Task UpdateRollupAsync(
        string sessionId,
        int eventCount,
        int toolCount,
        long totalTokens,
        decimal totalCostUsd,
        decimal durationMin,
        DateTimeOffset? lastEventAt,
        CancellationToken ct = default);
}

public interface ITranscriptIngest {
    Task<int> IngestAsync(
        IReadOnlyList<SessionEventRecord> events,
        string? ownerUserId = null,
        int firstLineNumber = 0,
        IReadOnlyList<TranscriptSourceLine>? acceptedSourceLines = null,
        IReadOnlyList<RejectedTranscriptSourceLine>? rejectedSourceLines = null,
        bool inferOmittedSourceLines = false,
        CancellationToken ct = default);
}

public interface IMachineRepository {
    /// <summary>True when the machine_id was inserted. False when that id is already enrolled — the stored credential is left untouched.</summary>
    Task<bool> EnrollAsync(string machineId, string hostname, string os, string arch, string tokenHash, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Updates last_heartbeat for the machine owning tokenHash; returns its machine_id, or null if the token matches no machine.</summary>
    Task<string?> HeartbeatAsync(string tokenHash, DateTimeOffset now, CancellationToken ct = default);
}
