namespace Laundry.Api.Integrations.Scans;

// Persistence model; the JSON Schema remains the authority for incoming messages.
public sealed record ScanObservation
{
    public required Guid EventId { get; init; }
    public required int SchemaVersion { get; init; }
    public required string EventType { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid PlantId { get; init; }
    public required Guid StationId { get; init; }
    public required Guid DeviceId { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public required DateTimeOffset GatewayAcceptedAtUtc { get; init; }
    public required DateTimeOffset CloudReceivedAtUtc { get; init; }
    public required string IdentifierTechnology { get; init; }
    public required string IdentifierValue { get; init; }
}
