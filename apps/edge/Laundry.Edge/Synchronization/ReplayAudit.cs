namespace Laundry.Edge.Synchronization;

public sealed record ReplayAudit
{
    public required Guid RequestId { get; init; }
    public required Guid EventId { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid PlantId { get; init; }
    public required int PreviousAttempts { get; init; }
    public string? PreviousError { get; init; }
    public required string ReasonCode { get; init; }
    public required string Actor { get; init; }
    public required DateTimeOffset RequestedAtUtc { get; init; }
}
