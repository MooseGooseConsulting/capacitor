using System.Text.Json.Serialization;

namespace Capacitor.Server.Data.Entities;

public record WorkItemRecord {
    [JsonPropertyName("work_item_id")]
    public required string WorkItemId { get; init; }

    [JsonPropertyName("repo_hash")]
    public required string RepoHash { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("issue_key")]
    public string? IssueKey { get; init; }

    [JsonPropertyName("pr_number")]
    public int? PrNumber { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "open";

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record WorkItemSessionRecord {
    [JsonPropertyName("work_item_id")]
    public required string WorkItemId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("correlation_source")]
    public required string CorrelationSource { get; init; }

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; init; } = 1.0m;

    [JsonPropertyName("attached_at")]
    public DateTimeOffset AttachedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record WorkItemBreakdownRecord {
    [JsonPropertyName("parent_id")]
    public required string ParentId { get; init; }

    [JsonPropertyName("part_id")]
    public required string PartId { get; init; }
}

public record WorkItemRelationRecord {
    [JsonPropertyName("from_id")]
    public required string FromId { get; init; }

    [JsonPropertyName("to_id")]
    public required string ToId { get; init; }

    [JsonPropertyName("relation_kind")]
    public required string RelationKind { get; init; } // "blocks" | "blocked_by"
}
