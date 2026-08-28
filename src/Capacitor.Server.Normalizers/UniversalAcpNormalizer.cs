using System.Text.Json;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Normalizers;

public class UniversalAcpNormalizer : INormalizer {
    public string VendorKey => "acp";

    private static readonly HashSet<string> SupportedVendors = new(StringComparer.OrdinalIgnoreCase) {
        "acp", "cursor", "gemini", "opencode", "pi", "copilot"
    };

    public bool CanNormalize(string vendor) => SupportedVendors.Contains(vendor);

    public SessionEventRecord NormalizeLine(string sessionId, string? agentId, int lineNumber, string rawLine) {
        var now = DateTimeOffset.UtcNow;
        var eventType = "AcpFrame";
        string? model = null;
        long inputTokens = 0;
        long outputTokens = 0;
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

            if (root.TryGetProperty("method", out var methodProp)) {
                var method = methodProp.GetString() ?? "";
                if (method.Contains("tool/call", StringComparison.OrdinalIgnoreCase)) {
                    eventType = "ToolCall";
                    if (root.TryGetProperty("params", out var p) && p.TryGetProperty("name", out var n)) {
                        toolName = n.GetString();
                    }
                    if (root.TryGetProperty("params", out var p2) && p2.TryGetProperty("arguments", out var args)) {
                        toolInput = args.GetRawText();
                    }
                } else if (method.Contains("tool/result", StringComparison.OrdinalIgnoreCase)) {
                    eventType = "ToolResult";
                    if (root.TryGetProperty("result", out var r)) toolOutput = r.GetRawText();
                } else if (method.Contains("message", StringComparison.OrdinalIgnoreCase) || method.Contains("prompt", StringComparison.OrdinalIgnoreCase)) {
                    eventType = "UserMessage";
                    if (root.TryGetProperty("params", out var p3) && p3.TryGetProperty("content", out var c)) {
                        content = c.GetString();
                    }
                }
            } else if (root.TryGetProperty("result", out var resProp)) {
                eventType = "ToolResult";
                toolOutput = resProp.GetRawText();
            } else if (root.TryGetProperty("error", out var errProp)) {
                eventType = "ToolResult";
                isError = true;
                toolOutput = errProp.GetRawText();
                toolExitCode = 1;
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
