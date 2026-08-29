using System.Text.Json.Serialization;

namespace Capacitor.Server.Data.Entities;

public record MachineRecord {
    [JsonPropertyName("machine_id")]
    public required string MachineId { get; init; }

    [JsonPropertyName("hostname")]
    public required string Hostname { get; init; }

    [JsonPropertyName("os")]
    public required string Os { get; init; }

    [JsonPropertyName("arch")]
    public required string Arch { get; init; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }

    [JsonPropertyName("registered_at")]
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("last_heartbeat")]
    public DateTimeOffset LastHeartbeat { get; init; } = DateTimeOffset.UtcNow;
}

public record DaemonRecord {
    [JsonPropertyName("daemon_id")]
    public required string DaemonId { get; init; }

    [JsonPropertyName("machine_id")]
    public required string MachineId { get; init; }

    [JsonPropertyName("daemon_name")]
    public required string DaemonName { get; init; }

    [JsonPropertyName("advertised_vendors")]
    public string[] AdvertisedVendors { get; init; } = [];

    [JsonPropertyName("max_agents")]
    public int MaxAgents { get; init; } = 4;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "connected";

    [JsonPropertyName("last_seen_at")]
    public DateTimeOffset LastSeenAt { get; init; } = DateTimeOffset.UtcNow;
}

public record DeadLetterEntryRecord {
    [JsonPropertyName("entry_id")]
    public required string EntryId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("vendor")]
    public required string Vendor { get; init; }

    [JsonPropertyName("line_number")]
    public int LineNumber { get; init; }

    [JsonPropertyName("raw_line")]
    public required string RawLine { get; init; }

    [JsonPropertyName("error_reason")]
    public required string ErrorReason { get; init; }

    [JsonPropertyName("received_at")]
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
}
