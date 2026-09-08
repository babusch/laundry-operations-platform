using System.Data;
using System.Net;
using System.Text.Json;
using Laundry.Edge.Persistence;
using Laundry.Edge.Scans;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Laundry.Edge.Synchronization;

public sealed record QueueItem
{
    public Guid EventId { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset AcceptedAtUtc { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAtUtc { get; init; }
    public DateTimeOffset? LeaseUntilUtc { get; init; }
    public DateTimeOffset? CloudReceivedAtUtc { get; init; }
    public string? LastError { get; init; }
}
public sealed record QueueSummary(DateTimeOffset AsOfUtc, int Pending, int Synchronized, int NeedsAttention,
    double? OldestPendingAgeSeconds, DateTimeOffset? LastCloudReceiptAtUtc, bool ForwardingEnabled, WorkerSnapshot Worker);
public sealed record ReplayRequest(Guid RequestId, int ExpectedAttempts, string ReasonCode);
public sealed record ReplayReceipt(Guid RequestId, Guid EventId, string Status, DateTimeOffset RequestedAtUtc);

public static class SyncDiagnosticsEndpoints
{
    public static void MapSyncDiagnostics(this WebApplication app, DevelopmentSource source)
    {
        var group = app.MapGroup("/api/sync");
        group.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
                return Results.Problem(statusCode: 403, title: "Local development diagnostics only.");
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException ||
                exception is InvalidOperationException { InnerException: NpgsqlException or TimeoutException })
            {
                context.HttpContext.Response.Headers.RetryAfter = "5";
                return Results.Problem(statusCode: 503, title: "Plant storage unavailable; no reliable diagnostic snapshot is available.");
            }
        });
        group.MapGet("/summary", async (PlantDbContext db, TimeProvider clock, WorkerDiagnostics worker, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            // One database snapshot keeps counts and age internally consistent during delivery.
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var queue = Query(db, source);
            var counts = await queue.GroupBy(x => x.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
            var oldest = await queue.Where(x => x.Status == "pending").Select(x => (DateTimeOffset?)x.AcceptedAtUtc).MinAsync(ct);
            var lastReceipt = await queue.Select(x => x.CloudReceivedAtUtc).MaxAsync(ct);
            await tx.CommitAsync(ct);
            int Count(string status) => counts.SingleOrDefault(x => x.Status == status)?.Count ?? 0;
            return Results.Ok(new QueueSummary(now, Count("pending"), Count("synchronized"), Count("needsAttention"),
                oldest is null ? null : Math.Max(0, (now - oldest.Value).TotalSeconds), lastReceipt,
                app.Configuration.GetValue<bool>("Forwarding:Enabled"), worker.Read()));
        }).Produces<QueueSummary>();

        group.MapGet("/events", async (string? status, int? offset, int? limit, PlantDbContext db, CancellationToken ct) =>
        {
            if (status is not (null or "pending" or "synchronized" or "needsAttention") ||
                offset is < 0 or > 1000000 || limit is < 1 or > 100)
                return Results.Problem(statusCode: 400, title: "Use a valid status, offset 0–1000000, and limit 1–100.");
            var query = Query(db, source);
            if (status is not null) query = query.Where(x => x.Status == status);
            var start = offset ?? 0;
            var size = limit ?? 50;
            var rows = await query.OrderBy(x => x.AcceptedAtUtc).ThenBy(x => x.EventId).Skip(start).Take(size + 1).ToListAsync(ct);
            return Results.Ok(new { items = rows.Take(size), nextOffset = rows.Count > size ? (int?)(start + size) : null });
        });
        group.MapGet("/events/{eventId:guid}", async (Guid eventId, PlantDbContext db, CancellationToken ct) =>
        {
            var row = await Query(db, source).SingleOrDefaultAsync(x => x.EventId == eventId, ct);
            return row is null ? Results.NotFound() : Results.Ok(row);
        }).Produces<QueueItem>().Produces(404);
        group.MapGet("/events/{eventId:guid}/replays", async (Guid eventId, int? offset, PlantDbContext db, CancellationToken ct) =>
        {
            if (offset is < 0 or > 1000000) return Results.Problem(statusCode: 400, title: "Invalid audit offset.");
            if (!await Query(db, source).AnyAsync(x => x.EventId == eventId, ct)) return Results.NotFound();
            var start = offset ?? 0;
            var rows = await db.ReplayAudits.AsNoTracking().Where(x => x.EventId == eventId &&
                x.TenantId == source.TenantId && x.PlantId == source.PlantId)
                .OrderBy(x => x.RequestedAtUtc).ThenBy(x => x.RequestId).Skip(start).Take(51).ToListAsync(ct);
            return Results.Ok(new { items = rows.Take(50), nextOffset = rows.Count > 50 ? (int?)(start + 50) : null });
        });
        group.MapPost("/events/{eventId:guid}/replay", (Guid eventId, HttpContext http, PlantDbContext db,
            TimeProvider clock, ILoggerFactory logs, CancellationToken ct) => Replay(eventId, http, db, clock, logs, source, ct))
            .Accepts<ReplayRequest>("application/json").Produces<ReplayReceipt>(202).Produces<ReplayReceipt>(200)
            .ProducesProblem(400).Produces(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(503);
    }

    private static IQueryable<QueueItem> Query(PlantDbContext db, DevelopmentSource source) =>
        from entry in db.Outbox.AsNoTracking()
        join scan in db.Observations.AsNoTracking() on entry.EventId equals scan.EventId
        where scan.TenantId == source.TenantId && scan.PlantId == source.PlantId
        select new QueueItem
        {
            EventId = scan.EventId, Status = entry.Status, AcceptedAtUtc = scan.AcceptedAtUtc, Attempts = entry.Attempts,
            NextAttemptAtUtc = entry.NextAttemptAtUtc, LeaseUntilUtc = entry.LeaseUntilUtc,
            CloudReceivedAtUtc = entry.CloudReceivedAtUtc, LastError = entry.LastError
        };

    private static async Task<IResult> Replay(Guid eventId, HttpContext http, PlantDbContext db,
        TimeProvider clock, ILoggerFactory logs, DevelopmentSource source, CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType()) return Results.Problem(statusCode: 415, title: "Send application/json.");
        var bytes = new byte[2049];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await http.Request.Body.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        if (count > 2048) return Results.Problem(statusCode: 413, title: "Replay body exceeds 2 KiB.");
        ReplayRequest request;
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 2 });
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object || json.EnumerateObject().Count() != 3 ||
                !json.TryGetProperty("requestId", out var id) || !id.TryGetGuid(out var requestId) || requestId == Guid.Empty ||
                !json.TryGetProperty("expectedAttempts", out var attempts) || !attempts.TryGetInt32(out var expected) || expected < 0 ||
                !json.TryGetProperty("reasonCode", out var reason) || reason.GetString() is not ("configurationCorrected" or "cloudIssueResolved" or "reviewedForRetry"))
                return InvalidRequest();
            request = new(requestId, expected, reason.GetString()!);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return InvalidRequest();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize replay with delivery claim/completion for this event; never steal a live lease.
        var rows = await db.Outbox.FromSqlInterpolated($"""
            SELECT o.* FROM plant.outbox o JOIN plant.observations s ON s.event_id = o.event_id
            WHERE o.event_id = {eventId} AND s.tenant_id = {source.TenantId} AND s.plant_id = {source.PlantId}
            FOR UPDATE OF o
            """).AsNoTracking().ToListAsync(ct);
        if (rows.Count == 0) return Results.NotFound();
        var previous = await db.ReplayAudits.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == request.RequestId &&
            x.TenantId == source.TenantId && x.PlantId == source.PlantId, ct);
        if (previous is not null)
        {
            if (previous.EventId != eventId || previous.PreviousAttempts != request.ExpectedAttempts || previous.ReasonCode != request.ReasonCode)
                return Conflict();
            return Results.Ok(new ReplayReceipt(previous.RequestId, eventId, "replayAlreadyRequested", previous.RequestedAtUtc));
        }
        var entry = rows[0];
        var now = clock.GetUtcNow();
        if (entry.Status != "needsAttention" || entry.Attempts != request.ExpectedAttempts || entry.LeaseUntilUtc > now)
            return Conflict();
        now = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO plant.replay_audit (request_id, event_id, tenant_id, plant_id, previous_attempts,
                previous_error, reason_code, actor, requested_at_utc)
            VALUES ({request.RequestId}, {eventId}, {source.TenantId}, {source.PlantId}, {entry.Attempts},
                {entry.LastError}, {request.ReasonCode}, {"local-development-unattributed"}, {now})
            ON CONFLICT (request_id) DO NOTHING
            """, ct);
        if (inserted == 0) return Conflict();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE plant.outbox SET status = 'pending', next_attempt_at_utc = {now},
                lease_id = NULL, lease_until_utc = NULL, last_error = NULL
            WHERE event_id = {eventId}
            """, ct);
        await transaction.CommitAsync(ct);
        logs.CreateLogger("SyncReplay").LogInformation("Replay request {RequestId} for event {EventId} queued.", request.RequestId, eventId);
        return Results.Json(new ReplayReceipt(request.RequestId, eventId, "replayRequested", now), statusCode: 202);
    }

    private static IResult InvalidRequest() => Results.Problem(statusCode: 400,
        title: "Supply a nonempty requestId UUID, nonnegative expectedAttempts, and a supported reasonCode.");
    private static IResult Conflict() => Results.Problem(statusCode: 409,
        title: "Replay conflicts with current state or an existing request. Refresh diagnostics before deciding whether to retry.");
}
