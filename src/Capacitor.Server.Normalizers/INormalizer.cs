using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Normalizers;

public interface INormalizer {
    string VendorKey { get; }
    bool CanNormalize(string vendor);
    SessionEventRecord NormalizeLine(string sessionId, string? agentId, int lineNumber, string rawLine);
}
