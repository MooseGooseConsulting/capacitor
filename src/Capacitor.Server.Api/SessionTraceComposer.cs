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

        foreach (var @event in OrderForPresentation(source)) {
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

    public static IReadOnlyList<SessionTurnSummary> SummarizeTurns(IEnumerable<SessionEventRecord> source) =>
        GroupTurns(source)
            .Select((events, index) => new SessionTurnSummary(
                index,
                events.FirstOrDefault(@event => string.Equals(@event.EventType, "UserMessage", StringComparison.OrdinalIgnoreCase))?.Content,
                events.Where(@event => !string.IsNullOrWhiteSpace(@event.ToolName))
                    .Select(@event => new SessionTurnTool(@event.ToolName!))
                    .DistinctBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                [],
                SumObservedTokens(events),
                events.Min(@event => @event.Timestamp),
                events.Max(@event => @event.Timestamp),
                null))
            .ToArray();

    public static SessionTurnDetail? GetTurn(IEnumerable<SessionEventRecord> source, int turnIndex) {
        var turns = GroupTurns(source);
        if (turnIndex < 0 || turnIndex >= turns.Count) return null;

        var trace = turns[turnIndex].Select(@event => new SessionTurnTraceEntry(
            TraceKind(@event),
            @event.AgentId,
            @event.Content,
            @event.ToolName,
            @event.ToolInput,
            @event.ToolOutput ?? @event.Content,
            @event.IsError)).ToArray();
        return new SessionTurnDetail(trace);
    }

    private static List<List<SessionEventRecord>> GroupTurns(IEnumerable<SessionEventRecord> source) {
        var turns = new List<List<SessionEventRecord>>();
        List<SessionEventRecord>? active = null;
        foreach (var @event in OrderForPresentation(source)) {
            if (string.Equals(@event.EventType, "UserMessage", StringComparison.OrdinalIgnoreCase)) {
                if (active is { Count: > 0 }) turns.Add(active);
                active = [@event];
            } else if (active is not null) {
                active.Add(@event);
            }
        }

        if (active is { Count: > 0 }) turns.Add(active);
        return turns;
    }

    // Line numbers are per agent stream. A parent line 1 and a subagent line 0
    // cannot establish a cross-agent order, while their captured timestamps can.
    // Agent and source coordinates only break genuinely simultaneous events.
    private static IOrderedEnumerable<SessionEventRecord> OrderForPresentation(IEnumerable<SessionEventRecord> source) =>
        source.OrderBy(@event => @event.Timestamp)
            .ThenBy(@event => @event.AgentId, StringComparer.Ordinal)
            .ThenBy(@event => @event.LineNumber)
            .ThenBy(@event => @event.LogicalSeq);

    private static string TraceKind(SessionEventRecord @event) => @event.EventType switch {
        "UserMessage" => "user_message",
        "AssistantTurn" or "AssistantMessage" => "assistant_message",
        "AssistantThinking" => "assistant_thinking",
        "ToolCall" => "tool_invocation",
        "ToolResult" => "tool_result",
        _ => "event"
    };

    private static void CompleteTurn(
        ICollection<SessionTraceEntry> entries,
        ref List<SessionEventRecord>? activeTurn,
        ref int turnIndex) {
        if (activeTurn is not { Count: > 0 } events) return;

        var startedAt = events.Min(e => e.Timestamp);
        var endedAt = events.Max(e => e.Timestamp);
        entries.Add(new SessionTraceEntry("turn", new SessionTraceTurn(
            turnIndex,
            startedAt,
            endedAt,
            Math.Max(0, (long)(endedAt - startedAt).TotalMilliseconds),
            SumObserved(events.Select(e => e.InputTokens)),
            SumObserved(events.Select(e => e.OutputTokens)),
            SumObserved(events.Select(e => e.CacheReadTokens)),
            SumObserved(events.Select(e => e.CacheWriteTokens)),
            SumObserved(events.Select(e => e.CostUsd)),
            events.Count(e => !string.IsNullOrEmpty(e.ToolName) || e.EventType is "ToolCall" or "ToolResult"),
            events), null));
        turnIndex++;
        activeTurn = null;
    }

    private static long? SumObservedTokens(IEnumerable<SessionEventRecord> events) =>
        SumObserved(events.SelectMany(@event => new[] {
            @event.InputTokens,
            @event.OutputTokens,
            @event.CacheReadTokens,
            @event.CacheWriteTokens
        }));

    private static long? SumObserved(IEnumerable<long?> values) {
        long? total = null;
        foreach (var value in values) {
            if (value is { } observed) total = (total ?? 0) + observed;
        }
        return total;
    }

    private static decimal? SumObserved(IEnumerable<decimal?> values) {
        decimal? total = null;
        foreach (var value in values) {
            if (value is { } observed) total = (total ?? 0m) + observed;
        }
        return total;
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
    [property: JsonPropertyName("input_tokens")] long? InputTokens,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens,
    [property: JsonPropertyName("cache_read_tokens")] long? CacheReadTokens,
    [property: JsonPropertyName("cache_write_tokens")] long? CacheWriteTokens,
    [property: JsonPropertyName("cost_usd")] decimal? CostUsd,
    [property: JsonPropertyName("tool_count")] int ToolCount,
    [property: JsonPropertyName("events")] IReadOnlyList<SessionEventRecord> Events);

public sealed record SessionTurnSummary(
    [property: JsonPropertyName("turn_index")] int TurnIndex,
    [property: JsonPropertyName("user_prompt")] string? UserPrompt,
    [property: JsonPropertyName("tools")] IReadOnlyList<SessionTurnTool> Tools,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files,
    [property: JsonPropertyName("total_tokens")] long? TotalTokens,
    [property: JsonPropertyName("first_event_at")] DateTimeOffset FirstEventAt,
    [property: JsonPropertyName("last_event_at")] DateTimeOffset LastEventAt,
    [property: JsonPropertyName("prose")] string? Prose);

public sealed record SessionTurnTool([property: JsonPropertyName("name")] string Name);

public sealed record SessionTurnDetail([property: JsonPropertyName("trace")] IReadOnlyList<SessionTurnTraceEntry> Trace);

public sealed record SessionTurnTraceEntry(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("arguments")] string? Arguments,
    [property: JsonPropertyName("output")] string? Output,
    [property: JsonPropertyName("is_error")] bool IsError);
