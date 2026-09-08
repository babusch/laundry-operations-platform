namespace Laundry.Edge.Synchronization;

public sealed record WorkerSnapshot(DateTimeOffset? LastIterationAtUtc, string? LastIterationError);

// Process-local heartbeat only; durable delivery evidence stays in PostgreSQL.
public sealed class WorkerDiagnostics
{
    private WorkerSnapshot _snapshot = new(null, null);
    public WorkerSnapshot Read() => Volatile.Read(ref _snapshot);
    public void Record(string? error) => Interlocked.Exchange(ref _snapshot, new(DateTimeOffset.UtcNow, error));
}
