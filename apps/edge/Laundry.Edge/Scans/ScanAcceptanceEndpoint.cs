using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Laundry.Edge.Persistence;
using Npgsql;

namespace Laundry.Edge.Scans;

public sealed record DevelopmentSource(Guid TenantId, Guid PlantId, Guid StationId, Guid DeviceId);
public sealed record LocalReceipt(Guid EventId, string Status, DateTimeOffset GatewayAcceptedAtUtc,
    string DeliveryStatus);

public static class ScanAcceptanceEndpoint
{
    public static void MapScanAcceptance(this WebApplication app, DevelopmentSource source)
    {
        app.MapPost("/api/scans", (HttpContext http, SubmissionValidator validator, PlantDbContext database,
            TimeProvider clock, ILoggerFactory logs, CancellationToken cancellationToken) =>
            AcceptAsync(http, validator, database, clock, logs, source, cancellationToken))
            .WithName("SubmitScan")
            .Accepts<JsonElement>("application/json")
            .Produces<LocalReceipt>(201).Produces<LocalReceipt>(200)
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(503);
    }

    private static async Task<IResult> AcceptAsync(HttpContext http, SubmissionValidator validator,
        PlantDbContext database, TimeProvider clock, ILoggerFactory logs, DevelopmentSource source,
        CancellationToken cancellationToken)
    {
        if (http.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
            return Results.Problem(statusCode: 403, title: "Local development access only.");
        if (!http.Request.HasJsonContentType())
            return Results.Problem(statusCode: 415, title: "Send an application/json body.");

        const int limit = 16 * 1024;
        var buffer = new byte[limit + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await http.Request.Body.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        if (count > limit) return Results.Problem(statusCode: 413, title: "Scan body exceeds 16 KiB.");

        LocalObservation scan;
        Guid correlationId;
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
            var json = document.RootElement;
            if (!validator.IsValid(json)) return InvalidScan();
            if (json.GetProperty("tenantId").GetGuid() != source.TenantId ||
                json.GetProperty("plantId").GetGuid() != source.PlantId ||
                json.GetProperty("stationId").GetGuid() != source.StationId ||
                json.GetProperty("deviceId").GetGuid() != source.DeviceId)
                return Results.Problem(statusCode: 403, title: "Scan source is outside the configured development scope.");

            // Match the cloud storage adapter's representation limits before accepting locally.
            _ = json.GetProperty("observedAtUtc").GetDateTimeOffset();
            if (json.GetProperty("identifier").GetProperty("value").GetString()!.Contains('\0'))
                return InvalidScan();
            correlationId = json.GetProperty("correlationId").GetGuid();
            var now = clock.GetUtcNow().ToUniversalTime();
            // PostgreSQL stores microseconds. Use the same precision in the event and receipt.
            var acceptedAt = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
            var acceptedEvent = JsonNode.Parse(json.GetRawText())!.AsObject();
            acceptedEvent["gatewayAcceptedAtUtc"] = acceptedAt.UtcDateTime.ToString("O");
            scan = new LocalObservation
            {
                EventId = json.GetProperty("eventId").GetGuid(),
                TenantId = source.TenantId,
                PlantId = source.PlantId,
                AcceptedAtUtc = acceptedAt,
                SubmissionJson = json.GetRawText(),
                EventJson = acceptedEvent.ToJsonString()
            };
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return InvalidScan();
        }

        var logger = logs.CreateLogger("LocalScanAcceptance");
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var inserted = await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO plant.observations
                    (event_id, tenant_id, plant_id, accepted_at_utc, submission_json, event_json)
                VALUES ({scan.EventId}, {scan.TenantId}, {scan.PlantId}, {scan.AcceptedAtUtc},
                        {scan.SubmissionJson}, {scan.EventJson})
                ON CONFLICT (event_id) DO NOTHING
                """, cancellationToken);
            if (inserted == 1)
            {
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO plant.outbox (event_id, status) VALUES ({scan.EventId}, {"pending"})
                    """, cancellationToken);
            }

            var stored = await database.Observations.AsNoTracking().SingleOrDefaultAsync(x =>
                x.EventId == scan.EventId && x.TenantId == source.TenantId && x.PlantId == source.PlantId,
                cancellationToken);
            if (stored is null || !SamePayload(stored.SubmissionJson, scan.SubmissionJson))
            {
                logger.LogWarning("Local scan conflict: event {EventId}, correlation {CorrelationId}.", scan.EventId, correlationId);
                return Results.Problem(statusCode: 409, title: "Event ID conflicts with an existing observation.");
            }
            var delivery = await database.Outbox.AsNoTracking().SingleAsync(x => x.EventId == stored.EventId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var status = inserted == 1 ? "acceptedLocally" : "alreadyAcceptedLocally";
            logger.LogInformation("Local scan {EventId}, correlation {CorrelationId}: {Status}.", scan.EventId, correlationId, status);
            return Results.Json(new LocalReceipt(stored.EventId, status, stored.AcceptedAtUtc, delivery.Status),
                statusCode: inserted == 1 ? 201 : 200);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            logger.LogWarning("Local scan storage unavailable: event {EventId}, correlation {CorrelationId}.", scan.EventId, correlationId);
            http.Response.Headers.RetryAfter = "5";
            return Results.Problem(statusCode: 503, title: "Local storage unavailable; retry the same submission unchanged.");
        }
    }

    private static IResult InvalidScan() => Results.Problem(statusCode: 400,
        title: "Body must be a supported submit-scan v1 request without a gateway acceptance timestamp.");

    private static bool SamePayload(string left, string right)
    {
        using var original = JsonDocument.Parse(left);
        using var incoming = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(original.RootElement, incoming.RootElement);
    }
}
