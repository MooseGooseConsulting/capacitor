namespace Capacitor.Server.Normalizers.Tests.Unit;

public class NormalizerTests {
    private readonly NormalizerRouter _router = new();

    [Test]
    public async Task ClaudeCodeNormalizer_normalizes_tool_use_and_usage() {
        var rawLine = @"
        {
            ""type"": ""tool_use"",
            ""name"": ""bash"",
            ""input"": { ""command"": ""git status"" },
            ""usage"": {
                ""input_tokens"": 4200,
                ""output_tokens"": 150,
                ""cost_usd"": 0.0135
            }
        }";

        var ev = _router.Normalize("claude", "sess-1", "", 1, rawLine);

        await Assert.That(ev.EventType).IsEqualTo("ToolCall");
        await Assert.That(ev.ToolName).IsEqualTo("bash");
        await Assert.That(ev.InputTokens).IsEqualTo(4200);
        await Assert.That(ev.OutputTokens).IsEqualTo(150);
        await Assert.That(ev.CostUsd).IsEqualTo(0.0135m);
    }

    [Test]
    public async Task UniversalAcpNormalizer_normalizes_jsonrpc_tool_call() {
        var rawLine = @"
        {
            ""jsonrpc"": ""2.0"",
            ""method"": ""tool/call"",
            ""params"": {
                ""name"": ""read_file"",
                ""arguments"": { ""path"": ""/src/index.ts"" }
            }
        }";

        var ev = _router.Normalize("cursor", "sess-2", "", 1, rawLine);

        await Assert.That(ev.EventType).IsEqualTo("ToolCall");
        await Assert.That(ev.ToolName).IsEqualTo("read_file");
    }

    [Test]
    public async Task AntigravityNormalizer_normalizes_planner_step() {
        var rawLine = @"
        {
            ""type"": ""PLANNER_RESPONSE"",
            ""thinking"": ""Let's inspect the directory structure"",
            ""tool_calls"": [
                { ""toolAction"": ""Listing directory"", ""DirectoryPath"": ""/src"" }
            ]
        }";

        var ev = _router.Normalize("antigravity", "sess-3", "", 1, rawLine);

        await Assert.That(ev.EventType).IsEqualTo("ToolCall");
        await Assert.That(ev.ToolName).IsEqualTo("Listing directory");
    }
}
