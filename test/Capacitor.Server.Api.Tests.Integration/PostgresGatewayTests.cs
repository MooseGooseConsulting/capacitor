using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Capacitor.Server.Ingest;
using Capacitor.Server.Data.Entities;
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

            // The dashboard reads an evaluator-produced persisted result; it must not
            // manufacture an Evaluation tab from transcript data.
            await using (var dataSource = NpgsqlDataSource.Create(connectionString)) {
                var evaluationStore = new PostgresSessionRepository(dataSource);
                var evaluationId = $"evaluation-{Guid.NewGuid():N}";
                await evaluationStore.PersistEvalRunAsync(new EvalRunRecord {
                    EvalRunId = evaluationId,
                    SessionId = sessionId,
                    JudgeModel = "integration-judge",
                    OverallScore = 4,
                    Summary = "Persisted dashboard evaluation",
                    EvaluatedAt = DateTimeOffset.Parse("2026-01-02T03:05:05Z", CultureInfo.InvariantCulture)
                }, [new EvalVerdictRecord {
                    EvalRunId = evaluationId,
                    Category = "quality",
                    QuestionId = "session-read-model",
                    Score = 4,
                    Verdict = "pass",
                    Finding = "The persisted evaluation is available to the browser API."
                }]);
            }

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
                var traceEntries = detailDocument.RootElement.GetProperty("trace").GetProperty("entries");
                await Assert.That(traceEntries.GetArrayLength()).IsGreaterThan(0);
                var firstTurn = traceEntries.EnumerateArray()
                    .First(entry => entry.GetProperty("kind").GetString() == "turn")
                    .GetProperty("turn");
                await Assert.That(firstTurn.GetProperty("turn_index").GetInt32()).IsEqualTo(0);
                var evaluation = detailDocument.RootElement.GetProperty("evaluation");
                await Assert.That(evaluation.GetProperty("run").GetProperty("summary").GetString())
                    .IsEqualTo("Persisted dashboard evaluation");
                await Assert.That(evaluation.GetProperty("verdicts")[0].GetProperty("question_id").GetString())
                    .IsEqualTo("session-read-model");
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
                await Assert.That(turnsDocument.RootElement[0].GetProperty("turn_index").GetInt32()).IsEqualTo(0);
            }

            using (var turnDocument = JsonDocument.Parse(await turn.Content.ReadAsStringAsync())) {
                var trace = turnDocument.RootElement.GetProperty("trace");
                await Assert.That(trace.EnumerateArray().Any(entry =>
                    entry.GetProperty("kind").GetString() == "assistant_message"
                    && entry.GetProperty("text").GetString() == "stored result")).IsTrue();
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
                await Assert.That(aliceReader.GetString(1)).IsEqualTo("private");
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

    [Test, NotInParallel]
    public async Task Gateway_retries_rejected_source_coordinates_and_replaces_the_rejected_receipt() {
        var connectionString = RequireRecoveryConnectionString();
        var sessionId = $"retry-{Guid.NewGuid():N}";
        using var environment = EnvScope.Exclusive("ConnectionStrings__Capacitor", connectionString);

        try {
            using var factory = new GatewayFactory();
            using var client = factory.CreateClient();
            await Assert.That((await client.PostAsJsonAsync("/hooks/session-start", new {
                session_id = sessionId, user_id = "integration-test"
            })).StatusCode).IsEqualTo(HttpStatusCode.OK);

            await Assert.That((await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = sessionId, vendor = "claude", lines = new[] { "{" }, line_numbers = new[] { 1 }
            })).StatusCode).IsEqualTo(HttpStatusCode.OK);

            await Assert.That((await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = sessionId, vendor = "claude",
                lines = new[] { """{"type":"user","timestamp":"2026-01-01T00:00:01Z","message":{"role":"user","content":"corrected"}}""" },
                line_numbers = new[] { 1 }
            })).StatusCode).IsEqualTo(HttpStatusCode.OK);

            await Assert.That((await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = sessionId, vendor = "claude",
                lines = new[] { """{"type":"system","timestamp":"2026-01-01T00:00:02Z","subtype":"init"}""" },
                line_numbers = new[] { 2 }
            })).StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await client.GetFromJsonAsync<WatermarkResponse>($"/api/sessions/{sessionId}/last-line"))!.LastLineNumber).IsEqualTo(2);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(@"
                SELECT event_type FROM session_events
                WHERE session_id = $1 AND agent_id = '' AND line_number = 1;", connection);
            command.Parameters.AddWithValue(sessionId);
            await Assert.That((string?)await command.ExecuteScalarAsync()).IsEqualTo("UserMessage");

            await using var receipt = new NpgsqlCommand(@"
                SELECT normalization_status, raw_payload
                FROM transcript_receipts
                WHERE session_id = $1 AND agent_id = '' AND line_number = 1;", connection);
            receipt.Parameters.AddWithValue(sessionId);
            await using var receiptReader = await receipt.ExecuteReaderAsync();
            await Assert.That(await receiptReader.ReadAsync()).IsTrue();
            await Assert.That(receiptReader.GetString(0)).IsEqualTo("accepted");
            await Assert.That(receiptReader.GetString(1)).Contains("corrected");
        } finally {
            await DeleteTestSessionAsync(connectionString, sessionId);
        }
    }

    [Test, NotInParallel]
    public async Task Gateway_preserves_zero_event_receipts_and_projects_all_event_repositories() {
        var connectionString = RequireRecoveryConnectionString();
        var sessionId = $"repository-evidence-{Guid.NewGuid():N}";
        using var environment = EnvScope.Exclusive("ConnectionStrings__Capacitor", connectionString);

        try {
            using var factory = new GatewayFactory();
            using var client = factory.CreateClient();
            await Assert.That((await client.PostAsJsonAsync("/hooks/session-start", new {
                session_id = sessionId,
                user_id = "integration-test",
                repository = new { owner = "example", repo_name = "launch-repository" }
            })).StatusCode).IsEqualTo(HttpStatusCode.OK);

            // Claude metadata is a valid source line but deliberately emits no display event.
            await Assert.That((await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = sessionId,
                vendor = "claude",
                cwd = "C:\\work\\repo-a",
                repository = new { owner = "example", repo_name = "repo-a" },
                lines = new[] {
                    """{"type":"user","isMeta":true,"timestamp":"2026-01-01T00:00:00Z","message":{"role":"user","content":"metadata only"}}"""
                },
                line_numbers = new[] { 0 }
            })).StatusCode).IsEqualTo(HttpStatusCode.OK);

            await Assert.That((await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = sessionId,
                vendor = "claude",
                cwd = "C:\\work\\repo-a",
                repository = new { owner = "example", repo_name = "repo-a" },
                lines = new[] {
                    """{"type":"user","timestamp":"2026-01-01T00:00:01Z","message":{"role":"user","content":"one event in repo A"}}"""
                },
                line_numbers = new[] { 1 }
            })).StatusCode).IsEqualTo(HttpStatusCode.OK);

            // One source line produces two events in repo B, making it the primary
            // association by observed event count rather than the launch repository.
            await Assert.That((await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = sessionId,
                vendor = "antigravity",
                cwd = "C:\\work\\repo-b",
                repository = new { owner = "example", repo_name = "repo-b" },
                lines = new[] {
                    """{"type":"PLANNER_RESPONSE","created_at":"2026-01-01T00:00:02Z","content":"two events in repo B","thinking":"reasoning"}"""
                },
                line_numbers = new[] { 2 }
            })).StatusCode).IsEqualTo(HttpStatusCode.OK);

            foreach (var repository in new[] { "example/repo-a", "example/repo-b", "example/launch-repository" }) {
                using var search = JsonDocument.Parse(await (await client.GetAsync(
                    $"/api/sessions/search?repo={Uri.EscapeDataString(repository)}")).Content.ReadAsStringAsync());
                await Assert.That(search.RootElement.GetProperty("hits").EnumerateArray().Any(hit =>
                    hit.GetProperty("session_id").GetString() == sessionId)).IsTrue();
            }

            using (var detail = JsonDocument.Parse(await (await client.GetAsync($"/api/sessions/{sessionId}")).Content.ReadAsStringAsync())) {
                await Assert.That(detail.RootElement.GetProperty("session").GetProperty("repo_name").GetString())
                    .IsEqualTo("repo-b");
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(@"
                SELECT
                    (SELECT normalization_status FROM transcript_receipts WHERE session_id = $1 AND line_number = 0),
                    (SELECT raw_payload FROM transcript_receipts WHERE session_id = $1 AND line_number = 0),
                    (SELECT cwd FROM session_events WHERE session_id = $1 AND line_number = 1),
                    (SELECT COUNT(*) FROM session_repositories WHERE session_id = $1),
                    (SELECT repo_name FROM session_repositories WHERE session_id = $1 AND is_primary);", connection);
            command.Parameters.AddWithValue(sessionId);
            await using var reader = await command.ExecuteReaderAsync();
            await Assert.That(await reader.ReadAsync()).IsTrue();
            await Assert.That(reader.GetString(0)).IsEqualTo("accepted");
            await Assert.That(reader.GetString(1)).Contains("metadata only");
            await Assert.That(reader.GetString(2)).IsEqualTo("C:\\work\\repo-a");
            await Assert.That(reader.GetInt64(3)).IsEqualTo(3L);
            await Assert.That(reader.GetString(4)).IsEqualTo("repo-b");
        } finally {
            await DeleteTestSessionAsync(connectionString, sessionId);
        }
    }

    [Test, NotInParallel]
    public async Task Gateway_uses_codex_snapshots_once_and_preserves_checkpoint_order() {
        var connectionString = RequireRecoveryConnectionString();
        var responseUsageId = $"response-usage-{Guid.NewGuid():N}";
        var staleSnapshotId = $"stale-snapshot-{Guid.NewGuid():N}";
        using var environment = EnvScope.Exclusive("ConnectionStrings__Capacitor", connectionString);

        try {
            using var factory = new GatewayFactory();
            using var client = factory.CreateClient();
            foreach (var sessionId in new[] { responseUsageId, staleSnapshotId }) {
                await Assert.That((await client.PostAsJsonAsync("/hooks/session-start", new {
                    session_id = sessionId, user_id = "integration-test"
                })).StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            await Assert.That((await client.PostAsJsonAsync("/hooks/transcript", new {
                session_id = responseUsageId, vendor = "codex",
                lines = new[] {
                    """{"timestamp":"2026-01-01T01:00:00Z","type":"response_item","payload":{"id":"msg-usage","type":"message","role":"assistant","content":[{"type":"output_text","text":"response item"}],"usage":{"input_tokens":999,"output_tokens":999}}}""",
                    """{"timestamp":"2026-01-01T01:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"output_tokens":20}}}}""",
                    """{"timestamp":"2026-01-01T01:00:02Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"output_tokens":50}}}}"""
                }, line_numbers = new[] { 0, 1, 2 }
            })).StatusCode).IsEqualTo(HttpStatusCode.OK);
            using (var detail = JsonDocument.Parse(await (await client.GetAsync($"/api/sessions/{responseUsageId}")).Content.ReadAsStringAsync())) {
                await Assert.That(detail.RootElement.GetProperty("session").GetProperty("total_tokens").GetInt64()).IsEqualTo(150L);
            }

            foreach (var (line, raw) in new[] {
                (10, """{"timestamp":"2026-01-01T01:10:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"output_tokens":50,"reasoning_output_tokens":10,"cost_usd":0.2}}}}"""),
                (9, """{"timestamp":"2026-01-01T01:09:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"output_tokens":20,"reasoning_output_tokens":8,"cost_usd":0.16}}}}"""),
                (11, """{"timestamp":"2026-01-01T01:11:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":120,"output_tokens":60,"reasoning_output_tokens":4,"cost_usd":0.1}}}}""")
            }) {
                await Assert.That((await client.PostAsJsonAsync("/hooks/transcript", new {
                    session_id = staleSnapshotId, vendor = "codex", lines = new[] { raw }, line_numbers = new[] { line }
                })).StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(@"
                SELECT total_tokens, total_cost_usd FROM sessions WHERE session_id = $1;", connection);
            command.Parameters.AddWithValue(staleSnapshotId);
            await using var reader = await command.ExecuteReaderAsync();
            await Assert.That(await reader.ReadAsync()).IsTrue();
            await Assert.That(reader.GetInt64(0)).IsEqualTo(180L);
            await Assert.That(reader.GetDecimal(1)).IsEqualTo(0.3m);
        } finally {
            await DeleteTestSessionAsync(connectionString, responseUsageId);
            await DeleteTestSessionAsync(connectionString, staleSnapshotId);
        }
    }

    [Test, NotInParallel]
    public async Task Complete_session_preserves_concurrently_projected_session_fields() {
        var connectionString = RequireRecoveryConnectionString();
        var sessionId = $"complete-{Guid.NewGuid():N}";

        try {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            var sessions = new PostgresSessionRepository(dataSource);
            await sessions.GetOrCreatePlaceholderAsync(sessionId, "claude", "integration-test");
            _ = await sessions.GetSessionAsync(sessionId); // The stale read which completion must not write back.

            await using (var connection = await dataSource.OpenConnectionAsync()) {
                await using var concurrentProjection = new NpgsqlCommand(@"
                    UPDATE sessions SET visibility = 'project', next_session_id = 'preserved-next',
                        event_count = 72, tool_count = 21, total_tokens = 123, total_cost_usd = 5.25
                    WHERE session_id = $1;", connection);
                concurrentProjection.Parameters.AddWithValue(sessionId);
                await concurrentProjection.ExecuteNonQueryAsync();
            }

            await Assert.That(await sessions.CompleteSessionAsync(sessionId,
                DateTimeOffset.Parse("2026-01-02T03:04:05Z", CultureInfo.InvariantCulture))).IsTrue();

            await using var verify = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(@"
                SELECT status, visibility, next_session_id, event_count, tool_count, total_tokens, total_cost_usd
                FROM sessions WHERE session_id = $1;", verify);
            command.Parameters.AddWithValue(sessionId);
            await using var reader = await command.ExecuteReaderAsync();
            await Assert.That(await reader.ReadAsync()).IsTrue();
            await Assert.That(reader.GetString(0)).IsEqualTo("completed");
            await Assert.That(reader.GetString(1)).IsEqualTo("project");
            await Assert.That(reader.GetString(2)).IsEqualTo("preserved-next");
            await Assert.That(reader.GetInt32(3)).IsEqualTo(72);
            await Assert.That(reader.GetInt32(4)).IsEqualTo(21);
            await Assert.That(reader.GetInt64(5)).IsEqualTo(123L);
            await Assert.That(reader.GetDecimal(6)).IsEqualTo(5.25m);
        } finally {
            await DeleteTestSessionAsync(connectionString, sessionId);
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
            "DELETE FROM session_repositories WHERE session_id = $1;",
            "DELETE FROM transcript_receipts WHERE session_id = $1;",
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
