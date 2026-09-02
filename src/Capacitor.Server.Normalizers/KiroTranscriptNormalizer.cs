using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Normalizers;

/// <summary>
/// Projects AWS Kiro CLI JSONL records into canonical events. Kiro's historical
/// importer and live watcher both send the native <c>{ version, kind, data }</c>
/// records, with a synthetic <c>KiroUsageBackfilled</c> record when the sidecar
/// metadata arrives after a live transcript line.
/// </summary>
public sealed class KiroTranscriptNormalizer : INormalizer {
    public string VendorKey => "kiro";

    public bool CanNormalize(string vendor) =>
        string.Equals(vendor, VendorKey, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SessionEventRecord> NormalizeLine(
        string vendor, string sessionId, string? agentId, int lineNumber, string rawLine) {
        var timestamp = DateTimeOffset.UtcNow;

        try {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            timestamp = ReadTimestamp(root, timestamp);
            var data = root.Obj("data");

            return root.Str("kind") switch {
                "Prompt" when data is { } value => NormalizeContent(
                    sessionId, agentId, lineNumber, rawLine, timestamp, "Prompt", value),
                "AssistantMessage" when data is { } value => NormalizeContent(
                    sessionId, agentId, lineNumber, rawLine, timestamp, "AssistantMessage", value),
                "ToolResults" when data is { } value => NormalizeContent(
                    sessionId, agentId, lineNumber, rawLine, timestamp, "ToolResults", value),
                "KiroUsageBackfilled" when data is { } value => NormalizeUsageBackfill(
                    sessionId, agentId, lineNumber, rawLine, timestamp, value),
                _ => [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "RawMessage",
                    eventId: root.Str("id"), itemId: data is { } value ? value.Str("message_id") ?? value.Str("id") : null)]
            };
        } catch (JsonException) {
            return [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "RawMessage", content: rawLine)];
        }
    }

    static IReadOnlyList<SessionEventRecord> NormalizeContent(
        string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp,
        string kind, JsonElement data) {
        var itemId = data.Str("message_id") ?? data.Str("id");
        var eventId = itemId is null ? null : $"{kind}:{itemId}";
        var usage = ReadUsage(data);
        var result = new List<SessionEventRecord>();
        var usageAttached = false;

        SessionEventRecord Emit(
            string eventType, string? content = null, string? toolName = null, string? toolInput = null,
            string? toolOutput = null, int? toolExitCode = null, bool isError = false) {
            var attachUsage = !usageAttached;
            usageAttached = true;
            return Frame(sessionId, agentId, lineNumber, rawLine, timestamp, eventType,
                eventId: eventId, itemId: itemId, model: usage.Model, content: content,
                toolName: toolName, toolInput: toolInput, toolOutput: toolOutput, toolExitCode: toolExitCode,
                isError: isError, inputTokens: attachUsage ? usage.InputTokens : null,
                outputTokens: attachUsage ? usage.OutputTokens : null,
                cacheReadTokens: attachUsage ? usage.CacheReadTokens : null,
                cacheWriteTokens: attachUsage ? usage.CacheWriteTokens : null,
                reasoningTokens: attachUsage ? usage.ReasoningTokens : null,
                contextUsedTokens: attachUsage ? usage.ContextUsedTokens : null,
                contextWindowTokens: attachUsage ? usage.ContextWindowTokens : null,
                costUsd: attachUsage ? usage.CostUsd : null);
        }

        if (data.Arr("content") is { } blocks) {
            foreach (var block in blocks.EnumerateArray()) {
                switch (block.Str("kind")) {
                    case "text": {
                        var text = Content(block, "data");
                        if (text is not null)
                            result.Add(Emit(kind switch {
                                "Prompt" => "UserMessage",
                                "AssistantMessage" => "AssistantTurn",
                                _ => "ToolResult"
                            }, content: kind == "ToolResults" ? null : text,
                                toolOutput: kind == "ToolResults" ? text : null));
                        break;
                    }
                    case "thinking" or "reasoning": {
                        var text = Content(block, "data");
                        if (text is not null) result.Add(Emit("AssistantThinking", content: text));
                        break;
                    }
                    case "toolUse" or "tool_use":
                        result.Add(Emit("ToolCall",
                            toolName: ToolName(block),
                            toolInput: ToolInput(block)));
                        break;
                    case "toolResult" or "tool_result":
                        result.Add(Emit("ToolResult",
                            toolOutput: ToolOutput(block),
                            toolExitCode: ToolExitCode(block),
                            isError: IsToolError(block)));
                        break;
                }
            }
        }

        if (result.Count == 0 && Content(data, "content") is { } scalar) {
            result.Add(Emit(kind switch {
                "Prompt" => "UserMessage",
                "AssistantMessage" => "AssistantTurn",
                _ => "ToolResult"
            }, content: kind == "ToolResults" ? null : scalar,
                toolOutput: kind == "ToolResults" ? scalar : null));
        }

        if (result.Count == 0)
            result.Add(Emit("RawMessage"));
        return result;
    }

    static IReadOnlyList<SessionEventRecord> NormalizeUsageBackfill(
        string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, JsonElement data) {
        var usage = ReadUsage(data);
        var itemId = data.Str("message_id") ?? data.Str("id");
        return [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "UsageBackfill",
            eventId: itemId is null ? null : $"KiroUsageBackfilled:{itemId}", itemId: itemId, model: usage.Model,
            inputTokens: usage.InputTokens, outputTokens: usage.OutputTokens,
            cacheReadTokens: usage.CacheReadTokens, cacheWriteTokens: usage.CacheWriteTokens,
            reasoningTokens: usage.ReasoningTokens, contextUsedTokens: usage.ContextUsedTokens,
            contextWindowTokens: usage.ContextWindowTokens, costUsd: usage.CostUsd)];
    }

    static Usage ReadUsage(JsonElement data) {
        var stamped = data.Obj("_kcap_usage");
        var usage = stamped ?? data.Obj("usage");
        var source = usage ?? data;

        return new Usage(
            source.Num("input_token_count") ?? source.Num("input_tokens"),
            source.Num("output_token_count") ?? source.Num("output_tokens"),
            source.Num("cache_read_tokens"),
            source.Num("cache_write_tokens"),
            source.Num("reasoning_tokens"),
            source.Num("context_used_tokens"),
            source.Num("context_window_tokens"),
            source.Str("model") ?? data.Str("model") ?? data.Str("model_id"),
            Decimal(source, "cost_usd"));
    }

    static string? ToolName(JsonElement block) {
        var data = block.Obj("data");
        return data is { } value
            ? value.Str("name") ?? value.Str("tool_name") ?? block.Str("name") ?? block.Str("tool_name")
            : block.Str("name") ?? block.Str("tool_name");
    }

    static string? ToolInput(JsonElement block) {
        var data = block.Obj("data");
        return data is { } value
            ? Content(value, "input") ?? Content(value, "arguments") ?? value.GetRawText()
            : Content(block, "data");
    }

    static string? ToolOutput(JsonElement block) {
        var data = block.Obj("data");
        return data is { } value
            ? Content(value, "output") ?? Content(value, "content") ?? Content(value, "result")
              ?? Content(value, "error") ?? value.GetRawText()
            : Content(block, "data");
    }

    static int? ToolExitCode(JsonElement block) {
        var data = block.Obj("data");
        return ToInt(data is { } value
            ? value.Num("exit_code") ?? value.Num("exitCode") ?? block.Num("exit_code") ?? block.Num("exitCode")
            : block.Num("exit_code") ?? block.Num("exitCode"));
    }

    static bool IsToolError(JsonElement block) {
        var data = block.Obj("data");
        return (data is { } value && (value.Bool("is_error") == true || value.Bool("isError") == true
                                      || value.Str("status") is "failed" or "error"))
            || block.Bool("is_error") == true || block.Bool("isError") == true
            || block.Str("status") is "failed" or "error";
    }

    static DateTimeOffset ReadTimestamp(JsonElement root, DateTimeOffset fallback) {
        var data = root.Obj("data");
        return TryParseTimestamp(root.Str("timestamp"))
            ?? TryParseTimestamp(root.Str("created_at"))
            ?? (data is { } value ? TryParseTimestamp(value.Str("timestamp")) ?? TryParseTimestamp(value.Str("created_at")) : null)
            ?? fallback;
    }

    static DateTimeOffset? TryParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp
            : null;

    static string? Content(JsonElement value, string property) {
        var content = value.Prop(property);
        return content is not { } item || item.IsNull
            ? null
            : item.IsString ? item.GetString() : item.GetRawText();
    }

    static decimal? Decimal(JsonElement value, string property) =>
        value.Prop(property) is { } number && number.ValueKind == JsonValueKind.Number && number.TryGetDecimal(out var result)
            ? result
            : null;

    static int? ToInt(long? value) =>
        value is { } number && number >= int.MinValue && number <= int.MaxValue ? (int)number : null;

    static SessionEventRecord Frame(
        string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, string eventType,
        string? eventId = null, string? itemId = null, string? model = null, string? content = null,
        string? toolName = null, string? toolInput = null, string? toolOutput = null, int? toolExitCode = null,
        bool isError = false, long? inputTokens = null, long? outputTokens = null, long? cacheReadTokens = null,
        long? cacheWriteTokens = null, long? reasoningTokens = null, long? contextUsedTokens = null,
        long? contextWindowTokens = null, decimal? costUsd = null) =>
        new() {
            SessionId = sessionId,
            AgentId = agentId ?? string.Empty,
            LineNumber = lineNumber,
            EventId = eventId,
            EventType = eventType,
            Vendor = "kiro",
            Model = model,
            Timestamp = timestamp,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            CacheWriteTokens = cacheWriteTokens,
            ReasoningTokens = reasoningTokens,
            ContextUsedTokens = contextUsedTokens,
            ContextWindowTokens = contextWindowTokens,
            CostUsd = costUsd,
            ItemId = itemId,
            ToolName = toolName,
            ToolInput = toolInput,
            ToolOutput = toolOutput,
            ToolExitCode = toolExitCode,
            IsError = isError,
            Content = content,
            RawPayload = rawLine
        };

    readonly record struct Usage(
        long? InputTokens,
        long? OutputTokens,
        long? CacheReadTokens,
        long? CacheWriteTokens,
        long? ReasoningTokens,
        long? ContextUsedTokens,
        long? ContextWindowTokens,
        string? Model,
        decimal? CostUsd);
}
