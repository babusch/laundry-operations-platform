using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Laundry.Api.Orders;

public sealed partial class OrderPlanningStore
{
    public async Task<OrderNotesDefaultDetails> ReadNotesDefaultAsync(OrderPlanningScope scope, CancellationToken ct = default)
    {
        await AuthorizeAsync(scope, OrderPlanningPermissions.Read, ct);
        return await ReadNotesDefaultValueAsync(scope, ct);
    }

    // The create form needs the initial choice even when its user has create but not read.
    public async Task<OrderNotesDefaultDetails> ReadNotesDefaultForCreationAsync(OrderPlanningScope scope, CancellationToken ct = default)
    {
        await AuthorizeAsync(scope, OrderPlanningPermissions.Create, ct);
        return await ReadNotesDefaultValueAsync(scope, ct);
    }

    private async Task<OrderNotesDefaultDetails> ReadNotesDefaultValueAsync(OrderPlanningScope scope, CancellationToken ct)
    {
        await using var db = new OrderDbContext(options);
        return await db.NotesDefaults.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.PlantId == scope.PlantId)
            .Select(x => new OrderNotesDefaultDetails(x.NotesVisibleToOperators, x.Version)).SingleOrDefaultAsync(ct)
            ?? new OrderNotesDefaultDetails(false, 0);
    }

    // HTTP composition supplies a verified human; no grant is implied by this operation.
    public async Task<OrderNotesDefaultDetails> ChangeNotesDefaultAsync(OrderPlanningScope scope, Guid key,
        long expectedVersion, bool visible, CancellationToken ct = default)
    {
        await AuthorizeAsync(scope, OrderPlanningPermissions.EditNotesDefault, ct);
        if (key == Guid.Empty || expectedVersion < 0) throw new OrderPlanningException("invalid_command");
        await using var db = new OrderDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockPlantAsync(db, scope, ct);
        await using IAsyncDisposable? lease = access is ITransactionalOrderPlanningAccess transactional
            ? await transactional.BindTransactionAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), scope, ct) : null;
        await AuthorizeAsync(scope, OrderPlanningPermissions.EditNotesDefault, ct);
        var previous = await db.NotesDefaultCommands.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == scope.TenantId && x.PlantId == scope.PlantId && x.ActorIssuer == scope.ActorIssuer &&
            x.ActorSubject == scope.ActorSubject && x.IdempotencyKey == key, ct);
        if (previous is not null)
        {
            if (previous.ExpectedVersion != expectedVersion || previous.AfterVisible != visible)
                throw new OrderPlanningException("idempotency_conflict");
            return new(previous.AfterVisible, previous.ResultVersion);
        }
        var setting = await db.NotesDefaults.FromSqlInterpolated(
            $"SELECT * FROM orders.note_defaults WHERE tenant_id = {scope.TenantId} AND plant_id = {scope.PlantId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if ((setting?.Version ?? 0) != expectedVersion) throw new OrderPlanningException("version_conflict");
        var before = setting?.NotesVisibleToOperators ?? false;
        if (setting is null)
        {
            setting = new OrderNotesDefault { TenantId = scope.TenantId, PlantId = scope.PlantId, Version = 1, NotesVisibleToOperators = visible };
            db.NotesDefaults.Add(setting);
        }
        else if (before != visible)
        {
            setting.NotesVisibleToOperators = visible;
            setting.Version++;
        }
        var clock = timeProvider.GetUtcNow();
        db.NotesDefaultCommands.Add(new OrderNotesDefaultCommand
        {
            TenantId = scope.TenantId,
            PlantId = scope.PlantId,
            ActorIssuer = scope.ActorIssuer,
            ActorSubject = scope.ActorSubject,
            IdempotencyKey = key,
            ExpectedVersion = expectedVersion,
            BeforeVisible = before,
            AfterVisible = visible,
            ResultVersion = setting.Version,
            RecordedAtUtc = new DateTimeOffset(clock.Ticks - clock.Ticks % 10, TimeSpan.Zero)
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(setting.NotesVisibleToOperators, setting.Version);
    }
}
