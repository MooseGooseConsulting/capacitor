using System.Net;
using System.Text;
using Capacitor.Web.Models;
using Capacitor.Web.Services;

namespace Capacitor.Web.Tests.Unit;

public sealed class CapacitorSessionsClientTests {
    [Test]
    public async Task SearchAsync_uses_the_session_read_contract_and_returns_persisted_summaries() {
        Uri? requestedUri = null;
        using var http = new HttpClient(new StubHandler(request => {
            requestedUri = request.RequestUri;
            return JsonResponse("""
                {
                  "sessions": [{
                    "session_id": "session-1",
                    "title": "Persisted session",
                    "repo_owner": "owner",
                    "repo_name": "repo",
                    "input_tokens": 10,
                    "output_tokens": 5,
                    "total_tokens": 15
                  }],
                  "total": 1
                }
                """);
        })) {
            BaseAddress = new Uri("https://api.example/")
        };
        var client = new CapacitorSessionsClient(http);

        var result = await client.SearchAsync(new SessionSearchRequest(Query: "token budget", Repository: "owner/repo", Limit: 25));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value!.Total).IsEqualTo(1);
        await Assert.That(result.Value.Sessions.Single().Repository).IsEqualTo("owner/repo");
        await Assert.That(requestedUri!.PathAndQuery).IsEqualTo("/api/sessions/search?limit=25&offset=0&query=token%20budget&repo=owner%2Frepo");
    }

    [Test]
    public async Task GetSessionAsync_reports_an_unavailable_read_route_without_inventing_a_session() {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))) {
            BaseAddress = new Uri("https://api.example/")
        };
        var client = new CapacitorSessionsClient(http);

        var result = await client.GetSessionAsync("missing-session");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Value).IsNull();
        await Assert.That(result.Failure!.IsNotFound).IsTrue();
    }

    static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK) {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
