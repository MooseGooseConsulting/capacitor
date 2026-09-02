using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

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
            await Assert.That(count).IsEqualTo(5L);
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
            await Assert.That(transcript.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(events.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(turns.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(turn.StatusCode).IsEqualTo(HttpStatusCode.OK);

            using (var turnsDocument = JsonDocument.Parse(await turns.Content.ReadAsStringAsync())) {
                await Assert.That(turnsDocument.RootElement.ValueKind).IsEqualTo(JsonValueKind.Array);
                await Assert.That(turnsDocument.RootElement.GetArrayLength()).IsGreaterThan(0);
            }

            var titled = await client.PostAsJsonAsync("/hooks/set-title", new { session_id = sessionId, title = "Stored title" });
            await Assert.That(titled.StatusCode).IsEqualTo(HttpStatusCode.OK);

        } finally {
            await DeleteTestSessionAsync(connectionString, sessionId);
        }
    }

    [Test, NotInParallel]
    public async Task Gateway_preserves_session_contracts_and_recovery_watermarks() {
        var connectionString = RequireRecoveryConnectionString();
        var predecessorId = Guid.NewGuid().ToString("N");
        var childId = Guid.NewGuid().ToString("N");
        var aliceId = $"alice-{Guid.NewGuid():N}";
        var bobId = $"bob-{Guid.NewGuid():N}";
        var rejectedId = $"rejected-{Guid.NewGuid():N}";
        var codexId = $"codex-{Guid.NewGuid():N}";
        var multiAgentId = $"multi-{Guid.NewGuid():N}";
        using var environment = EnvScope.Exclusive("ConnectionStrings__Capacitor", connectionString);

        try {
            using var factory = new GatewayFactory();
            using var client = factory.CreateClient();

            foreach (var (id, owner) in new[] { (predecessorId, "owner-chain"), (aliceId, "alice"), (bobId, "bob"), (rejectedId, "owner-rejected"), (codexId, "owner-codex"), (multiAgentId, "owner-multi") }) {
                var response = await client.PostAsJsonAsync("/hooks/session-start", new { session_id = id, user_id = owner });
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
            var childStarted = await client.PostAsJsonAsync("/hooks/session-start", new {
                session_id = childId,
                user_id = "owner-chain",
                previous_session_id = Guid.ParseExact(predecessorId, "N").ToString("D")
            });
            await Assert.That(childStarted.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var aliceSearch = await client.GetAsync("/api/sessions/search?author=alice");
            await Assert.That(aliceSearch.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using (var search = JsonDocument.Parse(await aliceSearch.Content.ReadAsStringAsync())) {
                var ids = search.RootElement.GetProperty("hits").EnumerateArray()
                    .Select(hit => hit.GetProperty("session_id").GetString()).ToArray();
                await Assert.That(ids).Contains(aliceId);
                await Assert.That(ids).DoesNotContain(bobId);
            }

            var ended = await client.PostAsJsonAsync("/hooks/session-end", new { session_id = aliceId });
            await Assert.That(ended.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var visibility = await client.PutAsJsonAsync($"/api/sessions/{aliceId}/visibility", new { visibility = "none" });
            await Assert.That(visibility.StatusCode).IsEqualTo(HttpStatusCode.NotImplemented);

            var rejected = await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = rejectedId,
                vendor = "claude",
                lines = new[] {
                    """{"type":"user","timestamp":"2026-01-01T00:00:00Z","message":{"role":"user","content":"first"}}""",
                    "{",
                    """{"type":"user","timestamp":"2026-01-01T00:00:02Z","message":{"role":"user","content":"third"}}"""
                },
                line_numbers = new[] { 0, 1, 2 }
            });
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await client.GetFromJsonAsync<WatermarkResponse>($"/api/sessions/{rejectedId}/last-line"))!.LastLineNumber).IsEqualTo(0);

            var later = await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = rejectedId, vendor = "claude",
                lines = new[] { """{"type":"user","timestamp":"2026-01-01T00:00:03Z","message":{"role":"user","content":"later"}}""" },
                line_numbers = new[] { 3 }
            });
            await Assert.That(later.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await client.GetFromJsonAsync<WatermarkResponse>($"/api/sessions/{rejectedId}/last-line"))!.LastLineNumber).IsEqualTo(0);

            var retry = await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = rejectedId, vendor = "claude",
                lines = new[] { """{"type":"user","timestamp":"2026-01-01T00:00:01Z","message":{"role":"user","content":"second"}}""" },
                line_numbers = new[] { 1 }
            });
            await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var resumed = await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = rejectedId, vendor = "claude",
                lines = new[] {
                    """{"type":"user","timestamp":"2026-01-01T00:00:02Z","message":{"role":"user","content":"third"}}""",
                    """{"type":"user","timestamp":"2026-01-01T00:00:03Z","message":{"role":"user","content":"later"}}"""
                }, line_numbers = new[] { 2, 3 }
            });
            await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await client.GetFromJsonAsync<WatermarkResponse>($"/api/sessions/{rejectedId}/last-line"))!.LastLineNumber).IsEqualTo(3);

            var tokenCounts = await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = codexId, vendor = "codex",
                lines = new[] {
                    """{"timestamp":"2026-01-01T01:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"output_tokens":20}}}}""",
                    """{"timestamp":"2026-01-01T01:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"output_tokens":50}}}}"""
                }, line_numbers = new[] { 0, 1 }
            });
            await Assert.That(tokenCounts.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using (var detail = JsonDocument.Parse(await (await client.GetAsync($"/api/sessions/{codexId}")).Content.ReadAsStringAsync())) {
                await Assert.That(detail.RootElement.GetProperty("session").GetProperty("total_tokens").GetInt64()).IsEqualTo(150L);
            }

            var subagentStarted = await client.PostAsJsonAsync("/hooks/subagent-start", new {
                session_id = multiAgentId,
                agent_id = "child-agent",
                agent_type = "code-reviewer",
                role = "review",
                prompt = "inspect the migration",
                started_at = "2026-01-01T02:00:00Z"
            });
            await Assert.That(subagentStarted.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var parentEvents = await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = multiAgentId, vendor = "claude", agent_id = "parent-agent",
                lines = new[] { """{"type":"user","timestamp":"2026-01-01T02:00:00Z","message":{"role":"user","content":"parent first"}}""" },
                line_numbers = new[] { 40 }
            });
            var childEvents = await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = multiAgentId, vendor = "claude", agent_id = "child-agent",
                lines = new[] { """{"type":"user","timestamp":"2026-01-01T02:00:01Z","message":{"role":"user","content":"child later"}}""" },
                line_numbers = new[] { 0 }
            });
            await Assert.That(parentEvents.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(childEvents.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var subagentStopped = await client.PostAsJsonAsync("/hooks/subagent-stop", new {
                session_id = multiAgentId,
                agent_id = "child-agent",
                stopped_at = "2026-01-01T02:00:02Z",
                exit_status = "completed"
            });
            await Assert.That(subagentStopped.StatusCode).IsEqualTo(HttpStatusCode.OK);

            foreach (var path in new[] { $"/api/sessions/{multiAgentId}/events", $"/api/sessions/{multiAgentId}/transcript" }) {
                using var ordered = JsonDocument.Parse(await (await client.GetAsync(path)).Content.ReadAsStringAsync());
                var orderedAgents = ordered.RootElement.GetProperty("events").EnumerateArray()
                    .Select(@event => @event.GetProperty("agent_id").GetString()).ToArray();
                await Assert.That(orderedAgents.Length).IsEqualTo(2);
                await Assert.That(orderedAgents[0]).IsEqualTo("parent-agent");
                await Assert.That(orderedAgents[1]).IsEqualTo("child-agent");
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT session_id, previous_session_id, next_session_id, status, visibility FROM sessions WHERE session_id = $1 OR session_id = $2 ORDER BY session_id;", connection);
            command.Parameters.AddWithValue(predecessorId);
            command.Parameters.AddWithValue(childId);
            var links = new Dictionary<string, (string? Previous, string? Next, string Status, string Visibility)>();
            await using (var reader = await command.ExecuteReaderAsync()) {
                while (await reader.ReadAsync()) {
                    links[reader.GetString(0)] = (reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4));
                }
            }
            await Assert.That(links[childId].Previous).IsEqualTo(predecessorId);
            await Assert.That(links[predecessorId].Next).IsEqualTo(childId);

            await using var aliceState = new NpgsqlCommand("SELECT status, visibility FROM sessions WHERE session_id = $1;", connection);
            aliceState.Parameters.AddWithValue(aliceId);
            await using (var aliceReader = await aliceState.ExecuteReaderAsync()) {
                await Assert.That(await aliceReader.ReadAsync()).IsTrue();
                await Assert.That(aliceReader.GetString(0)).IsEqualTo("completed");
                await Assert.That(aliceReader.GetString(1)).IsEqualTo("project");
            }

            await using var subagentState = new NpgsqlCommand(@"
                SELECT agent_type, role, prompt, stopped_at, duration_ms, exit_status
                FROM subagent_runs WHERE parent_session_id = $1 AND agent_id = $2;", connection);
            subagentState.Parameters.AddWithValue(multiAgentId);
            subagentState.Parameters.AddWithValue("child-agent");
            await using var subagentReader = await subagentState.ExecuteReaderAsync();
            await Assert.That(await subagentReader.ReadAsync()).IsTrue();
            await Assert.That(subagentReader.GetString(0)).IsEqualTo("code-reviewer");
            await Assert.That(subagentReader.GetString(1)).IsEqualTo("review");
            await Assert.That(subagentReader.GetString(2)).IsEqualTo("inspect the migration");
            await Assert.That(DateTimeOffset.Parse(subagentReader.GetString(3), CultureInfo.InvariantCulture)).IsEqualTo(
                DateTimeOffset.Parse("2026-01-01T02:00:02Z", CultureInfo.InvariantCulture));
            await Assert.That(subagentReader.GetInt64(4)).IsEqualTo(2000L);
            await Assert.That(subagentReader.GetString(5)).IsEqualTo("completed");
        } finally {
            foreach (var sessionId in new[] { predecessorId, childId, aliceId, bobId, rejectedId, codexId, multiAgentId }) {
                await DeleteTestSessionAsync(connectionString, sessionId);
            }
        }
    }

    private static async Task DeleteTestSessionAsync(string connectionString, string sessionId) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var cleanupStatements = new[] {
            "DELETE FROM subagent_runs WHERE parent_session_id = $1;",
            "DELETE FROM eval_verdicts WHERE eval_run_id IN (SELECT eval_run_id FROM eval_runs WHERE session_id = $1);",
            "DELETE FROM eval_runs WHERE session_id = $1;",
            "DELETE FROM work_item_sessions WHERE session_id = $1;",
            "DELETE FROM dead_letter_entries WHERE session_id = $1;",
            "DELETE FROM session_usage_checkpoints WHERE session_id = $1;",
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
