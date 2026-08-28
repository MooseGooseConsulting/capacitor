using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class AuthProxyEndpointTests {
    [Test]
    [NotInParallel(nameof(AuthProxyEndpointTests))]
    public async Task Returns_default_when_env_var_is_unset() {
        Environment.SetEnvironmentVariable("KCAP_AUTH_PROXY_URL", null);
        try {
            await Assert.That(AuthProxyEndpoint.Url).IsEqualTo(AuthProxyEndpoint.DefaultUrl);
            await Assert.That(AuthProxyEndpoint.IsConfigured).IsFalse();
            await Assert.That(AuthProxyEndpoint.IsConfigured).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("KCAP_AUTH_PROXY_URL", null);
        }
    }

    [Test]
    [NotInParallel(nameof(AuthProxyEndpointTests))]
    public async Task Uses_env_var_when_set() {
        Environment.SetEnvironmentVariable("KCAP_AUTH_PROXY_URL", "https://local-proxy.test/");
        try {
            await Assert.That(AuthProxyEndpoint.Url).IsEqualTo("https://local-proxy.test");
            await Assert.That(AuthProxyEndpoint.IsConfigured).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("KCAP_AUTH_PROXY_URL", null);
        }
    }
}
