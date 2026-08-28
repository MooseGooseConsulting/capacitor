using System.Text.Json.Serialization;

namespace Capacitor.Server.Data.Entities;

public record EvalRunRecord {
    [JsonPropertyName("eval_run_id")]
    public required string EvalRunId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("judge_model")]
    public required string JudgeModel { get; init; }

    [JsonPropertyName("overall_score")]
    public required int OverallScore { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("retrospective_json")]
    public string? RetrospectiveJson { get; init; }

    [JsonPropertyName("retrospective_prompt_version")]
    public string? RetrospectivePromptVersion { get; init; }

    [JsonPropertyName("evaluated_at")]
    public DateTimeOffset EvaluatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record EvalVerdictRecord {
    [JsonPropertyName("eval_run_id")]
    public required string EvalRunId { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("question_id")]
    public required string QuestionId { get; init; }

    [JsonPropertyName("score")]
    public required int Score { get; init; }

    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    [JsonPropertyName("finding")]
    public required string Finding { get; init; }

    [JsonPropertyName("evidence")]
    public string? Evidence { get; init; }

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; init; }

    [JsonPropertyName("tools_used")]
    public int? ToolsUsed { get; init; }

    [JsonPropertyName("prompt_version")]
    public string? PromptVersion { get; init; }
}

public record JudgeFactRecord {
    [JsonPropertyName("fact_hash")]
    public required string FactHash { get; init; }

    [JsonPropertyName("repo_hash")]
    public required string RepoHash { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("fact")]
    public required string Fact { get; init; }

    [JsonPropertyName("source_session_id")]
    public required string SourceSessionId { get; init; }

    [JsonPropertyName("source_eval_run_id")]
    public required string SourceEvalRunId { get; init; }

    [JsonPropertyName("applies_to_vendors")]
    public string[]? AppliesToVendors { get; init; }

    [JsonPropertyName("applies_to_session_kinds")]
    public string[]? AppliesToSessionKinds { get; init; }

    [JsonPropertyName("retained_at")]
    public DateTimeOffset RetainedAt { get; init; } = DateTimeOffset.UtcNow;
}
