using Npgsql;

namespace Capacitor.Server.Data;

public static class PostgresDatabaseInitializer {
    public static async Task InitializeAsync(NpgsqlConnection connection, CancellationToken ct = default) {
        if (connection.State != System.Data.ConnectionState.Open) {
            await connection.OpenAsync(ct);
        }

        var schemaSql = await SqliteDatabaseInitializer.GetEmbeddedMigrationAsync("001_initial_schema.sql", ct);
        using (var cmd = connection.CreateCommand()) {
            cmd.CommandText = schemaSql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var viewsSql = await SqliteDatabaseInitializer.GetEmbeddedMigrationAsync("002_analytics_views.postgres.sql", ct);
        using (var cmd = connection.CreateCommand()) {
            cmd.CommandText = viewsSql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
