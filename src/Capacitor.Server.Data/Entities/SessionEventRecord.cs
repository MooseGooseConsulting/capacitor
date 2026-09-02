using System.Text.Json.Serialization;

namespace Capacitor.Server.Data.Entities;

/// <summary>
/// Immutable record representing a single canonical event in a session stream.
/// Keyed on (SessionId, AgentId, LineNumber, LogicalSeq).
/// </summary>
public record SessionEventRecord {
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("agent_id")]
    public string AgentId { get; init; } = string.Empty;

    [JsonPropertyName("line_number")]
    public required int LineNumber { get; init; }

    [JsonPropertyName("logical_seq")]
    public long LogicalSeq { get; init; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; init; }

    [JsonPropertyName("event_type")]
    public required string EventType { get; init; }

    [JsonPropertyName("vendor")]
    public required string Vendor { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("input_tokens")]
    public long? InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long? OutputTokens { get; init; }

    [JsonPropertyName("cache_read_tokens")]
    public long? CacheReadTokens { get; init; }

    [JsonPropertyName("cache_write_tokens")]
    public long? CacheWriteTokens { get; init; }

    [JsonPropertyName("reasoning_tokens")]
    public long? ReasoningTokens { get; init; }

    [JsonPropertyName("context_used_tokens")]
    public long? ContextUsedTokens { get; init; }

    [JsonPropertyName("context_window_tokens")]
    public long? ContextWindowTokens { get; init; }

    [JsonPropertyName("cost_usd")]
    public decimal? CostUsd { get; init; }

    [JsonPropertyName("item_id")]
    public string? ItemId { get; init; }

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

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("raw_payload")]
    public string? RawPayload { get; init; }

    // Evidence belongs to the emitted event, never only to the session. A session can
    // legitimately work across several repositories and directories.
    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("repo_hash")]
    public string? RepoHash { get; init; }

    [JsonPropertyName("repo_owner")]
    public string? RepoOwner { get; init; }

    [JsonPropertyName("repo_name")]
    public string? RepoName { get; init; }
}
