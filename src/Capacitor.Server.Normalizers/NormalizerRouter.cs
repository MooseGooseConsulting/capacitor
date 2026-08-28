using System.Text.Json;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Normalizers;

public class AntigravityNormalizer : INormalizer {
    public string VendorKey => "antigravity";

    public bool CanNormalize(string vendor) =>
        string.Equals(vendor, "antigravity", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(vendor, "agy", StringComparison.OrdinalIgnoreCase);

    public SessionEventRecord NormalizeLine(string sessionId, string? agentId, int lineNumber, string rawLine) {
        var now = DateTimeOffset.UtcNow;
        var eventType = "PlannerStep";
        string? model = null;
        string? toolName = null;
        string? toolInput = null;
        string? toolOutput = null;
        var isError = false;
        string? content = null;

        try {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp)) {
                var typeStr = typeProp.GetString() ?? "";
                if (typeStr.Equals("USER_INPUT", StringComparison.OrdinalIgnoreCase)) {
                    eventType = "UserMessage";
                    if (root.TryGetProperty("content", out var c)) content = c.GetString();
                } else if (typeStr.Equals("PLANNER_RESPONSE", StringComparison.OrdinalIgnoreCase)) {
                    eventType = "AssistantTurn";
                    if (root.TryGetProperty("thinking", out var th)) content = th.GetString();
                    if (root.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array && tc.GetArrayLength() > 0) {
                        var firstTool = tc[0];
                        eventType = "ToolCall";
                        if (firstTool.TryGetProperty("toolAction", out var ta)) toolName = ta.GetString();
                        toolInput = firstTool.GetRawText();
                    }
                }
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
            ToolName = toolName,
            ToolInput = toolInput,
            ToolOutput = toolOutput,
            IsError = isError,
            Content = content,
            RawPayload = rawLine
        };
    }
}

public class NormalizerRouter {
    private readonly List<INormalizer> _normalizers;

    public NormalizerRouter(IEnumerable<INormalizer>? normalizers = null) {
        _normalizers = normalizers?.ToList() ?? new List<INormalizer> {
            new ClaudeCodeNormalizer(),
            new UniversalAcpNormalizer(),
            new AntigravityNormalizer()
        };
    }

    public SessionEventRecord Normalize(string vendor, string sessionId, string? agentId, int lineNumber, string rawLine) {
        var normalizer = _normalizers.FirstOrDefault(n => n.CanNormalize(vendor))
                         ?? _normalizers.First(n => n.CanNormalize("acp"));

        return normalizer.NormalizeLine(sessionId, agentId, lineNumber, rawLine);
    }
}
