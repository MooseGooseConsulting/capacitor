using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Normalizers;

// Dispatches by vendor rather than parsing one shared wire shape: Pi and Gemini each write
// their own flat transcript record, and only cursor/opencode/copilot/acp itself speak real
// ACP `session/update` JSON-RPC notifications.
public class UniversalAcpNormalizer : INormalizer {
    public string VendorKey => "acp";

    static readonly HashSet<string> SupportedVendors = new(StringComparer.OrdinalIgnoreCase) {
        "acp", "cursor", "gemini", "opencode", "pi", "copilot"
    };

    public bool CanNormalize(string vendor) => SupportedVendors.Contains(vendor);

    public IReadOnlyList<SessionEventRecord> NormalizeLine(string vendor, string sessionId, string? agentId, int lineNumber, string rawLine) {
        var timestamp = DateTimeOffset.UtcNow;

        try {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.Str("timestamp") is { } tsStr && DateTimeOffset.TryParse(tsStr, out var parsedTs)) timestamp = parsedTs;

            if (string.Equals(vendor, "pi", StringComparison.OrdinalIgnoreCase))
                return NormalizePi(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, root);
            if (string.Equals(vendor, "gemini", StringComparison.OrdinalIgnoreCase))
                return NormalizeGemini(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, root);

            return NormalizeAcpUpdate(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, root);
        } catch (JsonException) {
            return [Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "AcpFrame", content: rawLine)];
        }
    }

    // Pi transcript record: {"type":"message","timestamp":...,"message":{"role":..,"content":
    // string|[{type:"text",text}],"model":..,"usage":{"input":N,"output":N}}}.
    static List<SessionEventRecord> NormalizePi(string vendor, string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, JsonElement root) {
        if (root.Str("type") != "message" || root.Obj("message") is not { } message)
            return [Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "AcpFrame")];

        return message.Str("role") switch {
            "user" => [
                Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "UserMessage",
                    content: message.Str("content") ?? (message.Arr("content") is { } blocks ? JoinTextBlocks(blocks) : null),
                    model: message.Str("model"))
            ],
            "assistant" => NormalizePiAssistant(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, message),
            "toolResult" when message.Str("toolCallId") is { Length: > 0 } => [
                Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "ToolResult",
                    toolOutput: PiToolResultText(message), isError: message.Bool("isError") == true)
            ],
            "bashExecution" when message.Str("command") is { Length: > 0 } command => [
                Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "ToolCall",
                    toolName: "bash", toolInput: command)
            ],
            _ => [Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "RawMessage")],
        };
    }

    static List<SessionEventRecord> NormalizePiAssistant(string vendor, string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, JsonElement message) {
        var model = message.Str("model");
        var usage = message.Obj("usage");
        var inputTokens = usage?.Num("input");
        var outputTokens = usage?.Num("output");
        var result = new List<SessionEventRecord>();
        var usageAttached = false;

        SessionEventRecord Emit(string eventType, string? content = null, string? toolName = null, string? toolInput = null) {
            var record = Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, eventType,
                content: content, toolName: toolName, toolInput: toolInput, model: model,
                inputTokens: usageAttached ? null : inputTokens,
                outputTokens: usageAttached ? null : outputTokens);
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
                    case "toolCall":
                        result.Add(Emit("ToolCall",
                            toolName: block.Str("name"),
                            toolInput: block.Obj("arguments")?.GetRawText()));
                        break;
                }
            }
        } else if (message.Str("content") is { } scalar) {
            result.Add(Emit("AssistantTurn", content: scalar));
        }

        if (result.Count == 0) result.Add(Emit("AssistantTurn"));
        return result;
    }

    static string? PiToolResultText(JsonElement message) =>
        message.Str("content") ?? (message.Arr("content") is { } blocks ? JoinTextBlocks(blocks) : null);

    // Gemini transcript record: user turns are {"type":"user","content":[{text}]}; assistant
    // turns are {"type":"gemini","content":string,"tokens":{input,output},"model":..,"toolCalls":[...]}.
    static List<SessionEventRecord> NormalizeGemini(string vendor, string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, JsonElement root) {
        switch (root.Str("type")) {
            case "user": {
                var content = root.Arr("content") is { } blocks ? JoinTextBlocks(blocks) : null;
                return [Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "UserMessage", content: content)];
            }
            case "gemini": {
                var model = root.Str("model");
                var tokens = root.Obj("tokens");
                var inputTokens = tokens?.Num("input");
                var outputTokens = tokens?.Num("output");
                var result = new List<SessionEventRecord>();

                if (root.Str("content") is { Length: > 0 } content)
                    result.Add(Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "AssistantTurn",
                        content: content, model: model, inputTokens: inputTokens, outputTokens: outputTokens));

                if (root.Arr("toolCalls") is { } toolCalls)
                    foreach (var call in toolCalls.EnumerateArray())
                        result.Add(Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "ToolCall",
                            toolName: call.Str("name"), toolInput: call.Obj("args")?.GetRawText(), model: model,
                            inputTokens: result.Count == 0 ? inputTokens : null,
                            outputTokens: result.Count == 0 ? outputTokens : null));

                if (result.Count == 0)
                    result.Add(Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "AssistantTurn",
                        model: model, inputTokens: inputTokens, outputTokens: outputTokens));
                return result;
            }
            default:
                return [Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "AcpFrame")];
        }
    }

    // Real ACP: {"method":"session/update","params":{"update":{"sessionUpdate":<kind>, ...}}}.
    static List<SessionEventRecord> NormalizeAcpUpdate(string vendor, string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, JsonElement root) {
        if (root.Str("method") != "session/update" || root.Obj("params") is not { } p || p.Obj("update") is not { } update)
            return [Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "AcpFrame")];

        return update.Str("sessionUpdate") switch {
            "agent_message_chunk" => [Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "AssistantTurn",
                content: update.Obj("content")?.Str("text"))],
            "agent_thought_chunk" => [Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "AssistantThinking",
                content: update.Obj("content")?.Str("text"))],
            "tool_call" => [Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "ToolCall",
                toolName: update.Str("title") ?? update.Str("kind"),
                toolInput: update.Obj("rawInput")?.GetRawText())],
            // Terminal status with no payload only updates correlation — emit a result when
            // content or rawOutput is present. Non-terminal status is the default AcpFrame path.
            "tool_call_update" when (update.Str("status") is "completed" or "failed") && ToolResultText(update) is { } output =>
                [Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "ToolResult",
                    toolOutput: output, isError: update.Str("status") == "failed")],
            "tool_call_update" when update.Str("status") is "completed" or "failed" => [],
            _ => [Frame(vendor, sessionId, agentId, lineNumber, rawLine, timestamp, "AcpFrame")],
        };
    }

    static string? JoinTextBlocks(JsonElement blocks) {
        List<string>? parts = null;
        foreach (var block in blocks.EnumerateArray())
            if (block.Str("text") is { } t) (parts ??= []).Add(t);
        return parts is null ? null : string.Join("", parts);
    }

    static string? ToolResultText(JsonElement update) {
        if (update.Arr("content") is { } blocks) {
            List<string>? texts = null;
            foreach (var block in blocks.EnumerateArray())
                if (block.Str("type") == "content" && block.Obj("content")?.Str("text") is { } t) (texts ??= []).Add(t);
            if (texts is { Count: > 0 }) return string.Join("\n", texts);
        }
        return update.Obj("rawOutput")?.GetRawText();
    }

    static SessionEventRecord Frame(
            string vendor, string sessionId, string? agentId, int lineNumber, string rawLine, DateTimeOffset timestamp, string eventType,
            string? model = null, string? content = null, string? toolName = null, string? toolInput = null,
            string? toolOutput = null, bool isError = false, long? inputTokens = null, long? outputTokens = null) =>
        new SessionEventRecord {
            SessionId = sessionId,
            AgentId = agentId ?? string.Empty,
            LineNumber = lineNumber,
            EventType = eventType,
            Vendor = vendor,
            Model = model,
            Timestamp = timestamp,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ToolName = toolName,
            ToolInput = toolInput,
            ToolOutput = toolOutput,
            IsError = isError,
            Content = content,
            RawPayload = rawLine
        };
}
