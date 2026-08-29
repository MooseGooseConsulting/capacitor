using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Ingest;

public sealed class TranscriptIngestEngine : ITranscriptIngest {
    private readonly IEventStoreRepository _events;
    private readonly ISessionWatermarkRepository _watermarks;
    private readonly ISessionRepository _sessions;

    public TranscriptIngestEngine(
        IEventStoreRepository events,
        ISessionWatermarkRepository watermarks,
        ISessionRepository sessions) {
        _events = events;
        _watermarks = watermarks;
        _sessions = sessions;
    }

    public async Task<int> IngestAsync(IReadOnlyList<SessionEventRecord> events, string? ownerUserId = null, CancellationToken ct = default) {
        if (events.Count == 0) return 0;

        foreach (var session in events.GroupBy(e => e.SessionId, StringComparer.Ordinal)) {
            var vendor = session.First().Vendor;
            await _sessions.GetOrCreatePlaceholderAsync(session.Key, vendor, ownerUserId, ct);
        }

        var inserted = await _events.AppendEventsAsync(events, ct);

        foreach (var stream in events.GroupBy(e => (sessionId: e.SessionId, agentId: e.AgentId ?? string.Empty))) {
            var lastLine = stream.Max(e => e.LineNumber);
            await _watermarks.UpdateWatermarkAsync(stream.Key.sessionId, stream.Key.agentId, lastLine, byteOffset: 0, ct);
        }

        return inserted;
    }
}
