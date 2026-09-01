using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Capacitor.Server.Api.Tests.Unit;

public sealed class GatewayIntegrationTests {
    [Test]
    public async Task Session_lifecycle_and_transcript_endpoints_share_the_configured_database() {
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();

        var started = await client.PostAsJsonAsync("/hooks/session-start", new {
            session_id = "gateway-session",
            user_id = "user-1",
            default_visibility = "project"
        });
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var ingested = await client.PostAsJsonAsync("/hooks/transcript", new {
            session_id = "gateway-session",
            vendor = "claude",
            lines = new[] { """{"type":"user","message":{"content":"hello"}}""" }
        });
        await Assert.That(ingested.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var watermark = await client.GetFromJsonAsync<WatermarkResponse>(
            "/api/sessions/gateway-session/last-line");
        await Assert.That(watermark).IsNotNull();
        await Assert.That(watermark!.LastLineNumber).IsEqualTo(1);
    }

    [Test]
    public async Task Session_end_requires_a_known_session() {
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/hooks/session-end", new {
            session_id = "not-yet-started"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    sealed class GatewayFactory : WebApplicationFactory<Program> {
        readonly TempDir _temp = new("server-api");

        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.UseSetting("Database:Path", _temp.PathTo("capacitor.db"));
        }

        protected override void Dispose(bool disposing) {
            _temp.Dispose();
            base.Dispose(disposing);
        }
    }

    sealed record WatermarkResponse {
        [JsonPropertyName("last_line_number")]
        public int LastLineNumber { get; init; }
    }
}
