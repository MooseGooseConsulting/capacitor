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
        await Assert.That(events[0].InputTokens).IsEqualTo(5);
        await Assert.That(events[0].OutputTokens).IsEqualTo(2);
    }

    [Test]
    public async Task ClaudeCodeNormalizer_skips_meta_and_sidechain_rows() {
        const string meta = """{"type":"user","isMeta":true,"message":{"content":"x"}}""";
        const string sidechain = """{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"x"}]}}""";

        await Assert.That(_router.Normalize("claude", "sess-1", "", 1, meta)).IsEmpty();
        await Assert.That(_router.Normalize("claude", "sess-1", "", 2, sidechain)).IsEmpty();
    }

    [Test]
    public async Task UniversalAcpNormalizer_normalizes_pi_tool_result() {
        const string rawLine = """{"type":"message","message":{"role":"toolResult","toolCallId":"c1","content":[{"type":"text","text":"ok"}],"isError":false}}""";

        var events = _router.Normalize("pi", "sess-3", "", 2, rawLine);

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo("ToolResult");
        await Assert.That(events[0].ToolOutput).IsEqualTo("ok");
        await Assert.That(events[0].IsError).IsFalse();
        await Assert.That(events[0].Vendor).IsEqualTo("pi");
    }

    [Test]
    public async Task UniversalAcpNormalizer_skips_status_only_tool_call_update() {
        const string rawLine = """{"jsonrpc":"2.0","method":"session/update","params":{"update":{"sessionUpdate":"tool_call_update","status":"completed"}}}""";

        var events = _router.Normalize("cursor", "sess-2", "", 2, rawLine);

        await Assert.That(events).IsEmpty();
    }

    [Test]
    public async Task AntigravityNormalizer_strips_user_request_envelope_and_keeps_created_at() {
        const string rawLine = """{"type":"USER_INPUT","created_at":"2026-07-02T19:00:00Z","content":"<USER_REQUEST>hi</USER_REQUEST><ADDITIONAL_METADATA>x</ADDITIONAL_METADATA>"}""";

        var events = _router.Normalize("antigravity", "sess-5", "", 0, rawLine);

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo("UserMessage");
        await Assert.That(events[0].Content).IsEqualTo("hi");
        await Assert.That(events[0].Timestamp).IsEqualTo(DateTimeOffset.Parse("2026-07-02T19:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
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

    [Test]
    public async Task CodexNormalizer_normalizes_response_item_assistant_content_without_duplicate_usage() {
        const string rawLine = """{"timestamp":"2026-08-11T09:00:16.000Z","type":"response_item","payload":{"id":"msg-1","type":"message","role":"assistant","model":"gpt-5-codex","content":[{"type":"output_text","text":"I found the issue."},{"type":"reasoning","text":"Inspect the call path."}],"usage":{"input_tokens":120,"cached_input_tokens":40,"output_tokens":22,"reasoning_output_tokens":9,"cost_usd":0.0042}}}""";

        var events = _router.Normalize("codex", "sess-codex", "", 14, rawLine);

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[0].EventType).IsEqualTo("AssistantTurn");
        await Assert.That(events[0].Content).IsEqualTo("I found the issue.");
        await Assert.That(events[0].Model).IsEqualTo("gpt-5-codex");
        // Codex response-item usage is a point-in-time value. The durable stream uses the
        // cumulative token_count records as its sole usage source, so retaining it here
        // would double count the same work when the snapshot arrives.
        await Assert.That(events[0].InputTokens).IsNull();
        await Assert.That(events[0].CacheReadTokens).IsNull();
        await Assert.That(events[0].ReasoningTokens).IsNull();
        await Assert.That(events[0].CostUsd).IsNull();
        await Assert.That(events[0].LogicalSeq).IsEqualTo(0);
        await Assert.That(events[1].EventType).IsEqualTo("AssistantThinking");
        await Assert.That(events[1].InputTokens).IsNull();
        await Assert.That(events[1].LogicalSeq).IsEqualTo(1);
        await Assert.That(events[1].RawPayload).IsEqualTo(rawLine);
    }

    [Test]
    public async Task CodexNormalizer_normalizes_tool_call_output_and_token_count() {
        const string output = """{"timestamp":"2026-08-11T09:01:01.000Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call-1","output":"permission denied","exit_code":126,"status":"failed"}}""";
        const string tokens = """{"timestamp":"2026-08-11T09:01:02.000Z","type":"event_msg","payload":{"type":"token_count","info":{"model":"gpt-5-codex","total_token_usage":{"input_tokens":500,"cached_input_tokens":200,"output_tokens":30,"reasoning_output_tokens":11}}}}""";

        var toolResult = _router.Normalize("codex", "sess-codex", "", 15, output);
        var usage = _router.Normalize("codex", "sess-codex", "", 16, tokens);

        await Assert.That(toolResult).Count().IsEqualTo(1);
        await Assert.That(toolResult[0].EventType).IsEqualTo("ToolResult");
        await Assert.That(toolResult[0].ItemId).IsEqualTo("call-1");
        await Assert.That(toolResult[0].ToolOutput).IsEqualTo("permission denied");
        await Assert.That(toolResult[0].ToolExitCode).IsEqualTo(126);
        await Assert.That(toolResult[0].IsError).IsTrue();
        await Assert.That(usage).Count().IsEqualTo(1);
        await Assert.That(usage[0].EventType).IsEqualTo("UsageSnapshot");
        await Assert.That(usage[0].InputTokens).IsEqualTo(500);
        await Assert.That(usage[0].CacheReadTokens).IsEqualTo(200);
        await Assert.That(usage[0].ReasoningTokens).IsEqualTo(11);
    }

    [Test]
    public async Task KiroTranscriptNormalizer_normalizes_native_assistant_text_tool_use_and_enriched_usage() {
        const string rawLine = """{"version":"v1","timestamp":"2026-06-10T20:23:50.000Z","kind":"AssistantMessage","data":{"message_id":"a2","content":[{"kind":"text","data":"done"},{"kind":"toolUse","data":{"name":"write","input":{"path":"/work/a.txt","content":"x"}}}],"_kcap_usage":{"input_token_count":120,"output_token_count":21,"model":"claude-haiku-4.5"}}}""";

        var events = _router.Normalize("kiro", "sess-kiro", "", 6, rawLine);

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[0].EventType).IsEqualTo("AssistantTurn");
        await Assert.That(events[0].Content).IsEqualTo("done");
        await Assert.That(events[0].ItemId).IsEqualTo("a2");
        await Assert.That(events[0].Model).IsEqualTo("claude-haiku-4.5");
        await Assert.That(events[0].InputTokens).IsEqualTo(120);
        await Assert.That(events[0].OutputTokens).IsEqualTo(21);
        await Assert.That(events[0].Timestamp).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:50.000Z", System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(events[1].EventType).IsEqualTo("ToolCall");
        await Assert.That(events[1].ToolName).IsEqualTo("write");
        await Assert.That(events[1].ToolInput).Contains("/work/a.txt");
        await Assert.That(events[1].LogicalSeq).IsEqualTo(1);
    }

    [Test]
    public async Task KiroTranscriptNormalizer_normalizes_live_usage_backfill_without_treating_credits_as_usd() {
        const string rawLine = """{"kind":"KiroUsageBackfilled","data":{"message_id":"msg-42","credits":1.5,"context_usage_percentage":37.0}}""";

        var events = _router.Normalize("kiro", "sess-kiro", "", 2_000_000_000, rawLine);

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo("UsageBackfill");
        await Assert.That(events[0].ItemId).IsEqualTo("msg-42");
        await Assert.That(events[0].CostUsd).IsNull();
        await Assert.That(events[0].RawPayload).IsEqualTo(rawLine);
    }

    [Test]
    public async Task ClaudeCodeNormalizer_preserves_explicit_zero_usage() {
        const string rawLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"done"}],"usage":{"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0,"cost_usd":0}}}""";

        var events = _router.Normalize("claude", "sess-zeros", "", 1, rawLine);

        await Assert.That(events).Count().IsEqualTo(1);
        await Assert.That(events[0].InputTokens).IsEqualTo(0);
        await Assert.That(events[0].OutputTokens).IsEqualTo(0);
        await Assert.That(events[0].CacheReadTokens).IsEqualTo(0);
        await Assert.That(events[0].CacheWriteTokens).IsEqualTo(0);
        await Assert.That(events[0].CostUsd).IsEqualTo(0m);
    }
}
