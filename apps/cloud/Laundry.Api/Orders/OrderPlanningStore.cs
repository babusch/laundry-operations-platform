using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Laundry.Api.Orders;

// HTTP composition must supply verified human access and current customer eligibility.
// Each operation owns a fresh context so a rejected/failed transaction cannot contaminate a retry.
public sealed class OrderPlanningStore(DbContextOptions<OrderDbContext> options,
    IOrderPlanningAccess access, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<OrderPlanningReceipt> CreateAsync(OrderPlanningScope scope, Guid key, OrderPlan plan,
        CancellationToken cancellationToken = default) => SaveAsync(scope, key, null, null, plan, cancellationToken);

    public Task<OrderPlanningReceipt> UpdateAsync(OrderPlanningScope scope, Guid key, Guid orderId,
        long expectedVersion, OrderPlan plan, CancellationToken cancellationToken = default) =>
        SaveAsync(scope, key, orderId, expectedVersion, plan, cancellationToken);

    public async Task<ServiceOrder?> GetAsync(OrderPlanningScope scope, Guid orderId,
        CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(scope, OrderPlanningPermissions.Read, cancellationToken);
        await using var db = new OrderDbContext(options);
        return await db.Orders.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == scope.TenantId && x.PlantId == scope.PlantId && x.Id == orderId, cancellationToken);
    }

    private async Task<OrderPlanningReceipt> SaveAsync(OrderPlanningScope scope, Guid key, Guid? orderId,
        long? expectedVersion, OrderPlan input, CancellationToken ct)
    {
        await AuthorizeAsync(scope, orderId.HasValue ? OrderPlanningPermissions.EditPlan : OrderPlanningPermissions.Create, ct);
        if (key == Guid.Empty || orderId == Guid.Empty || expectedVersion <= 0)
        {
            throw new OrderPlanningException("invalid_command");
        }

        var plan = input.Normalize();
        var operation = orderId.HasValue ? "update_plan" : "create";
        var target = orderId?.ToString("D") ?? "orders";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { operation, target, expectedVersion, plan }, JsonOptions))));

        await using var db = new OrderDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Conservative per-plant write serialization for this small slice. Locks are transaction-bound.
        // Hash collisions can reduce concurrency, never weaken scope checks or overwrite data.
        var lockKey = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"order-planning:{scope.TenantId:D}:{scope.PlantId:D}")));
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);
        await using IAsyncDisposable? accessLease = access is ITransactionalOrderPlanningAccess transactionalAccess
            ? await transactionalAccess.BindTransactionAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), scope, ct)
            : null;
        // Recheck after waiting for another writer, including before returning a saved receipt.
        await AuthorizeAsync(scope, orderId.HasValue ? OrderPlanningPermissions.EditPlan : OrderPlanningPermissions.Create, ct);
        var previous = await db.CommandReceipts.SingleOrDefaultAsync(x =>
            x.TenantId == scope.TenantId && x.PlantId == scope.PlantId &&
            x.ActorIssuer == scope.ActorIssuer && x.ActorSubject == scope.ActorSubject && x.IdempotencyKey == key, ct);
        if (previous is not null)
        {
            if (previous.RequestFingerprint != fingerprint)
            {
                throw new OrderPlanningException("idempotency_conflict");
            }

            // Return the original acknowledgement even if the order/customer has since changed.
            // Current human permission was checked above; never reapply the historic plan.
            return previous.Result();
        }

        if (!await access.IsCustomerEligibleAsync(scope, plan.CustomerId, ct))
        {
            throw new OrderPlanningException("customer_not_eligible");
        }

        var clock = timeProvider.GetUtcNow();
        // Match PostgreSQL's microsecond precision in both current state and stored receipts.
        var now = new DateTimeOffset(clock.Ticks - clock.Ticks % 10, TimeSpan.Zero);
        ServiceOrder order;
        OrderPlan? before = null;
        if (orderId.HasValue)
        {
            // Row lock also coordinates with future lifecycle writers that do not use our plant lock.
            order = await db.Orders.FromSqlInterpolated($"SELECT * FROM orders.service_orders WHERE tenant_id = {scope.TenantId} AND plant_id = {scope.PlantId} AND id = {orderId.Value} FOR UPDATE")
                .SingleOrDefaultAsync(ct) ?? throw new OrderPlanningException("order_not_found");
            if (order.Status != "planned")
            {
                throw new OrderPlanningException("order_not_planned");
            }

            if (order.Version != expectedVersion)
            {
                throw new OrderPlanningException("version_conflict");
            }

            before = order.Plan();
            if (before != plan)
            {
                order.Apply(plan);
                order.Version++;
                order.UpdatedAtUtc = now;
                order.UpdatedByIssuer = scope.ActorIssuer;
                order.UpdatedBySubject = scope.ActorSubject;
            }
        }
        else
        {
            var counter = await db.NumberCounters.SingleOrDefaultAsync(x =>
                x.TenantId == scope.TenantId && x.PlantId == scope.PlantId, ct);
            if (counter is null)
            {
                counter = new OrderNumberCounter { TenantId = scope.TenantId, PlantId = scope.PlantId };
                db.NumberCounters.Add(counter);
            }

            counter.LastNumber++;
            order = new ServiceOrder
            {
                Id = Guid.NewGuid(),
                TenantId = scope.TenantId,
                PlantId = scope.PlantId,
                OrderNumber = "ORD-" + counter.LastNumber.ToString("D6", CultureInfo.InvariantCulture),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByIssuer = scope.ActorIssuer,
                CreatedBySubject = scope.ActorSubject,
                UpdatedByIssuer = scope.ActorIssuer,
                UpdatedBySubject = scope.ActorSubject
            };
            order.Apply(plan);
            db.Orders.Add(order);
        }

        if (before != plan)
        {
            db.AuditEntries.Add(new OrderAuditEntry
            {
                Id = Guid.NewGuid(),
                TenantId = scope.TenantId,
                PlantId = scope.PlantId,
                OrderId = order.Id,
                Action = orderId.HasValue ? "order_plan_changed" : "order_created",
                OrderVersion = order.Version,
                RecordedAtUtc = now,
                ActorIssuer = scope.ActorIssuer,
                ActorSubject = scope.ActorSubject,
                RequestId = key,
                BeforePlan = before is null ? null : JsonSerializer.Serialize(before, JsonOptions),
                AfterPlan = JsonSerializer.Serialize(plan, JsonOptions)
            });
        }

        var receipt = new OrderCommandReceipt
        {
            TenantId = scope.TenantId,
            PlantId = scope.PlantId,
            ActorIssuer = scope.ActorIssuer,
            ActorSubject = scope.ActorSubject,
            IdempotencyKey = key,
            Operation = operation,
            Target = target,
            RequestFingerprint = fingerprint,
            OrderId = order.Id,
            ResponseStatus = orderId.HasValue ? 200 : 201,
            ResponseBody = JsonSerializer.Serialize(OrderDetails.From(order), JsonOptions),
            ResponseEtag = OrderDetails.From(order).EntityTag(),
            ResponseLocation = $"/api/v1/plants/{scope.PlantId:D}/orders/{order.Id:D}",
            AcceptedAtUtc = now
        };
        db.CommandReceipts.Add(receipt);
        await db.SaveChangesAsync(ct);
        // PostgreSQL jsonb canonicalizes property order. First acceptance and retries return
        // the same stored acknowledgement, rather than differently formatted JSON strings.
        await db.Entry(receipt).ReloadAsync(ct);
        await transaction.CommitAsync(ct);
        return receipt.Result();
    }

    private async Task AuthorizeAsync(OrderPlanningScope scope, string permission, CancellationToken ct)
    {
        if (scope.TenantId == Guid.Empty || scope.PlantId == Guid.Empty ||
            string.IsNullOrWhiteSpace(scope.ActorIssuer) || string.IsNullOrWhiteSpace(scope.ActorSubject) ||
            !await access.IsAllowedAsync(scope, permission, ct))
        {
            throw new OrderPlanningException("access_denied");
        }
    }
}
