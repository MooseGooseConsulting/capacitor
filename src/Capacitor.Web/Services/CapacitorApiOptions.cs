namespace Capacitor.Web.Services;

/// <summary>Connection settings for the separate Capacitor API host.</summary>
public sealed record CapacitorApiOptions {
    public const string SectionName = "CapacitorApi";

    /// <summary>Base URL of the separately configured Capacitor API; it is not a database connection string.</summary>
    public string? BaseUrl { get; init; }

    public Uri GetBaseAddress() {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Scheme is not ("http" or "https")) {
            throw new InvalidOperationException($"{SectionName}:BaseUrl must be an absolute HTTP or HTTPS URL.");
        }

        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri($"{uri.AbsoluteUri}/", UriKind.Absolute);
    }
}
