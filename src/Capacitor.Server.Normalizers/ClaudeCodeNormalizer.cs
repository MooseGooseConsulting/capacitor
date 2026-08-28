using System.Text.Json;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Normalizers;

public class ClaudeCodeNormalizer : INormalizer {
    public string VendorKey => "claude";

    public bool CanNormalize(string vendor) =>
        string.Equals(vendor, "claude", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(vendor, "claude-code", StringComparison.OrdinalIgnoreCase);

    public SessionEventRecord NormalizeLine(string sessionId, string? agentId, int lineNumber, string rawLine) {
        var now = DateTimeOffset.UtcNow;
        var eventType = "RawMessage";
        string? model = null;
        long inputTokens = 0;
        long outputTokens = 0;
        long cacheRead = 0;
        long cacheWrite = 0;
        decimal costUsd = 0m;
        string? toolName = null;
        string? toolInput = null;
        string? toolOutput = null;
        int? toolExitCode = null;
        var isError = false;
        string? content = null;

        try {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp)) {
                var typeStr = typeProp.GetString() ?? "";
                if (typeStr.Equals("user", StringComparison.OrdinalIgnoreCase)) {
                    eventType = "UserMessage";
                    if (root.TryGetProperty("message", out var msg)) content = msg.GetString();
                } else if (typeStr.Equals("assistant", StringComparison.OrdinalIgnoreCase)) {
                    eventType = "AssistantTurn";
                    if (root.TryGetProperty("model", out var m)) model = m.GetString();
                    if (root.TryGetProperty("content", out var c)) content = c.GetString();
                } else if (typeStr.Equals("tool_use", StringComparison.OrdinalIgnoreCase)) {
                    eventType = "ToolCall";
                    if (root.TryGetProperty("name", out var tn)) toolName = tn.GetString();
                    if (root.TryGetProperty("input", out var ti)) toolInput = ti.GetRawText();
                } else if (typeStr.Equals("tool_result", StringComparison.OrdinalIgnoreCase)) {
                    eventType = "ToolResult";
                    if (root.TryGetProperty("content", out var tc)) toolOutput = tc.GetString();
                    if (root.TryGetProperty("is_error", out var ie)) isError = ie.GetBoolean();
                    if (root.TryGetProperty("exit_code", out var ec) && ec.TryGetInt32(out var ecVal)) toolExitCode = ecVal;
                }
            }

            if (root.TryGetProperty("usage", out var usageProp)) {
                if (usageProp.TryGetProperty("input_tokens", out var inTok)) inputTokens = inTok.GetInt64();
                if (usageProp.TryGetProperty("output_tokens", out var outTok)) outputTokens = outTok.GetInt64();
                if (usageProp.TryGetProperty("cache_read_input_tokens", out var crTok)) cacheRead = crTok.GetInt64();
                if (usageProp.TryGetProperty("cache_creation_input_tokens", out var cwTok)) cacheWrite = cwTok.GetInt64();
                if (usageProp.TryGetProperty("cost_usd", out var costProp)) costUsd = costProp.GetDecimal();
            }
        } catch {
            content = rawLine;
        }

        return new SessionEventRecord {
            SessionId = sessionId,
            AgentId = agentId ?? string.Empty,
            LineNumber = lineNumber,
            EventType = eventType,
            Vendor = VendorKey,
            Model = model,
            Timestamp = now,
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
}
