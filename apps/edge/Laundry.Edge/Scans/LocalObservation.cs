namespace Laundry.Edge.Scans;

public sealed record LocalObservation
{
    public required Guid EventId { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid PlantId { get; init; }
    public required DateTimeOffset AcceptedAtUtc { get; init; }
    public required string SubmissionJson { get; init; }
    public required string EventJson { get; init; }
}
