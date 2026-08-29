using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Api;

public static class EvalContextComposer {
    public static (List<EvalContextEntry> Trace, EvalContextCompactionSummary Compaction) Compose(
            IReadOnlyList<SessionEventRecord> events,
            int thresholdBytes
        ) {
        var threshold = thresholdBytes > 0 ? thresholdBytes : 2000;
        var truncated = 0;
        var bytesSaved = 0L;
        var toolResultsTotal = 0;
        var trace = new List<EvalContextEntry>(events.Count);

        foreach (var e in events) {
            if (e.EventType == "ToolResult") toolResultsTotal++;

            var text = e.EventType switch {
                "ToolCall"   => e.ToolInput ?? e.Content,
                "ToolResult" => e.ToolOutput ?? e.Content,
                _            => e.Content ?? e.ToolOutput ?? e.ToolInput
            };

            if (text != null) {
                var compacted = Compact(text, threshold, out var saved);
                if (saved > 0) {
                    if (e.EventType == "ToolResult") truncated++;
                    bytesSaved += saved;
                    text = compacted;
                }
            }

            trace.Add(new EvalContextEntry {
                Kind      = Kind(e.EventType),
                Timestamp = e.Timestamp,
                Text      = text,
                Tool      = e.ToolName
            });
        }

        return (trace, new EvalContextCompactionSummary {
            ThresholdBytes       = threshold,
            Entries              = trace.Count,
            ToolResultsTotal     = toolResultsTotal,
            ToolResultsTruncated = truncated,
            BytesSaved           = bytesSaved
        });
    }

    static string Kind(string eventType) => eventType switch {
        "UserMessage"   => "user_message",
        "AssistantTurn" => "assistant_turn",
        "ToolCall"      => "tool_call",
        "ToolResult"    => "tool_result",
        _               => "event"
    };

    static string Compact(string text, int thresholdBytes, out long bytesSaved) {
        var original = Encoding.UTF8.GetByteCount(text);
        if (original <= thresholdBytes) {
            bytesSaved = 0;
            return text;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var len = Utf8PrefixLength(bytes, thresholdBytes);
        bytesSaved = original - len;
        return Encoding.UTF8.GetString(bytes, 0, len);
    }

    static int Utf8PrefixLength(byte[] bytes, int max) {
        var limit = Math.Min(max, bytes.Length);
        var len = limit;
        while (len > 0 && (bytes[len - 1] & 0xC0) == 0x80) len--;
        if (len == 0) return 0;

        var lead = bytes[len - 1];
        var need = lead switch {
            < 0x80 => 1,
            < 0xE0 when (lead & 0xE0) == 0xC0 => 2,
            < 0xF0 when (lead & 0xF0) == 0xE0 => 3,
            < 0xF8 when (lead & 0xF8) == 0xF0 => 4,
            _ => 0
        };
        if (need == 0 || len - 1 + need > limit) return len - 1;
        return len - 1 + need;
    }
}
