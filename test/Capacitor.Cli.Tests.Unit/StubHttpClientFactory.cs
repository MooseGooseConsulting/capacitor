namespace Capacitor.Cli.Tests.Unit;

/// <summary>Minimal IHttpClientFactory so the orchestrator can be constructed without DI.</summary>
sealed class StubHttpClientFactory : IHttpClientFactory {
    public HttpClient CreateClient(string name) => new();
}
