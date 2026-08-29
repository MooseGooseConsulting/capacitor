using Microsoft.Data.Sqlite;

namespace Capacitor.Server.Data;

public static class SqliteDatabaseInitializer {
    public static async Task InitializeAsync(SqliteConnection connection, CancellationToken ct = default) {
        if (connection.State != System.Data.ConnectionState.Open) {
            await connection.OpenAsync(ct);
        }

        var schemaSql = await GetEmbeddedMigrationAsync("001_initial_schema.sql", ct);
        var viewsSql = await GetEmbeddedMigrationAsync("002_analytics_views.sql", ct);
        await InitializeAsync(connection, schemaSql, viewsSql, ct);
    }

    internal static async Task InitializeAsync(
        SqliteConnection connection,
        string schemaSql,
        string viewsSql,
        CancellationToken ct = default) {
        using var transaction = connection.BeginTransaction();
        try {
            using (var cmd = connection.CreateCommand()) {
                cmd.Transaction = transaction;
                cmd.CommandText = schemaSql;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using (var cmd = connection.CreateCommand()) {
                cmd.Transaction = transaction;
                cmd.CommandText = viewsSql;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public static async Task<string> GetEmbeddedMigrationAsync(string filename, CancellationToken ct = default) {
        var baseDir = AppContext.BaseDirectory;
        var localPath = Path.Combine(baseDir, "Migrations", filename);
        if (File.Exists(localPath)) {
            return await File.ReadAllTextAsync(localPath, ct);
        }

        // Try direct source path fallback during tests
        var currentDir = Directory.GetCurrentDirectory();
        while (currentDir != null && !File.Exists(Path.Combine(currentDir, "Capacitor.slnx"))) {
            currentDir = Directory.GetParent(currentDir)?.FullName;
        }

        if (currentDir != null) {
            var fallback = Path.Combine(currentDir, "src", "Capacitor.Server.Data", "Migrations", filename);
            if (File.Exists(fallback)) {
                return await File.ReadAllTextAsync(fallback, ct);
            }
        }

        throw new FileNotFoundException($"Migration file '{filename}' not found.");
    }
}
