namespace Capacitor.Server.Ingest;

/// <summary>
/// Serializes access to one shared <see cref="Microsoft.Data.Sqlite.SqliteConnection"/>.
/// Microsoft.Data.Sqlite connections are not thread-safe; every HTTP request that touches
/// the singleton connection must share one gate instance. Nested waits on the same instance
/// deadlock — the semaphore is not reentrant.
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
