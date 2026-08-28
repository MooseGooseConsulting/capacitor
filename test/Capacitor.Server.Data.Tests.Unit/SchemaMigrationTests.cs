using Microsoft.Data.Sqlite;
using Capacitor.Server.Data.Entities;
using System.Text.Json;

namespace Capacitor.Server.Data.Tests.Unit;

public class SchemaMigrationTests {
    [Test]
    public async Task InitializeAsync_creates_all_tables_and_views_in_memory() {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await SqliteDatabaseInitializer.InitializeAsync(connection);

        // Verify tables exist
        var tables = new List<string>();
        using (var cmd = connection.CreateCommand()) {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view');";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                tables.Add(reader.GetString(0));
            }
        }

        await Assert.That(tables).Contains("session_events");
        await Assert.That(tables).Contains("session_watermarks");
        await Assert.That(tables).Contains("sessions");
        await Assert.That(tables).Contains("subagent_runs");
        await Assert.That(tables).Contains("work_items");
        await Assert.That(tables).Contains("work_item_sessions");
        await Assert.That(tables).Contains("eval_runs");
        await Assert.That(tables).Contains("eval_verdicts");
        await Assert.That(tables).Contains("judge_facts");
        await Assert.That(tables).Contains("machines");
        await Assert.That(tables).Contains("daemons");
        await Assert.That(tables).Contains("v_an_sessions");
        await Assert.That(tables).Contains("v_an_token_usage_by_model");
        await Assert.That(tables).Contains("v_an_tool_usage");
    }

    [Test]
    public async Task SessionEventRecord_serializes_and_deserializes_cleanly() {
        var record = new SessionEventRecord {
            SessionId = "70dc37b2b3b14f139c153858abbe88a8",
            AgentId = "sub-1",
            LineNumber = 1,
            LogicalSeq = 100,
            EventType = "ToolCall",
            Vendor = "claude",
            Model = "claude-3-5-sonnet",
            Timestamp = DateTimeOffset.UtcNow,
            InputTokens = 1500,
            OutputTokens = 300,
            CostUsd = 0.0045m,
            ToolName = "bash",
            ToolInput = "{\"command\":\"ls -la\"}",
            IsError = false
        };

        var json = JsonSerializer.Serialize(record);
        var deserialized = JsonSerializer.Deserialize<SessionEventRecord>(json);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized!.SessionId).IsEqualTo(record.SessionId);
        await Assert.That(deserialized.AgentId).IsEqualTo(record.AgentId);
        await Assert.That(deserialized.LineNumber).IsEqualTo(record.LineNumber);
        await Assert.That(deserialized.InputTokens).IsEqualTo(1500);
        await Assert.That(deserialized.ToolName).IsEqualTo("bash");
    }
}
