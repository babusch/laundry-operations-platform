namespace Laundry.Edge.Scans;

// Delivery bookkeeping is separate from immutable observation evidence.
public sealed record OutboxEntry
{
    public required Guid EventId { get; init; }
    public required string Status { get; init; }
}
