using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Laundry.Api.Integrations.Scans;

public sealed record DevelopmentScanScope(Guid TenantId, Guid PlantId);
public sealed record ScanReceipt(Guid EventId, string Status, DateTimeOffset CloudReceivedAtUtc);

public static class ScanIngestionEndpoint
{
    public static void MapScanIngestion(this WebApplication app, DevelopmentScanScope source)
    {
        app.MapPost("/api/scans", (HttpContext http, ScanContractValidator validator,
            ScanDbContext database, TimeProvider clock, ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
            AcceptAsync(http, validator, database, clock, loggerFactory, source, cancellationToken))
            .WithName("ObserveScan")
            .Accepts<JsonElement>("application/json")
            .Produces<ScanReceipt>(StatusCodes.Status201Created)
            .Produces<ScanReceipt>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> AcceptAsync(HttpContext http, ScanContractValidator validator,
        ScanDbContext database, TimeProvider clock, ILoggerFactory loggerFactory,
        DevelopmentScanScope source, CancellationToken cancellationToken)
    {
        if (http.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
        {
            return Results.Problem(statusCode: 403, title: "Local development access only.");
        }

        if (!http.Request.HasJsonContentType())
        {
            return Results.Problem(statusCode: 415, title: "Send an application/json body.");
        }

        const int maxBodyBytes = 16 * 1024;
        // Also bound chunked requests, which may have no Content-Length.
        var buffer = new byte[maxBodyBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await http.Request.Body.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }

        if (count > maxBodyBytes)
        {
            return Results.Problem(statusCode: 413, title: "Scan body exceeds 16 KiB.");
        }

        ScanObservation scan;
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count),
                new JsonDocumentOptions { MaxDepth = 8 });
            var json = document.RootElement;
            if (!validator.IsValid(json))
            {
                return InvalidScan();
            }

            if (json.GetProperty("tenantId").GetGuid() != source.TenantId ||
                json.GetProperty("plantId").GetGuid() != source.PlantId)
            {
                return Results.Problem(statusCode: 403, title: "Scan source is outside the permitted tenant or plant.");
            }

            var identifier = json.GetProperty("identifier");
            var value = identifier.GetProperty("value").GetString()!;
            if (value.Contains('\0')) return InvalidScan();

            scan = new ScanObservation
            {
                EventId = json.GetProperty("eventId").GetGuid(),
                SchemaVersion = 1,
                EventType = "scan.observed",
                CorrelationId = json.GetProperty("correlationId").GetGuid(),
                TenantId = source.TenantId,
                PlantId = source.PlantId,
                StationId = json.GetProperty("stationId").GetGuid(),
                DeviceId = json.GetProperty("deviceId").GetGuid(),
                ObservedAtUtc = json.GetProperty("observedAtUtc").GetDateTimeOffset(),
                GatewayAcceptedAtUtc = json.GetProperty("gatewayAcceptedAtUtc").GetDateTimeOffset(),
                CloudReceivedAtUtc = clock.GetUtcNow(),
                IdentifierTechnology = identifier.GetProperty("technology").GetString()!,
                IdentifierValue = value,
                PayloadJson = json.GetRawText()
            };
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return InvalidScan();
        }

        var logger = loggerFactory.CreateLogger("ScanIngestion");
        try
        {
            // One atomic insert protects against concurrent retries. No check-then-insert race.
            var inserted = await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO integrations.scan_observations
                    (event_id, schema_version, event_type, correlation_id, tenant_id, plant_id,
                     station_id, device_id, observed_at_utc, gateway_accepted_at_utc,
                     cloud_received_at_utc, identifier_technology, identifier_value, payload_json)
                VALUES
                    ({scan.EventId}, {scan.SchemaVersion}, {scan.EventType}, {scan.CorrelationId},
                     {scan.TenantId}, {scan.PlantId}, {scan.StationId}, {scan.DeviceId},
                     {scan.ObservedAtUtc}, {scan.GatewayAcceptedAtUtc}, {scan.CloudReceivedAtUtc},
                     {scan.IdentifierTechnology}, {scan.IdentifierValue}, {scan.PayloadJson})
                ON CONFLICT (event_id) DO NOTHING
                """, cancellationToken);

            var stored = await database.Observations.AsNoTracking().SingleOrDefaultAsync(x =>
                x.EventId == scan.EventId && x.TenantId == source.TenantId && x.PlantId == source.PlantId,
                cancellationToken);
            if (stored?.PayloadJson is null || !SamePayload(stored.PayloadJson, scan.PayloadJson!))
            {
                logger.LogWarning("Scan conflict for event {EventId}, correlation {CorrelationId}.",
                    scan.EventId, scan.CorrelationId);
                return Results.Problem(statusCode: 409, title: "Event ID conflicts with an existing observation.");
            }

            var status = inserted == 1 ? "accepted" : "alreadyProcessed";
            logger.LogInformation("Scan {EventId}, correlation {CorrelationId}: {Status}.",
                scan.EventId, scan.CorrelationId, status);
            return Results.Json(new ScanReceipt(stored.EventId, status, stored.CloudReceivedAtUtc),
                statusCode: inserted == 1 ? 201 : 200);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            // A commit may have succeeded even if its response was lost. Retrying the ID is safe.
            logger.LogWarning("Scan storage unavailable for event {EventId}, correlation {CorrelationId}.",
                scan.EventId, scan.CorrelationId);
            http.Response.Headers.RetryAfter = "5";
            return Results.Problem(statusCode: 503, title: "Storage unavailable; retry the same event unchanged.");
        }
    }

    private static IResult InvalidScan() =>
        Results.Problem(statusCode: 400, title: "Body must be a supported scan.observed v1 observation.");

    private static bool SamePayload(string left, string right)
    {
        using var original = JsonDocument.Parse(left);
        using var incoming = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(original.RootElement, incoming.RootElement);
    }
}
