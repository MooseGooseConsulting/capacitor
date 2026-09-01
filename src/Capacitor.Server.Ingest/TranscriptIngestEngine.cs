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

    public async Task<int> IngestAsync(
        IReadOnlyList<SessionEventRecord> events,
        string? ownerUserId = null,
        int firstLineNumber = 0,
        CancellationToken ct = default) {
        if (events.Count == 0) return 0;

        foreach (var session in events.GroupBy(e => e.SessionId, StringComparer.Ordinal)) {
            var vendor = session.First().Vendor;
            await _sessions.GetOrCreatePlaceholderAsync(session.Key, vendor, ownerUserId, ct);
        }

        var inserted = await _events.AppendEventsAsync(events, ct);

        foreach (var stream in events.GroupBy(e => (sessionId: e.SessionId, agentId: e.AgentId ?? string.Empty))) {
            var stored = await _events.GetEventsAsync(stream.Key.sessionId, stream.Key.agentId, fromLine: 0, ct);
            var lastLine = ContiguousLastLine(stored, firstLineNumber);
            if (lastLine is int last) {
                await _watermarks.UpdateWatermarkAsync(stream.Key.sessionId, stream.Key.agentId, last, byteOffset: 0, ct);
            }
        }

        return inserted;
    }

    internal static int? ContiguousLastLine(IReadOnlyList<SessionEventRecord> stored, int firstLineNumber = 0) {
        var lines = new HashSet<int>(stored.Count);
        foreach (var ev in stored) {
            lines.Add(ev.LineNumber);
        }

        int? last = null;
        for (var n = firstLineNumber; lines.Contains(n); n++) {
            last = n;
        }

        return last;
    }
}
