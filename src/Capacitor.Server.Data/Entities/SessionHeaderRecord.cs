using System.Text.Json.Serialization;

namespace Capacitor.Server.Data.Entities;

/// <summary>
/// Header metadata and rolled-up metrics for a session.
/// Keyed on SessionId (dashless).
/// </summary>
public record SessionHeaderRecord {
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("slug")]
    public string? Slug { get; init; }

    [JsonPropertyName("vendor")]
    public required string Vendor { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "active";

    [JsonPropertyName("visibility")]
    public string Visibility { get; init; } = "project";

    [JsonPropertyName("hidden_reason")]
    public string? HiddenReason { get; init; }

    [JsonPropertyName("disposition")]
    public string? Disposition { get; init; }

    [JsonPropertyName("owner_user_id")]
    public required string OwnerUserId { get; init; }

    [JsonPropertyName("machine_id")]
    public string? MachineId { get; init; }

    [JsonPropertyName("daemon_id")]
    public string? DaemonId { get; init; }

    [JsonPropertyName("repo_hash")]
    public string? RepoHash { get; init; }

    [JsonPropertyName("repo_owner")]
    public string? RepoOwner { get; init; }

    [JsonPropertyName("repo_name")]
    public string? RepoName { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("pr_number")]
    public int? PrNumber { get; init; }

    [JsonPropertyName("pr_title")]
    public string? PrTitle { get; init; }

    [JsonPropertyName("pr_url")]
    public string? PrUrl { get; init; }

    [JsonPropertyName("pr_head_ref")]
    public string? PrHeadRef { get; init; }

    [JsonPropertyName("started_at")]
    public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("ended_at")]
    public DateTimeOffset? EndedAt { get; init; }

    [JsonPropertyName("last_event_at")]
    public DateTimeOffset? LastEventAt { get; init; }

    [JsonPropertyName("duration_min")]
    public decimal DurationMin { get; init; }

    [JsonPropertyName("event_count")]
    public int EventCount { get; init; }

    [JsonPropertyName("tool_count")]
    public int? ToolCount { get; init; }

    [JsonPropertyName("total_tokens")]
    public long? TotalTokens { get; init; }

    [JsonPropertyName("total_cost_usd")]
    public decimal? TotalCostUsd { get; init; }

    [JsonPropertyName("previous_session_id")]
    public string? PreviousSessionId { get; init; }

    [JsonPropertyName("next_session_id")]
    public string? NextSessionId { get; init; }

    [JsonPropertyName("primary_phase")]
    public string? PrimaryPhase { get; init; }

    [JsonPropertyName("secondary_phase")]
    public string? SecondaryPhase { get; init; }

    [JsonPropertyName("classification_confidence")]
    public decimal? ClassificationConfidence { get; init; }

    [JsonPropertyName("classification_source")]
    public string? ClassificationSource { get; init; }
}
