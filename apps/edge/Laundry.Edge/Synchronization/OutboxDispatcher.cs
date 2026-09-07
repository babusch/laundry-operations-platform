using System.Text.Json;
using Laundry.Edge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Edge.Synchronization;

public sealed class OutboxDispatcher(PlantDbContext database, CloudDelivery cloud, TimeProvider clock,
    ILogger<OutboxDispatcher> logger)
{
    public async Task<bool> DispatchOneAsync(ForwardingSettings settings, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var leaseId = Guid.NewGuid();
        Guid eventId;
        int attempts;
        string payload;
        // Short row-lock transaction claims work, then releases all locks before network I/O.
        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            var rows = await database.Outbox.FromSqlInterpolated($"""
                SELECT o.* FROM plant.outbox o JOIN plant.observations s ON s.event_id = o.event_id
                WHERE o.status = 'pending' AND s.tenant_id = {settings.TenantId} AND s.plant_id = {settings.PlantId}
                  AND (o.next_attempt_at_utc IS NULL OR o.next_attempt_at_utc <= {now})
                  AND (o.lease_until_utc IS NULL OR o.lease_until_utc <= {now})
                ORDER BY s.accepted_at_utc, o.event_id LIMIT 1 FOR UPDATE OF o SKIP LOCKED
                """).AsNoTracking().ToListAsync(cancellationToken);
            if (rows.Count == 0) return false;
            var entry = rows[0];
            eventId = entry.EventId;
            attempts = entry.Attempts == int.MaxValue ? int.MaxValue : entry.Attempts + 1;
            payload = await database.Observations.Where(x => x.EventId == eventId)
                .Select(x => x.EventJson).SingleAsync(cancellationToken);
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE plant.outbox SET lease_id = {leaseId}, lease_until_utc = {now.AddSeconds(30)}, attempts = {attempts}
                WHERE event_id = {eventId}
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var result = await cloud.SendAsync(settings.Endpoint, eventId, payload, cancellationToken);
        DateTimeOffset? nextAttempt = result.Status == "pending"
            ? clock.GetUtcNow() + RetryDelay(attempts, result.RetryAfter) : null;
        // A reclaimed lease fences off a stale sender, including after a process restart.
        var updated = await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE plant.outbox SET status = {result.Status}, next_attempt_at_utc = {nextAttempt},
                lease_id = NULL, lease_until_utc = NULL, cloud_received_at_utc = {result.CloudReceivedAtUtc},
                last_error = {result.Error}
            WHERE event_id = {eventId} AND lease_id = {leaseId}
            """, cancellationToken);
        using var json = JsonDocument.Parse(payload);
        logger.LogInformation("Forward event {EventId}, correlation {CorrelationId}: {Status}, outcome {Outcome}, recorded {Recorded}.",
            eventId, json.RootElement.GetProperty("correlationId").GetGuid(), result.Status, result.Error, updated == 1);
        return true;
    }

    public static TimeSpan RetryDelay(int attempts, TimeSpan? hint)
    {
        var seconds = Math.Min(300, 5 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 6))) * (0.8 + Random.Shared.NextDouble() * 0.2);
        // Cap unreasonable server hints at a day, but never retry before a valid smaller hint.
        return TimeSpan.FromSeconds(Math.Max(seconds, Math.Clamp(hint?.TotalSeconds ?? 0, 0, 86400)));
    }
}
