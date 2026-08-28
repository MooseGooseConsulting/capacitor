using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Normalizers;

// Claude Code's project transcript: one JSON record per line, `type` at the root
// ("user" or "assistant" only), the API message payload nested under `message`.
public class ClaudeCodeNormalizer : INormalizer {
    public string VendorKey => "claude";

    public bool CanNormalize(string vendor) =>
        string.Equals(vendor, "claude", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(vendor, "claude-code", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SessionEventRecord> NormalizeLine(string vendor, string sessionId, string? agentId, int lineNumber, string rawLine) {
        var timestamp = DateTimeOffset.UtcNow;

        try {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.Str("timestamp") is { } tsStr && DateTimeOffset.TryParse(tsStr, out var parsedTs)) timestamp = parsedTs;

            return root.Str("type") switch {
                "user" when root.Obj("message") is { } m => NormalizeUser(sessionId, agentId, lineNumber, rawLine, timestamp, m),
                "assistant" when root.Obj("message") is { } m => NormalizeAssistant(sessionId, agentId, lineNumber, rawLine, timestamp, m),
                _ => [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "RawMessage")],
            };
        } catch (JsonException) {
            return [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "RawMessage", content: rawLine)];
        }
    }

    List<SessionEventRecord> NormalizeUser(string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, JsonElement message) {
        var result = new List<SessionEventRecord>();

        if (message.Str("content") is { } text) {
            result.Add(Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "UserMessage", content: text));
            return result;
        }
        if (message.Arr("content") is not { } blocks) return result;

        string? userText = null;
        foreach (var block in blocks.EnumerateArray()) {
            switch (block.Str("type")) {
                case "text":
                    if (block.Str("text") is { } t) userText = userText is null ? t : userText + t;
                    break;
                case "tool_result":
                    result.Add(Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "ToolResult",
                        toolOutput: block.Str("content") ?? (block.Arr("content") is { } c ? c.GetRawText() : null),
                        isError: block.Bool("is_error") == true));
                    break;
            }
        }
        if (userText is not null) result.Insert(0, Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "UserMessage", content: userText));
        return result;
    }

    List<SessionEventRecord> NormalizeAssistant(string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, JsonElement message) {
        var result = new List<SessionEventRecord>();
        var model = message.Str("model");
        var (inputTokens, outputTokens, cacheRead, cacheWrite, costUsd) = ReadUsage(message);
        var usageAttached = false;

        SessionEventRecord Emit(string eventType, string? content = null, string? toolName = null, string? toolInput = null) {
            var record = Frame(sessionId, agentId, lineNumber, rawLine, timestamp, eventType,
                model: model, content: content, toolName: toolName, toolInput: toolInput,
                inputTokens: usageAttached ? 0 : inputTokens,
                outputTokens: usageAttached ? 0 : outputTokens,
                cacheRead: usageAttached ? 0 : cacheRead,
                cacheWrite: usageAttached ? 0 : cacheWrite,
                costUsd: usageAttached ? 0m : costUsd);
            usageAttached = true;
            return record;
        }

        if (message.Arr("content") is { } blocks) {
            foreach (var block in blocks.EnumerateArray()) {
                switch (block.Str("type")) {
                    case "text":
                        if (block.Str("text") is { } text) result.Add(Emit("AssistantTurn", content: text));
                        break;
                    case "thinking":
                        if (block.Str("thinking") is { } thinking) result.Add(Emit("AssistantThinking", content: thinking));
                        break;
                    case "tool_use":
                        result.Add(Emit("ToolCall",
                            toolName: block.Str("name"),
                            toolInput: block.Obj("input")?.GetRawText()));
                        break;
                }
            }
        }
        if (result.Count == 0) result.Add(Emit("AssistantTurn"));
        return result;
    }

    static (long input, long output, long cacheRead, long cacheWrite, decimal cost) ReadUsage(JsonElement message) {
        if (message.Obj("usage") is not { } usage) return (0, 0, 0, 0, 0m);
        var cost = usage.TryGetProperty("cost_usd", out var costProp) && costProp.ValueKind == JsonValueKind.Number ? costProp.GetDecimal() : 0m;
        return (
            usage.Num("input_tokens") ?? 0,
            usage.Num("output_tokens") ?? 0,
            usage.Num("cache_read_input_tokens") ?? 0,
            usage.Num("cache_creation_input_tokens") ?? 0,
            cost);
    }

    SessionEventRecord Frame(
            string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, string eventType,
            string? model = null, string? content = null, string? toolName = null, string? toolInput = null,
            string? toolOutput = null, int? toolExitCode = null, bool isError = false,
            long inputTokens = 0, long outputTokens = 0, long cacheRead = 0, long cacheWrite = 0, decimal costUsd = 0m) =>
        new SessionEventRecord {
            SessionId = sessionId,
            AgentId = agentId ?? string.Empty,
            LineNumber = lineNumber,
            EventType = eventType,
            Vendor = VendorKey,
            Model = model,
            Timestamp = timestamp,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            CostUsd = costUsd,
            ToolName = toolName,
            ToolInput = toolInput,
            ToolOutput = toolOutput,
            ToolExitCode = toolExitCode,
            IsError = isError,
            Content = content,
            RawPayload = rawLine
        };
}
