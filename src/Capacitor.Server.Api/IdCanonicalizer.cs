namespace Capacitor.Server.Api;

public static class IdCanonicalizer {
    // UUID-shaped ids are stored dashless; opaque slugs such as `release-followup` stay verbatim.
    public static string Canonicalize(string? id) {
        if (string.IsNullOrEmpty(id)) return id ?? string.Empty;
        return Guid.TryParse(id, out var guid) ? guid.ToString("N") : id;
    }
}
