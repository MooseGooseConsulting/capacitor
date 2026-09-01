using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Capacitor.Server.Api.Tests.Integration;

public sealed class PostgresGatewayTests {
    [Test]
    public async Task Gateway_persists_every_logical_event_to_the_recovery_postgres_database() {
        var connectionString = RequireRecoveryConnectionString();
        var sessionId = $"integration-{Guid.NewGuid():N}";

        using var factory = new GatewayFactory(connectionString);
        using var client = factory.CreateClient();

        var started = await client.PostAsJsonAsync("/hooks/session-start", new {
            session_id = sessionId,
            user_id = "integration-test",
            default_visibility = "project"
        });
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var ingested = await client.PostAsJsonAsync("/hooks/transcript", new {
            session_id = sessionId,
            vendor = "antigravity",
            lines = new[] { """{"type":"PLANNER_RESPONSE","content":"response","thinking":"reasoning"}""" }
        });
        await Assert.That(ingested.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var watermark = await client.GetFromJsonAsync<WatermarkResponse>(
            $"/api/sessions/{sessionId}/last-line");
        await Assert.That(watermark).IsNotNull();
        await Assert.That(watermark!.LastLineNumber).IsEqualTo(1);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM session_events WHERE session_id = $1;", connection);
        command.Parameters.AddWithValue(sessionId);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        await Assert.That(count).IsEqualTo(2L);
    }

    private static string RequireRecoveryConnectionString() {
        var connectionString = Environment.GetEnvironmentVariable("CAPACITOR_TEST_POSTGRES_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "CAPACITOR_TEST_POSTGRES_CONNECTION_STRING is required. Run this integration suite against Blood Arrow's capacitor_recovery database.");
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(builder.Database, "capacitor_recovery", StringComparison.Ordinal)) {
            throw new InvalidOperationException("The integration suite may only target the capacitor_recovery database.");
        }

        return connectionString;
    }

    private sealed class GatewayFactory : WebApplicationFactory<Program> {
        private readonly string _connectionString;

        public GatewayFactory(string connectionString) {
            _connectionString = connectionString;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> {
                    ["ConnectionStrings:Capacitor"] = _connectionString
                }));
        }
    }

    private sealed record WatermarkResponse {
        [JsonPropertyName("last_line_number")]
        public int LastLineNumber { get; init; }
    }
}
