namespace Capacitor.Cli.Core.Auth;

public static class AuthProxyEndpoint {
    public const string DefaultUrl = ""; // severed at the fork: vendor endpoint removed

    /// <summary>
    /// What setup/login/machine print when there is no proxy to call. Discovery and M2M
    /// provisioning go through this host; without it, pass a workspace URL instead.
    /// </summary>
    public const string UnavailableHint =
        "This fork has no vendor auth proxy. Pass --server-url for a workspace you already have, or set KCAP_AUTH_PROXY_URL.";

    // KCAP_AUTH_PROXY_URL is an internal dev/test override; not documented for end users.
    public static string Url =>
        (Environment.GetEnvironmentVariable("KCAP_AUTH_PROXY_URL") ?? DefaultUrl).TrimEnd('/');

    /// <summary>False when <see cref="Url"/> is empty or not an absolute http(s) origin.</summary>
    public static bool IsConfigured => HttpClientExtensions.IsAcceptableUrl(Url);
}
