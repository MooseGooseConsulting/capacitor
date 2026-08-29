using System.Globalization;
using Microsoft.Data.Sqlite;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Ingest;

public class SqliteEventStoreRepository : IEventStoreRepository {
    private const string EventColumns = @"
        session_id, agent_id, line_number, logical_seq, event_id, event_type, vendor, model,
        timestamp, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_usd,
        tool_server, tool_name, tool_input, tool_output, tool_exit_code, is_error, content, raw_payload";

    private readonly SqliteConnection _connection;

    public SqliteEventStoreRepository(SqliteConnection connection) {
        _connection = connection;
    }

    public async Task<int> AppendEventsAsync(IReadOnlyList<SessionEventRecord> events, CancellationToken ct = default) {
        if (events.Count == 0) return 0;

        var inserted = 0;
        using var tx = _connection.BeginTransaction();
        try {
            const string sql = $@"
                INSERT INTO session_events ({EventColumns})
                VALUES (
                    $session_id, $agent_id, $line_number, $logical_seq, $event_id, $event_type, $vendor, $model,
                    $timestamp, $input_tokens, $output_tokens, $cache_read_tokens, $cache_write_tokens, $cost_usd,
                    $tool_server, $tool_name, $tool_input, $tool_output, $tool_exit_code, $is_error, $content, $raw_payload
                )
                ON CONFLICT(session_id, agent_id, line_number) DO NOTHING;
            ";

            foreach (var ev in events) {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;

                cmd.Parameters.AddWithValue("$session_id", ev.SessionId);
                cmd.Parameters.AddWithValue("$agent_id", ev.AgentId ?? string.Empty);
                cmd.Parameters.AddWithValue("$line_number", ev.LineNumber);
                cmd.Parameters.AddWithValue("$logical_seq", ev.LogicalSeq);
                cmd.Parameters.AddWithValue("$event_id", (object?)ev.EventId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$event_type", ev.EventType);
                cmd.Parameters.AddWithValue("$vendor", ev.Vendor);
                cmd.Parameters.AddWithValue("$model", (object?)ev.Model ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$timestamp", SqliteUtc.Format(ev.Timestamp));
                cmd.Parameters.AddWithValue("$input_tokens", ev.InputTokens);
                cmd.Parameters.AddWithValue("$output_tokens", ev.OutputTokens);
                cmd.Parameters.AddWithValue("$cache_read_tokens", ev.CacheReadTokens);
                cmd.Parameters.AddWithValue("$cache_write_tokens", ev.CacheWriteTokens);
                cmd.Parameters.AddWithValue("$cost_usd", ev.CostUsd);
                cmd.Parameters.AddWithValue("$tool_server", (object?)ev.ToolServer ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tool_name", (object?)ev.ToolName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tool_input", (object?)ev.ToolInput ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tool_output", (object?)ev.ToolOutput ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tool_exit_code", (object?)ev.ToolExitCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$is_error", ev.IsError ? 1 : 0);
                cmd.Parameters.AddWithValue("$content", (object?)ev.Content ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$raw_payload", (object?)ev.RawPayload ?? DBNull.Value);

                inserted += await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return inserted;
        } catch {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<SessionEventRecord>> GetEventsAsync(string sessionId, string? agentId = null, int fromLine = 0, CancellationToken ct = default) {
        var list = new List<SessionEventRecord>();
        using var cmd = _connection.CreateCommand();

        if (agentId == null) {
            cmd.CommandText = $@"
                SELECT {EventColumns}
                FROM session_events
                WHERE session_id = $session_id AND line_number >= $from_line
                ORDER BY logical_seq ASC, line_number ASC, agent_id ASC;";
            cmd.Parameters.AddWithValue("$session_id", sessionId);
            cmd.Parameters.AddWithValue("$from_line", fromLine);
        } else {
            cmd.CommandText = $@"
                SELECT {EventColumns}
                FROM session_events
                WHERE session_id = $session_id AND agent_id = $agent_id AND line_number >= $from_line
                ORDER BY line_number ASC;";
            cmd.Parameters.AddWithValue("$session_id", sessionId);
            cmd.Parameters.AddWithValue("$agent_id", agentId);
            cmd.Parameters.AddWithValue("$from_line", fromLine);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            list.Add(ReadEvent(reader));
        }

        return list;
    }

    public async Task<long> GetEventCountAsync(string sessionId, CancellationToken ct = default) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM session_events WHERE session_id = $session_id;";
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res is long count ? count : Convert.ToInt64(res, CultureInfo.InvariantCulture);
    }

    private static SessionEventRecord ReadEvent(SqliteDataReader reader) => new() {
        SessionId = reader.GetString(0),
        AgentId = reader.GetString(1),
        LineNumber = reader.GetInt32(2),
        LogicalSeq = reader.GetInt64(3),
        EventId = reader.IsDBNull(4) ? null : reader.GetString(4),
        EventType = reader.GetString(5),
        Vendor = reader.GetString(6),
        Model = reader.IsDBNull(7) ? null : reader.GetString(7),
        Timestamp = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        InputTokens = reader.GetInt64(9),
        OutputTokens = reader.GetInt64(10),
        CacheReadTokens = reader.GetInt64(11),
        CacheWriteTokens = reader.GetInt64(12),
        CostUsd = reader.GetDecimal(13),
        ToolServer = reader.IsDBNull(14) ? null : reader.GetString(14),
        ToolName = reader.IsDBNull(15) ? null : reader.GetString(15),
        ToolInput = reader.IsDBNull(16) ? null : reader.GetString(16),
        ToolOutput = reader.IsDBNull(17) ? null : reader.GetString(17),
        ToolExitCode = reader.IsDBNull(18) ? null : reader.GetInt32(18),
        IsError = reader.GetInt32(19) != 0,
        Content = reader.IsDBNull(20) ? null : reader.GetString(20),
        RawPayload = reader.IsDBNull(21) ? null : reader.GetString(21)
    };
}
