using Npgsql;

namespace Capacitor.Server.Data;

public static class PostgresDatabaseInitializer {
    // A transactional advisory lock prevents two concurrently-starting API pods from
    // applying the same bootstrap DDL at once.
    private const long MigrationLockId = 4_973_405_783_229_871_023;

    public static async Task InitializeAsync(NpgsqlDataSource dataSource, CancellationToken ct = default) {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var lockCommand = connection.CreateCommand()) {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "SELECT pg_advisory_xact_lock($1);";
            lockCommand.Parameters.AddWithValue(MigrationLockId);
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        await ExecuteMigrationAsync(connection, transaction, "001_initial_schema.sql", ct);
        await ExecuteMigrationAsync(connection, transaction, "003_event_logical_seq.postgres.sql", ct);
        await ExecuteMigrationAsync(connection, transaction, "004_ingest_checkpoints.postgres.sql", ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task ExecuteMigrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string filename,
        CancellationToken ct) {
        var sql = await SqliteDatabaseInitializer.GetEmbeddedMigrationAsync(filename, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
