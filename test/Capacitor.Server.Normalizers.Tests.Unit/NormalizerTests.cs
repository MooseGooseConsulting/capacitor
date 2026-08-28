namespace Capacitor.Server.Normalizers.Tests.Unit;

public class NormalizerTests {
    private readonly NormalizerRouter _router = new();

    [Test]
    public async Task ClaudeCodeNormalizer_normalizes_assistant_text_and_tool_use() {
        var rawLine = @"
        {
            ""type"": ""assistant"",
            ""timestamp"": ""2026-06-01T12:00:00.000Z"",
            ""message"": {
                ""model"": ""claude-opus-4"",
                ""content"": [
                    { ""type"": ""text"", ""text"": ""Running it now."" },
                    { ""type"": ""tool_use"", ""id"": ""t1"", ""name"": ""bash"", ""input"": { ""command"": ""git status"" } }
                ],
                ""usage"": { ""input_tokens"": 4200, ""output_tokens"": 150, ""cost_usd"": 0.0135 }
            }
        }";

        var events = _router.Normalize("claude", "sess-1", "", 1, rawLine);

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[0].EventType).IsEqualTo("AssistantTurn");
        await Assert.That(events[0].Content).IsEqualTo("Running it now.");
        await Assert.That(events[0].Model).IsEqualTo("claude-opus-4");
        await Assert.That(events[0].InputTokens).IsEqualTo(4200);
        await Assert.That(events[0].CostUsd).IsEqualTo(0.0135m);
        await Assert.That(events[0].Timestamp).IsEqualTo(DateTimeOffset.Parse("2026-06-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(events[1].EventType).IsEqualTo("ToolCall");
        await Assert.That(events[1].ToolName).IsEqualTo("bash");
    }

    [Test]
    public async Task ClaudeCodeNormalizer_normalizes_user_text() {
        const string rawLine = """{"type":"user","timestamp":"2026-06-01T12:00:00.000Z","message":{"role":"user","content":"hi"}}""";

        var events = _router.Normalize("claude", "sess-1", "", 1, rawLine);

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo("UserMessage");
        await Assert.That(events[0].Content).IsEqualTo("hi");
    }

    [Test]
    public async Task UniversalAcpNormalizer_normalizes_real_acp_tool_call() {
        var rawLine = @"
        {
            ""jsonrpc"": ""2.0"",
            ""method"": ""session/update"",
            ""params"": {
                ""update"": {
                    ""sessionUpdate"": ""tool_call"",
                    ""title"": ""read_file"",
                    ""rawInput"": { ""path"": ""/src/index.ts"" }
                }
            }
        }";

        var events = _router.Normalize("cursor", "sess-2", "", 1, rawLine);

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo("ToolCall");
        await Assert.That(events[0].ToolName).IsEqualTo("read_file");
        await Assert.That(events[0].Vendor).IsEqualTo("cursor");
    }

    [Test]
    public async Task UniversalAcpNormalizer_normalizes_pi_message() {
        const string rawLine = """{"type":"message","timestamp":"2026-06-12T10:00:02.000Z","message":{"role":"assistant","content":[{"type":"text","text":"hi there"}],"model":"gpt-5","usage":{"input":10,"output":3}}}""";

        var events = _router.Normalize("pi", "sess-3", "", 1, rawLine);

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo("AssistantTurn");
        await Assert.That(events[0].Content).IsEqualTo("hi there");
        await Assert.That(events[0].Vendor).IsEqualTo("pi");
        await Assert.That(events[0].InputTokens).IsEqualTo(10);
    }

    [Test]
    public async Task UniversalAcpNormalizer_normalizes_gemini_assistant_with_tool_call() {
        const string rawLine = """{"id":"m1","timestamp":"2026-06-22T14:31:05.000Z","type":"gemini","content":"","tokens":{"input":5,"output":2,"total":7},"model":"gemini-3-flash-preview","toolCalls":[{"id":"c1","name":"invoke_agent","args":{"agent_name":"codebase_investigator"},"status":"success"}]}""";

        var events = _router.Normalize("gemini", "sess-4", "", 1, rawLine);

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo("ToolCall");
        await Assert.That(events[0].ToolName).IsEqualTo("invoke_agent");
        await Assert.That(events[0].Vendor).IsEqualTo("gemini");
    }

    [Test]
    public async Task AntigravityNormalizer_normalizes_planner_response_content_and_tool_calls() {
        const string rawLine = """{"type":"PLANNER_RESPONSE","content":"Sure, adding it now.","thinking":"plan","tool_calls":[{"name":"list_dir","DirectoryPath":"/src"}]}""";

        var events = _router.Normalize("antigravity", "sess-5", "", 1, rawLine);

        await Assert.That(events.Count).IsEqualTo(3);
        await Assert.That(events[0].EventType).IsEqualTo("AssistantTurn");
        await Assert.That(events[0].Content).IsEqualTo("Sure, adding it now.");
        await Assert.That(events[1].EventType).IsEqualTo("AssistantThinking");
        await Assert.That(events[1].Content).IsEqualTo("plan");
        await Assert.That(events[2].EventType).IsEqualTo("ToolCall");
        await Assert.That(events[2].ToolName).IsEqualTo("list_dir");
    }

    [Test]
    public async Task AntigravityNormalizer_normalizes_tool_result_step() {
        const string rawLine = """{"type":"RUN_COMMAND","status":"DONE","content":"ran ls"}""";

        var events = _router.Normalize("antigravity", "sess-5", "", 2, rawLine);

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo("ToolResult");
        await Assert.That(events[0].ToolOutput).IsEqualTo("ran ls");
        await Assert.That(events[0].IsError).IsFalse();
    }

    [Test]
    public async Task AntigravityNormalizer_normalizes_usage_backfill_line() {
        const string rawLine = """{"type":"USAGE","gen_row":1,"input_tokens":120,"output_tokens":40,"cache_read_tokens":10,"cache_write_tokens":0,"model":"gemini-default","created_at":"2026-06-01T12:00:00.000Z"}""";

        var events = _router.Normalize("antigravity", "sess-5", "", 3, rawLine);

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo("UsageBackfill");
        await Assert.That(events[0].Model).IsEqualTo("gemini-default");
        await Assert.That(events[0].InputTokens).IsEqualTo(120);
        await Assert.That(events[0].OutputTokens).IsEqualTo(40);
        await Assert.That(events[0].CacheReadTokens).IsEqualTo(10);
        await Assert.That(events[0].Timestamp).IsEqualTo(DateTimeOffset.Parse("2026-06-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture));
    }
}
