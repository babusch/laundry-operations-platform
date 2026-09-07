namespace Laundry.Edge.Scans;

// Delivery bookkeeping is separate from immutable observation evidence.
public sealed record OutboxEntry
{
    public required Guid EventId { get; init; }
    public required string Status { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAtUtc { get; init; }
    public Guid? LeaseId { get; init; }
    public DateTimeOffset? LeaseUntilUtc { get; init; }
    public DateTimeOffset? CloudReceivedAtUtc { get; init; }
    public string? LastError { get; init; }
}
