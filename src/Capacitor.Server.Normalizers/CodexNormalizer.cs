using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Normalizers;

/// <summary>
/// Projects Codex rollout JSONL into the canonical session-event stream. Codex
/// records are wrapped in a top-level <c>response_item</c> (or <c>event_msg</c>)
/// envelope; they are not ACP notifications and must not fall through to the
/// ACP normalizer.
/// </summary>
public sealed class CodexNormalizer : INormalizer {
    public string VendorKey => "codex";

    public bool CanNormalize(string vendor) =>
        string.Equals(vendor, VendorKey, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SessionEventRecord> NormalizeLine(
        string vendor, string sessionId, string? agentId, int lineNumber, string rawLine) {
        var timestamp = DateTimeOffset.UtcNow;

        try {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            timestamp = ReadTimestamp(root, timestamp);

            return root.Str("type") switch {
                "response_item" when root.Obj("payload") is { } payload =>
                    NormalizeResponseItem(sessionId, agentId, lineNumber, rawLine, timestamp, root, payload),
                "event_msg" when root.Obj("payload") is { } payload =>
                    NormalizeEventMessage(sessionId, agentId, lineNumber, rawLine, timestamp, root, payload),
                _ => [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "RawMessage",
                    eventId: root.Str("id"), itemId: root.Str("id"))]
            };
        } catch (JsonException) {
            return [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "RawMessage", content: rawLine)];
        }
    }

    static IReadOnlyList<SessionEventRecord> NormalizeResponseItem(
        string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp,
        JsonElement root, JsonElement payload) {
        var itemId = payload.Str("id") ?? payload.Str("call_id");
        var eventId = root.Str("id") ?? itemId;

        return payload.Str("type") switch {
            "message" => NormalizeMessage(sessionId, agentId, lineNumber, rawLine, timestamp, payload, eventId, itemId),
            "reasoning" => NormalizeReasoning(sessionId, agentId, lineNumber, rawLine, timestamp, payload, eventId, itemId),
            "function_call" or "custom_tool_call" => [Frame(
                sessionId, agentId, lineNumber, rawLine, timestamp, "ToolCall",
                eventId: eventId, itemId: itemId,
                toolName: payload.Str("name") ?? payload.Str("type"),
                toolInput: Content(payload, "arguments") ?? Content(payload, "input"),
                model: payload.Str("model"))],
            "function_call_output" or "custom_tool_call_output" => [Frame(
                sessionId, agentId, lineNumber, rawLine, timestamp, "ToolResult",
                eventId: eventId, itemId: itemId,
                toolOutput: Content(payload, "output") ?? Content(payload, "result") ?? Content(payload, "error"),
                toolExitCode: ToInt(payload.Num("exit_code") ?? payload.Num("exitCode")),
                isError: payload.Bool("is_error") == true || payload.Bool("isError") == true
                      || payload.Str("status") is "failed" or "error")],
            _ => [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "RawMessage", eventId: eventId, itemId: itemId)]
        };
    }

    static IReadOnlyList<SessionEventRecord> NormalizeMessage(
        string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp,
        JsonElement payload, string? eventId, string? itemId) {
        var role = payload.Str("role");
        if (role is not ("user" or "assistant"))
            return [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "RawMessage", eventId: eventId, itemId: itemId)];

        var usage = ReadUsage(payload.Obj("usage"));
        var model = payload.Str("model");
        var result = new List<SessionEventRecord>();
        var usageAttached = false;

        SessionEventRecord Emit(string type, string? content = null) {
            var attachUsage = !usageAttached;
            usageAttached = true;
            return Frame(sessionId, agentId, lineNumber, rawLine, timestamp, type,
                eventId: eventId, itemId: itemId, model: model, content: content,
                inputTokens: attachUsage ? usage.InputTokens : 0,
                outputTokens: attachUsage ? usage.OutputTokens : 0,
                cacheReadTokens: attachUsage ? usage.CacheReadTokens : 0,
                cacheWriteTokens: attachUsage ? usage.CacheWriteTokens : 0,
                reasoningTokens: attachUsage ? usage.ReasoningTokens : null,
                costUsd: attachUsage ? usage.CostUsd : 0m);
        }

        if (payload.Arr("content") is { } blocks) {
            foreach (var block in blocks.EnumerateArray()) {
                var text = block.Str("text");
                switch (block.Str("type")) {
                    case "input_text" or "text" when role == "user" && text is not null:
                        result.Add(Emit("UserMessage", text));
                        break;
                    case "output_text" or "text" or "refusal" when role == "assistant" && text is not null:
                        result.Add(Emit("AssistantTurn", text));
                        break;
                    case "reasoning" when role == "assistant" && text is not null:
                        result.Add(Emit("AssistantThinking", text));
                        break;
                }
            }
        } else if (payload.Str("content") is { } scalar) {
            result.Add(Emit(role == "user" ? "UserMessage" : "AssistantTurn", scalar));
        }

        if (result.Count == 0)
            result.Add(Emit("RawMessage"));
        return result;
    }

    static IReadOnlyList<SessionEventRecord> NormalizeReasoning(
        string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp,
        JsonElement payload, string? eventId, string? itemId) {
        var result = new List<SessionEventRecord>();
        var model = payload.Str("model");
        var usage = ReadUsage(payload.Obj("usage"));
        var usageAttached = false;

        SessionEventRecord Emit(string? content) {
            var attachUsage = !usageAttached;
            usageAttached = true;
            return Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "AssistantThinking",
                eventId: eventId, itemId: itemId, model: model, content: content,
                inputTokens: attachUsage ? usage.InputTokens : 0,
                outputTokens: attachUsage ? usage.OutputTokens : 0,
                cacheReadTokens: attachUsage ? usage.CacheReadTokens : 0,
                cacheWriteTokens: attachUsage ? usage.CacheWriteTokens : 0,
                reasoningTokens: attachUsage ? usage.ReasoningTokens : null,
                costUsd: attachUsage ? usage.CostUsd : 0m);
        }

        if (payload.Arr("summary") is { } summary) {
            foreach (var block in summary.EnumerateArray()) {
                var text = block.Str("text") ?? (block.IsString ? block.GetString() : null);
                if (text is not null) result.Add(Emit(text));
            }
        }
        if (result.Count == 0 && payload.Arr("content") is { } content) {
            foreach (var block in content.EnumerateArray()) {
                var text = block.Str("text") ?? (block.IsString ? block.GetString() : null);
                if (text is not null) result.Add(Emit(text));
            }
        }
        if (result.Count == 0)
            result.Add(Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "RawMessage", eventId: eventId, itemId: itemId));
        return result;
    }

    static IReadOnlyList<SessionEventRecord> NormalizeEventMessage(
        string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp,
        JsonElement root, JsonElement payload) {
        var eventId = root.Str("id") ?? payload.Str("id");
        if (payload.Str("type") != "token_count")
            return [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "RawMessage", eventId: eventId, itemId: payload.Str("item_id"))];

        var info = payload.Obj("info");
        var usage = info is { } infoValue
            ? infoValue.Obj("total_token_usage") ?? infoValue.Obj("usage")
            : null;
        usage ??= payload.Obj("total_token_usage") ?? payload.Obj("usage");
        var parsed = ReadUsage(usage);
        var model = payload.Str("model") ?? (info is { } infoModel ? infoModel.Str("model") : null);

        return [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "UsageBackfill",
            eventId: eventId, itemId: payload.Str("item_id"), model: model,
            inputTokens: parsed.InputTokens, outputTokens: parsed.OutputTokens,
            cacheReadTokens: parsed.CacheReadTokens, cacheWriteTokens: parsed.CacheWriteTokens,
            reasoningTokens: parsed.ReasoningTokens, costUsd: parsed.CostUsd)];
    }

    static Usage ReadUsage(JsonElement? usage) {
        if (usage is not { } value) return default;
        return new Usage(
            value.Num("input_tokens") ?? 0,
            value.Num("output_tokens") ?? 0,
            value.Num("cached_input_tokens") ?? value.Num("cache_read_input_tokens") ?? 0,
            value.Num("cache_write_input_tokens") ?? value.Num("cache_creation_input_tokens") ?? 0,
            value.Num("reasoning_output_tokens") ?? value.Num("reasoning_tokens"),
            Decimal(value, "cost_usd"));
    }

    static DateTimeOffset ReadTimestamp(JsonElement root, DateTimeOffset fallback) =>
        TryParseTimestamp(root.Str("timestamp"))
        ?? TryParseTimestamp(root.Str("created_at"))
        ?? fallback;

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

    static decimal Decimal(JsonElement value, string property) =>
        value.Prop(property) is { } number && number.ValueKind == JsonValueKind.Number && number.TryGetDecimal(out var result)
            ? result
            : 0m;

    static int? ToInt(long? value) =>
        value is { } number && number >= int.MinValue && number <= int.MaxValue ? (int)number : null;

    static SessionEventRecord Frame(
        string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, string eventType,
        string? eventId = null, string? itemId = null, string? model = null, string? content = null,
        string? toolName = null, string? toolInput = null, string? toolOutput = null, int? toolExitCode = null,
        bool isError = false, long inputTokens = 0, long outputTokens = 0, long cacheReadTokens = 0,
        long cacheWriteTokens = 0, long? reasoningTokens = null, decimal costUsd = 0m) =>
        new() {
            SessionId = sessionId,
            AgentId = agentId ?? string.Empty,
            LineNumber = lineNumber,
            EventId = eventId,
            EventType = eventType,
            Vendor = "codex",
            Model = model,
            Timestamp = timestamp,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            CacheWriteTokens = cacheWriteTokens,
            ReasoningTokens = reasoningTokens,
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
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        long? ReasoningTokens,
        decimal CostUsd);
}
