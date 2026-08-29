using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Normalizers;

public class AntigravityNormalizer : INormalizer {
    public string VendorKey => "antigravity";

    public bool CanNormalize(string vendor) =>
        string.Equals(vendor, "antigravity", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(vendor, "agy", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SessionEventRecord> NormalizeLine(string vendor, string sessionId, string? agentId, int lineNumber, string rawLine) {
        var timestamp = DateTimeOffset.UtcNow;

        try {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.Str("created_at") is { } createdAt && DateTimeOffset.TryParse(createdAt, out var parsedTs))
                timestamp = parsedTs;

            return root.Str("type") switch {
                "USER_INPUT" => [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "UserMessage",
                    content: StripUserRequest(root.Str("content")))],
                "PLANNER_RESPONSE" => NormalizePlannerResponse(sessionId, agentId, lineNumber, rawLine, timestamp, root),
                // The transcript's own result step for a completed tool call — content is the
                // tool's output, status ("DONE"/"ERROR") flags failure.
                "RUN_COMMAND" or "VIEW_FILE" or "LIST_DIRECTORY" or "CODE_ACTION" =>
                    [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "ToolResult",
                        toolOutput: root.Str("content"), isError: root.Str("status") == "ERROR")],
                // Synthetic line AntigravityGenMetadata emits from the sibling SQLite db for
                // server-side cost backfill — not a real transcript step.
                "USAGE" => [NormalizeUsage(sessionId, agentId, lineNumber, rawLine, timestamp, root)],
                _ => [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "PlannerStep")],
            };
        } catch (JsonException) {
            return [Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "PlannerStep", content: rawLine)];
        }
    }

    static List<SessionEventRecord> NormalizePlannerResponse(string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, JsonElement root) {
        var result = new List<SessionEventRecord>();
        if (root.Str("content") is { } content)
            result.Add(Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "AssistantTurn", content: content));
        if (root.Str("thinking") is { } thinking)
            result.Add(Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "AssistantThinking", content: thinking));
        if (root.Arr("tool_calls") is { } toolCalls)
            foreach (var tc in toolCalls.EnumerateArray())
                result.Add(Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "ToolCall",
                    toolName: tc.Str("name"), toolInput: tc.GetRawText()));
        if (result.Count == 0) result.Add(Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "PlannerStep"));
        return result;
    }

    static SessionEventRecord NormalizeUsage(string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset fallbackTimestamp, JsonElement root) {
        var timestamp = root.Str("created_at") is { } createdAt && DateTimeOffset.TryParse(createdAt, out var parsed)
            ? parsed
            : fallbackTimestamp;
        return Frame(sessionId, agentId, lineNumber, rawLine, timestamp, "UsageBackfill",
            model: root.Str("model"),
            inputTokens: root.Num("input_tokens") ?? 0,
            outputTokens: root.Num("output_tokens") ?? 0,
            cacheRead: root.Num("cache_read_tokens") ?? 0,
            cacheWrite: root.Num("cache_write_tokens") ?? 0);
    }

    static SessionEventRecord Frame(
            string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, string eventType,
            string? model = null, string? content = null, string? toolName = null, string? toolInput = null,
            string? toolOutput = null, bool isError = false,
            long inputTokens = 0, long outputTokens = 0, long cacheRead = 0, long cacheWrite = 0) =>
        new SessionEventRecord {
            SessionId = sessionId,
            AgentId = agentId ?? string.Empty,
            LineNumber = lineNumber,
            EventType = eventType,
            Vendor = "antigravity",
            Model = model,
            Timestamp = timestamp,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            ToolName = toolName,
            ToolInput = toolInput,
            ToolOutput = toolOutput,
            IsError = isError,
            Content = content,
            RawPayload = rawLine
        };

    static string? StripUserRequest(string? content) {
        if (content is null) return null;
        const string open = "<USER_REQUEST>", close = "</USER_REQUEST>";
        var start = content.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return content;
        start += open.Length;
        var end = content.IndexOf(close, start, StringComparison.Ordinal);
        var inner = (end < 0 ? content[start..] : content[start..end]).Trim();
        return inner.Length > 0 ? inner : null;
    }
}

public class NormalizerRouter {
    readonly List<INormalizer> _normalizers;

    // Built-ins are always present regardless of what the container hands in: ASP.NET Core's
    // DI resolves an unregistered IEnumerable<T> constructor parameter to an EMPTY collection,
    // never the C# default, so a bare `normalizers ?? [...]` fallback never fires once this type
    // is DI-activated with nothing registered for INormalizer.
    public NormalizerRouter(IEnumerable<INormalizer>? normalizers = null) {
        _normalizers = new List<INormalizer> {
            new ClaudeCodeNormalizer(),
            new UniversalAcpNormalizer(),
            new AntigravityNormalizer()
        };
        if (normalizers is not null)
            foreach (var n in normalizers)
                if (!_normalizers.Any(b => b.VendorKey == n.VendorKey))
                    _normalizers.Add(n);
    }

    public IReadOnlyList<SessionEventRecord> Normalize(string vendor, string sessionId, string? agentId, int lineNumber, string rawLine) =>
        Normalize(vendor, sessionId, agentId, lineNumber, rawLine, out _);

    public IReadOnlyList<SessionEventRecord> Normalize(string vendor, string sessionId, string? agentId, int lineNumber, string rawLine, out bool failed) {
        var normalizer = _normalizers.FirstOrDefault(n => n.CanNormalize(vendor))
                         ?? _normalizers.First(n => n.CanNormalize("acp"));

        var events = normalizer.NormalizeLine(vendor, sessionId, agentId, lineNumber, rawLine);
        failed = LineFailed(vendor, rawLine, events);
        return events;
    }

    static bool LineFailed(string vendor, string rawLine, IReadOnlyList<SessionEventRecord> events) {
        JsonDocument doc;
        try {
            doc = JsonDocument.Parse(rawLine);
        } catch (JsonException) {
            return true;
        }

        using (doc) {
            if (!IsClaude(vendor)) return false;
            if (events.Count == 0) return false;
            if (events.Count != 1 || events[0].EventType != "RawMessage") return false;

            var type = doc.RootElement.Str("type");
            if (type is "user" or "assistant") {
                return doc.RootElement.Obj("message") is null;
            }

            return true;
        }
    }

    static bool IsClaude(string vendor) =>
        string.Equals(vendor, "claude", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(vendor, "claude-code", StringComparison.OrdinalIgnoreCase);
}
