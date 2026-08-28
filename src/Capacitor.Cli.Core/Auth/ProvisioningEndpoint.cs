namespace Capacitor.Cli.Core.Auth;

public static class ProvisioningEndpoint {
    public const string DefaultUrl = ""; // severed at the fork: vendor endpoint removed

    // KCAP_SIGNUP_URL is an internal dev/preview override; not documented for end users.
    public static string Url =>
        (Environment.GetEnvironmentVariable("KCAP_SIGNUP_URL") ?? DefaultUrl).TrimEnd('/');

    /// <summary>False when <see cref="Url"/> is empty or not an absolute http(s) origin.</summary>
    public static bool IsConfigured => HttpClientExtensions.IsAcceptableUrl(Url);
}
