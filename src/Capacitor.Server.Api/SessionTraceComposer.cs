using System.Text.Json.Serialization;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Api;

/// <summary>
/// Produces the Trace tab's ordered mixture of canonical turns and top-level
/// events. A user message starts a turn; lifecycle and other records that
/// precede the first user message remain visible as individual trace entries.
/// </summary>
public static class SessionTraceComposer {
    public static SessionTraceDocument Compose(IEnumerable<SessionEventRecord> source) {
        var entries = new List<SessionTraceEntry>();
        List<SessionEventRecord>? activeTurn = null;
        var turnIndex = 0;

        foreach (var @event in source
                     .OrderBy(e => e.LineNumber)
                     .ThenBy(e => e.LogicalSeq)
                     .ThenBy(e => e.AgentId, StringComparer.Ordinal)) {
            if (string.Equals(@event.EventType, "UserMessage", StringComparison.OrdinalIgnoreCase)) {
                CompleteTurn(entries, ref activeTurn, ref turnIndex);
                activeTurn = [@event];
            } else if (activeTurn is not null) {
                activeTurn.Add(@event);
            } else {
                entries.Add(new SessionTraceEntry("event", null, @event));
            }
        }

        CompleteTurn(entries, ref activeTurn, ref turnIndex);
        return new SessionTraceDocument(entries);
    }

    private static void CompleteTurn(
        ICollection<SessionTraceEntry> entries,
        ref List<SessionEventRecord>? activeTurn,
        ref int turnIndex) {
        if (activeTurn is not { Count: > 0 } events) return;

        turnIndex++;
        var startedAt = events.Min(e => e.Timestamp);
        var endedAt = events.Max(e => e.Timestamp);
        entries.Add(new SessionTraceEntry("turn", new SessionTraceTurn(
            turnIndex,
            startedAt,
            endedAt,
            Math.Max(0, (long)(endedAt - startedAt).TotalMilliseconds),
            events.Sum(e => e.InputTokens),
            events.Sum(e => e.OutputTokens),
            events.Sum(e => e.CacheReadTokens),
            events.Sum(e => e.CacheWriteTokens),
            events.Sum(e => e.CostUsd),
            events.Count(e => !string.IsNullOrEmpty(e.ToolName) || e.EventType is "ToolCall" or "ToolResult"),
            events), null));
        activeTurn = null;
    }
}

public sealed record SessionTraceDocument(
    [property: JsonPropertyName("entries")] IReadOnlyList<SessionTraceEntry> Entries);

public sealed record SessionTraceEntry(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("turn")] SessionTraceTurn? Turn,
    [property: JsonPropertyName("event")] SessionEventRecord? Event);

public sealed record SessionTraceTurn(
    [property: JsonPropertyName("turn_index")] int TurnIndex,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("ended_at")] DateTimeOffset EndedAt,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("input_tokens")] long InputTokens,
    [property: JsonPropertyName("output_tokens")] long OutputTokens,
    [property: JsonPropertyName("cache_read_tokens")] long CacheReadTokens,
    [property: JsonPropertyName("cache_write_tokens")] long CacheWriteTokens,
    [property: JsonPropertyName("cost_usd")] decimal CostUsd,
    [property: JsonPropertyName("tool_count")] int ToolCount,
    [property: JsonPropertyName("events")] IReadOnlyList<SessionEventRecord> Events);
