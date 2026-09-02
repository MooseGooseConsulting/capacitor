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
        IReadOnlyList<TranscriptSourceLine>? acceptedSourceLines = null,
        CancellationToken ct = default) {
        if (events.Count == 0 && (acceptedSourceLines is null || acceptedSourceLines.Count == 0)) return 0;

        foreach (var session in events.GroupBy(e => e.SessionId, StringComparer.Ordinal)) {
            var vendor = session.First().Vendor;
            await _sessions.GetOrCreatePlaceholderAsync(session.Key, vendor, ownerUserId, ct);
        }

        var inserted = await _events.AppendEventsAsync(events, ct);

        var streams = events.Select(e => (SessionId: e.SessionId, AgentId: e.AgentId ?? string.Empty))
            .Concat((acceptedSourceLines ?? []).Select(line => (line.SessionId, line.AgentId)))
            .Distinct()
            .ToArray();
        foreach (var stream in streams) {
            var stored = await _events.GetEventsAsync(stream.SessionId, stream.AgentId, fromLine: 0, ct);
            var acceptedLines = acceptedSourceLines?
                .Where(line => line.SessionId == stream.SessionId && line.AgentId == stream.AgentId)
                .Select(line => line.LineNumber);
            var lastLine = ContiguousLastLine(stored, acceptedLines, firstLineNumber);
            if (lastLine is int last) {
                await _watermarks.UpdateWatermarkAsync(stream.SessionId, stream.AgentId, last, byteOffset: 0, ct);
            }
        }

        return inserted;
    }

    internal static int? ContiguousLastLine(IReadOnlyList<SessionEventRecord> stored, int firstLineNumber = 0) =>
        ContiguousLastLine(stored, acceptedSourceLines: null, firstLineNumber);

    internal static int? ContiguousLastLine(
        IReadOnlyList<SessionEventRecord> stored,
        IEnumerable<int>? acceptedSourceLines = null,
        int firstLineNumber = 0) {
        var lines = new HashSet<int>(stored.Count);
        foreach (var ev in stored) {
            lines.Add(ev.LineNumber);
        }
        if (acceptedSourceLines is not null) {
            foreach (var lineNumber in acceptedSourceLines) lines.Add(lineNumber);
        }

        int? last = null;
        for (var n = firstLineNumber; lines.Contains(n); n++) {
            last = n;
        }

        return last;
    }
}
