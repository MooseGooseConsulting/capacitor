using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Ingest;

public interface IEventStoreRepository {
    Task<int> AppendEventsAsync(IReadOnlyList<SessionEventRecord> events, CancellationToken ct = default);
    Task<IReadOnlyList<SessionEventRecord>> GetEventsAsync(string sessionId, string? agentId = null, int fromLine = 0, CancellationToken ct = default);
    Task<long> GetEventCountAsync(string sessionId, CancellationToken ct = default);
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
    Task UpdateSessionAsync(SessionHeaderRecord session, CancellationToken ct = default);

    // Owner/visibility only. A concurrent session-end can commit completed between the
    // session-start handler's read and this write; a full-row UpdateSessionAsync would
    // resurrect the stale active status.
    Task PatchSessionStartAsync(string sessionId, string? ownerUserId, string? defaultVisibility, CancellationToken ct = default);

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

    Task PersistEvalRunAsync(EvalRunRecord run, IReadOnlyList<EvalVerdictRecord> verdicts, CancellationToken ct = default);
}

public interface ITranscriptIngest {
    Task<int> IngestAsync(IReadOnlyList<SessionEventRecord> events, string? ownerUserId = null, CancellationToken ct = default);
}
