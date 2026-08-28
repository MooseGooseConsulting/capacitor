using System.Text.Json.Serialization;

namespace Capacitor.Server.Data.Entities;

/// <summary>
/// Tracks subagent runs and child conversation hierarchies.
/// </summary>
public record SubagentRunRecord {
    [JsonPropertyName("parent_session_id")]
    public required string ParentSessionId { get; init; }

    [JsonPropertyName("agent_id")]
    public required string AgentId { get; init; }

    [JsonPropertyName("agent_type")]
    public string? AgentType { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    [JsonPropertyName("spawned_at")]
    public required DateTimeOffset SpawnedAt { get; init; }

    [JsonPropertyName("stopped_at")]
    public DateTimeOffset? StoppedAt { get; init; }

    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("exit_status")]
    public string? ExitStatus { get; init; }
}
