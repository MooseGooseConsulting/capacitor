namespace Capacitor.Server.Ingest;

/// <summary>
/// Serializes access to one shared <see cref="Microsoft.Data.Sqlite.SqliteConnection"/> —
/// Microsoft.Data.Sqlite connections are not thread-safe, and every repository/service in the
/// ingestion and analytics projects is wired to the same connection instance. Every caller must
/// share ONE gate instance (registered as a DI singleton) so two concurrent requests never run
/// commands on the connection at the same time; a class that awaits a gated call from inside
/// another gated call on the same instance will deadlock, since the semaphore isn't reentrant.
/// </summary>
public sealed class SqliteGate : IDisposable {
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct = default) {
        await _semaphore.WaitAsync(ct);
        try {
            return await operation();
        } finally {
            _semaphore.Release();
        }
    }

    public async Task RunAsync(Func<Task> operation, CancellationToken ct = default) {
        await _semaphore.WaitAsync(ct);
        try {
            await operation();
        } finally {
            _semaphore.Release();
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
