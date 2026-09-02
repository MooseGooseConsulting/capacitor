namespace Capacitor.Web.Services;

/// <summary>Connection settings for the separate Capacitor API host.</summary>
public sealed record CapacitorApiOptions {
    public const string SectionName = "CapacitorApi";

    /// <summary>Base URL of the in-repository API host; it is not a database connection string.</summary>
    public string BaseUrl { get; init; } = "http://localhost:5000";

    public Uri GetBaseAddress() {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)) {
            throw new InvalidOperationException($"{SectionName}:BaseUrl must be an absolute URL.");
        }

        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri($"{uri.AbsoluteUri}/", UriKind.Absolute);
    }
}
