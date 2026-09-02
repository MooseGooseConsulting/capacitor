using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Capacitor.Cli.Core;

namespace Capacitor.Server.Api.Tests.Integration;

public sealed class PostgresGatewayTests {
    [Test, NotInParallel]
    public async Task Gateway_persists_every_logical_event_to_the_recovery_postgres_database() {
        var connectionString = RequireRecoveryConnectionString();
        var sessionId = $"integration-{Guid.NewGuid():N}";
        using var environment = EnvScope.Exclusive("ConnectionStrings__Capacitor", connectionString);

        try {
            using var factory = new GatewayFactory();
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
                lines = new[] {
                    """{"type":"PLANNER_RESPONSE","content":"response","thinking":"reasoning"}""",
                    """{"type":"PLANNER_RESPONSE","content":"later response","thinking":"later reasoning"}"""
                },
                // SessionImporter omits blank source lines while retaining their original indexes.
                line_numbers = new[] { 0, 2 }
            });
            await Assert.That(ingested.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var continued = await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = sessionId,
                vendor = "antigravity",
                lines = new[] { """{"type":"PLANNER_RESPONSE","content":"final response"}""" },
                // A second sparse batch must continue from the persisted watermark, not re-scan
                // old event rows that naturally do not represent blank source lines.
                line_numbers = new[] { 4 }
            });
            await Assert.That(continued.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var watermark = await client.GetFromJsonAsync<WatermarkResponse>(
                $"/api/sessions/{sessionId}/last-line");
            await Assert.That(watermark).IsNotNull();
            await Assert.That(watermark!.LastLineNumber).IsEqualTo(4);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT COUNT(*) FROM session_events WHERE session_id = $1;", connection);
            command.Parameters.AddWithValue(sessionId);
            var count = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            await Assert.That(count).IsEqualTo(6L);
        } finally {
            await DeleteTestSessionAsync(connectionString, sessionId);
        }
    }

    [Test, NotInParallel]
    public async Task Gateway_exposes_the_postgres_backed_sessions_dashboard_read_surface() {
        var connectionString = RequireRecoveryConnectionString();
        var sessionId = $"dashboard-{Guid.NewGuid():N}";
        using var environment = EnvScope.Exclusive("ConnectionStrings__Capacitor", connectionString);

        try {
            using var factory = new GatewayFactory();
            using var client = factory.CreateClient();

            var started = await client.PostAsJsonAsync("/hooks/session-start/codex", new {
                session_id = sessionId,
                user_id = "integration-test",
                default_visibility = "project",
                started_at = "2026-01-02T03:04:05Z",
                model = "gpt-test",
                slug = "dashboard-test",
                previous_session_id = "previous-test",
                repository = new {
                    owner = "integration-owner",
                    repo_name = "integration-repo",
                    branch = "review-fix"
                }
            });
            await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var ingested = await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = sessionId,
                vendor = "antigravity",
                lines = new[] {
                    """{"type":"USER_INPUT","content":"find dashboard session"}""",
                    """{"type":"PLANNER_RESPONSE","content":"stored result","thinking":"stored reasoning","tool_calls":[{"name":"search"}]}"""
                }
            });
            await Assert.That(ingested.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var search = await client.GetAsync("/api/sessions/search?q=dashboard%20session");
            await Assert.That(search.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using (var searchDocument = JsonDocument.Parse(await search.Content.ReadAsStringAsync())) {
                var matches = searchDocument.RootElement.GetProperty("hits");
                await Assert.That(matches.EnumerateArray().Any(s =>
                    s.GetProperty("session_id").GetString() == sessionId)).IsTrue();
            }

            var detail = await client.GetAsync($"/api/sessions/{sessionId}");
            await Assert.That(detail.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using (var detailDocument = JsonDocument.Parse(await detail.Content.ReadAsStringAsync())) {
                await Assert.That(detailDocument.RootElement.GetProperty("session").GetProperty("session_id").GetString())
                    .IsEqualTo(sessionId);
                await Assert.That(detailDocument.RootElement.GetProperty("session").GetProperty("model").GetString())
                    .IsEqualTo("gpt-test");
                await Assert.That(detailDocument.RootElement.GetProperty("session").GetProperty("repo_name").GetString())
                    .IsEqualTo("integration-repo");
                await Assert.That(detailDocument.RootElement.GetProperty("events").GetArrayLength()).IsEqualTo(4);
                await Assert.That(detailDocument.RootElement.GetProperty("trace").GetProperty("entries").GetArrayLength())
                    .IsGreaterThan(0);
            }

            var transcript = await client.GetAsync($"/api/sessions/{sessionId}/transcript");
            var events = await client.GetAsync($"/api/sessions/{sessionId}/events");
            var turns = await client.GetAsync($"/api/sessions/{sessionId}/turns");
            var turn = await client.GetAsync($"/api/sessions/{sessionId}/turns/0");
            var evaluation = await client.GetAsync($"/api/sessions/{sessionId}/evaluation");
            await Assert.That(transcript.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(events.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(turns.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(turn.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(evaluation.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using (var turnsDocument = JsonDocument.Parse(await turns.Content.ReadAsStringAsync())) {
                await Assert.That(turnsDocument.RootElement.ValueKind).IsEqualTo(JsonValueKind.Array);
                await Assert.That(turnsDocument.RootElement.GetArrayLength()).IsGreaterThan(0);
            }

            var titled = await client.PostAsJsonAsync("/hooks/set-title", new { session_id = sessionId, title = "Stored title" });
            await Assert.That(titled.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var repoHash = RepoHashHelper.ComputeRepoHash("integration-owner", "integration-repo");
            var analytics = await client.PostAsJsonAsync("/api/analytics/query", new {
                sql = "SELECT session_id, logical_seq FROM v_an_session_steps ORDER BY logical_seq",
                repos = new[] { repoHash }
            });
            await Assert.That(analytics.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using (var analyticsDocument = JsonDocument.Parse(await analytics.Content.ReadAsStringAsync())) {
                await Assert.That(analyticsDocument.RootElement.GetProperty("rows").GetArrayLength()).IsGreaterThan(0);
            }
        } finally {
            await DeleteTestSessionAsync(connectionString, sessionId);
        }
    }

    private static async Task DeleteTestSessionAsync(string connectionString, string sessionId) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var cleanupStatements = new[] {
            "DELETE FROM eval_verdicts WHERE eval_run_id IN (SELECT eval_run_id FROM eval_runs WHERE session_id = $1);",
            "DELETE FROM eval_runs WHERE session_id = $1;",
            "DELETE FROM work_item_sessions WHERE session_id = $1;",
            "DELETE FROM session_events WHERE session_id = $1;",
            "DELETE FROM session_watermarks WHERE session_id = $1;",
            "DELETE FROM sessions WHERE session_id = $1;"
        };

        foreach (var cleanupStatement in cleanupStatements) {
            await using var command = new NpgsqlCommand(cleanupStatement, connection, transaction);
            command.Parameters.AddWithValue(sessionId);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static string RequireRecoveryConnectionString() {
        var connectionString = Environment.GetEnvironmentVariable("CAPACITOR_TEST_POSTGRES_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "CAPACITOR_TEST_POSTGRES_CONNECTION_STRING is required. Run this integration suite against Blood Arrow's capacitor_test database.");
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(builder.Database, "capacitor_test", StringComparison.Ordinal)) {
            throw new InvalidOperationException("The integration suite may only target the capacitor_test database.");
        }

        return connectionString;
    }

    private sealed class GatewayFactory : WebApplicationFactory<Program>;

    private sealed record WatermarkResponse {
        [JsonPropertyName("last_line_number")]
        public int LastLineNumber { get; init; }
    }
}
