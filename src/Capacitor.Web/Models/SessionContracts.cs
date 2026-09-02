using System.Text.Json.Serialization;

namespace Capacitor.Web.Models;

/// <summary>One page of sessions returned by the Capacitor API.</summary>
public sealed record SessionSearchResponse {
    // The API's frozen search envelope is { hits, total }. Keeping this at the
    // boundary prevents a successful API response from silently rendering an
    // empty Sessions list.
    [JsonPropertyName("hits")]
    public IReadOnlyList<SessionSummary> Sessions { get; init; } = [];

    [JsonPropertyName("total")]
    public long Total { get; init; }
}

/// <summary>Query parameters for the read-only sessions index.</summary>
public sealed record SessionSearchRequest(
    string? Query = null,
    string? Repository = null,
    string? Vendor = null,
    string? Status = null,
    int Limit = 50,
    int Offset = 0);

/// <summary>Summary data rendered by a card in the Sessions list.</summary>
public record SessionSummary {
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("repo_owner")]
    public string? RepoOwner { get; init; }

    [JsonPropertyName("repo_name")]
    public string? RepoName { get; init; }

    [JsonPropertyName("pr_number")]
    public int? PrNumber { get; init; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("total_tokens")]
    public long? TotalTokens { get; init; }

    [JsonPropertyName("tool_count")]
    public int? ToolCount { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("owner_user_id")]
    public string? OwnerUserId { get; init; }

    [JsonIgnore]
    public string Repository => string.IsNullOrWhiteSpace(RepoOwner) || string.IsNullOrWhiteSpace(RepoName)
        ? "Unattributed repository"
        : $"{RepoOwner}/{RepoName}";
}

/// <summary>The data required by all six tabs in a session detail route.</summary>
public sealed record SessionDetailResponse {
    [JsonPropertyName("session")]
    public required SessionDetailHeader Session { get; init; }

    [JsonPropertyName("events")]
    public IReadOnlyList<SessionEvent> Events { get; init; } = [];

    [JsonPropertyName("trace")]
    public SessionTrace? Trace { get; init; }

    [JsonPropertyName("evaluation")]
    public SessionEvaluation? Evaluation { get; init; }
}

/// <summary>Session metadata and aggregate metrics for the detail header and tabs.</summary>
public sealed record SessionDetailHeader : SessionSummary {
    [JsonPropertyName("visibility")]
    public string? Visibility { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("pr_title")]
    public string? PullRequestTitle { get; init; }

    [JsonPropertyName("machine_id")]
    public string? MachineId { get; init; }

    [JsonPropertyName("daemon_id")]
    public string? DaemonId { get; init; }

    [JsonPropertyName("event_count")]
    public int EventCount { get; init; }

    [JsonPropertyName("total_cost_usd")]
    public decimal? TotalCostUsd { get; init; }

    [JsonPropertyName("ended_at")]
    public DateTimeOffset? EndedAt { get; init; }

    [JsonPropertyName("last_event_at")]
    public DateTimeOffset? LastEventAt { get; init; }

    [JsonPropertyName("previous_session_id")]
    public string? PreviousSessionId { get; init; }

    [JsonPropertyName("next_session_id")]
    public string? NextSessionId { get; init; }
}

/// <summary>An immutable normalized transcript event with its original source payload.</summary>
public sealed record SessionEvent {
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; init; }

    [JsonPropertyName("event_type")]
    public required string EventType { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("input_tokens")]
    public long? InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long? OutputTokens { get; init; }

    [JsonPropertyName("cache_read_tokens")]
    public long? CacheReadTokens { get; init; }

    [JsonPropertyName("cache_write_tokens")]
    public long? CacheWriteTokens { get; init; }

    [JsonPropertyName("cost_usd")]
    public decimal? CostUsd { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("tool_server")]
    public string? ToolServer { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public string? ToolInput { get; init; }

    [JsonPropertyName("tool_output")]
    public string? ToolOutput { get; init; }

    [JsonPropertyName("tool_exit_code")]
    public int? ToolExitCode { get; init; }

    [JsonPropertyName("is_error")]
    public bool IsError { get; init; }

    [JsonPropertyName("raw_payload")]
    public string? RawPayload { get; init; }

    [JsonPropertyName("line_number")]
    public int LineNumber { get; init; }

    [JsonPropertyName("logical_seq")]
    public long LogicalSequence { get; init; }

    [JsonIgnore]
    public bool IsTranscriptMessage => EventType is "UserMessage" or "AssistantTurn" or "AssistantThinking";

    [JsonIgnore]
    public string ToolDisplayName => string.IsNullOrWhiteSpace(ToolServer) ? ToolName ?? "Tool" : $"{ToolServer}: {ToolName}";
}

/// <summary>Ordered rollup rows for the Trace tab.</summary>
public sealed record SessionTrace {
    [JsonPropertyName("entries")]
    public IReadOnlyList<TraceEntry> Entries { get; init; } = [];
}

public sealed record TraceEntry {
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("turn")]
    public TraceTurn? Turn { get; init; }

    [JsonPropertyName("event")]
    public SessionEvent? Event { get; init; }
}

/// <summary>Aggregate emitted by the persisted-session API for one user turn.</summary>
public sealed record TraceTurn {
    [JsonPropertyName("turn_index")]
    public int TurnIndex { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("ended_at")]
    public DateTimeOffset EndedAt { get; init; }

    [JsonPropertyName("duration_ms")]
    public long? DurationMilliseconds { get; init; }

    [JsonPropertyName("input_tokens")]
    public long? InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long? OutputTokens { get; init; }

    [JsonPropertyName("cache_read_tokens")]
    public long? CacheReadTokens { get; init; }

    [JsonPropertyName("cache_write_tokens")]
    public long? CacheWriteTokens { get; init; }

    [JsonPropertyName("cost_usd")]
    public decimal? CostUsd { get; init; }

    [JsonPropertyName("tool_count")]
    public int ToolCount { get; init; }

    [JsonPropertyName("events")]
    public IReadOnlyList<SessionEvent> Events { get; init; } = [];
}

/// <summary>The latest persisted evaluation, if this session has been evaluated.</summary>
public sealed record SessionEvaluation {
    [JsonPropertyName("run")]
    public SessionEvaluationRun? Run { get; init; }

    [JsonPropertyName("verdicts")]
    public IReadOnlyList<SessionEvaluationVerdict> Verdicts { get; init; } = [];
}

/// <summary>Latest evaluation run returned by the persisted-session API.</summary>
public sealed record SessionEvaluationRun {
    [JsonPropertyName("overall_score")]
    public decimal? OverallScore { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("judge_model")]
    public string? JudgeModel { get; init; }

    [JsonPropertyName("evaluated_at")]
    public DateTimeOffset? EvaluatedAt { get; init; }
}

/// <summary>One persisted evaluation verdict associated with the latest run.</summary>
public sealed record SessionEvaluationVerdict {
    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("question_id")]
    public string? QuestionId { get; init; }

    [JsonPropertyName("score")]
    public int Score { get; init; }

    [JsonPropertyName("verdict")]
    public string? Verdict { get; init; }

    [JsonPropertyName("finding")]
    public string? Finding { get; init; }

    [JsonPropertyName("evidence")]
    public string? Evidence { get; init; }

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; init; }
}
