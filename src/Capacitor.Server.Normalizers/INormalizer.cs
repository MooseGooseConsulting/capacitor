using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Normalizers;

public interface INormalizer {
    string VendorKey { get; }
    bool CanNormalize(string vendor);

    // One transcript line can hold several logical events (an assistant turn with text plus
    // N tool calls, a planner response with visible content plus internal thinking), so a
    // normalizer returns one envelope per event rather than collapsing them into one record.
    // `failed` is true when rawLine could not be parsed into the vendor's expected shape and
    // the normalizer fell back to a bare content record — a strict ingestion batch reads it,
    // everyone else can discard it.
    IReadOnlyList<SessionEventRecord> NormalizeLine(string vendor, string sessionId, string? agentId, int lineNumber, string rawLine, out bool failed);
}
