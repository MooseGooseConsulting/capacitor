using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Api.Tests.Unit;

public class IdCanonicalizerTests {
    [Test]
    public async Task Canonicalize_strips_hyphens_from_uuids_only() {
        await Assert.That(IdCanonicalizer.Canonicalize("70dc37b2-b3b1-4f13-9c15-3858abbe88a8"))
            .IsEqualTo("70dc37b2b3b14f139c153858abbe88a8");
        await Assert.That(IdCanonicalizer.Canonicalize("release-followup"))
            .IsEqualTo("release-followup");
        await Assert.That(IdCanonicalizer.Canonicalize(null)).IsEqualTo("");
    }
}

public class EvalContextComposerTests {
    [Test]
    public async Task Compose_puts_tool_input_on_tool_calls_and_compacts_oversize_results() {
        var now = DateTimeOffset.UtcNow;
        var events = new List<SessionEventRecord> {
            new() {
                SessionId = "s",
                LineNumber = 1,
                EventType = "ToolCall",
                Vendor = "claude",
                Timestamp = now,
                ToolName = "bash",
                ToolInput = "rm -rf /tmp/x"
            },
            new() {
                SessionId = "s",
                LineNumber = 2,
                EventType = "ToolResult",
                Vendor = "claude",
                Timestamp = now,
                ToolName = "bash",
                ToolOutput = new string('x', 500)
            }
        };

        var (trace, compaction) = EvalContextComposer.Compose(events, thresholdBytes: 50);

        await Assert.That(trace[0].Kind).IsEqualTo("tool_call");
        await Assert.That(trace[0].Text).IsEqualTo("rm -rf /tmp/x");
        await Assert.That(trace[1].Kind).IsEqualTo("tool_result");
        await Assert.That(trace[1].Text!.Length).IsLessThan(500);
        await Assert.That(compaction.ToolResultsTruncated).IsEqualTo(1);
        await Assert.That(compaction.BytesSaved).IsGreaterThan(0);
        await Assert.That(compaction.ToolResultsTotal).IsEqualTo(1);
    }
}
