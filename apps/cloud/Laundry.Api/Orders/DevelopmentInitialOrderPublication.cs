using System.Data.Common;
using Laundry.Api.Customers;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Laundry.Api.Orders;

public sealed record InitialOrderPublicationBatch(int Examined, int Created, int AlreadyPresent,
    int IneligibleCustomer, int UnavailableOrder, Guid? NextAfterId);

// Local maintenance composition only. Not an HTTP service or a verified-human identity adapter.
public sealed class DevelopmentInitialOrderPublication(DbContextOptions<OrderDbContext> options, TimeProvider clock)
{
    public async Task CheckPrerequisitesAsync(CancellationToken ct = default)
    {
        await using var db = new OrderDbContext(options);
        await using var customers = new CustomerDbContext(new DbContextOptionsBuilder<CustomerDbContext>().UseNpgsql(db.Database.GetDbConnection(),
            p => p.MigrationsHistoryTable("__customer_migrations", "customers")).Options);
        if ((await db.Database.GetPendingMigrationsAsync(ct)).Any() || (await customers.Database.GetPendingMigrationsAsync(ct)).Any())
            throw new InvalidOperationException("apply_initial_publication_prerequisite_migrations");
    }

    public async Task<InitialOrderPublicationBatch> RunBatchAsync(Guid tenantId, Guid plantId,
        string recordedBy, int limit = 100, Guid? afterId = null, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || plantId == Guid.Empty || afterId == Guid.Empty || limit is < 1 or > 100 ||
            string.IsNullOrWhiteSpace(recordedBy) || recordedBy.Length > 200)
            throw new InvalidOperationException("invalid_initial_publication_request");
        await using var candidates = new OrderDbContext(options);
        var query = candidates.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.PlantId == plantId && x.Status == "planned" &&
            !candidates.PublicationHeads.Any(h => h.TenantId == tenantId && h.PlantId == plantId && h.OrderId == x.Id));
        if (afterId.HasValue) query = query.Where(x => x.Id.CompareTo(afterId.Value) > 0);
        var ids = await query.OrderBy(x => x.Id).Select(x => x.Id).Take(limit + 1).ToArrayAsync(ct);
        var created = 0; var already = 0; var ineligible = 0; var unavailable = 0;
        foreach (var id in ids.Take(limit))
        {
            await using var db = new OrderDbContext(options);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await OrderPublicationStorage.LockPlantAsync(db, tenantId, plantId, ct);
            // Check again after waiting: an ordinary save or another initialization may have won.
            if (await db.PublicationHeads.AnyAsync(x => x.TenantId == tenantId && x.PlantId == plantId && x.OrderId == id, ct))
            { already++; continue; }
            var order = await db.Orders.FromSqlInterpolated($"SELECT * FROM orders.service_orders WHERE tenant_id = {tenantId} AND plant_id = {plantId} AND id = {id} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (order is null || order.Status != "planned") { unavailable++; continue; }
            await using var customerDb = new CustomerDbContext(new DbContextOptionsBuilder<CustomerDbContext>().UseNpgsql(db.Database.GetDbConnection()).Options);
            await customerDb.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct);
            var labels = await new CustomerStore(customerDb).ReadEligibleLabelsForMutationAsync(tenantId, plantId, order.CustomerId, ct);
            if (labels is null) { ineligible++; continue; }
            var staged = await OrderPublicationStorage.StageAsync(db, tenantId, plantId, order,
                new(tenantId, labels.Id, labels.Code, labels.DisplayName), clock, onlyIfMissing: true, ct: ct);
            if (staged.Changed)
                db.PublicationOutbox.Local.Single(x => x.OrderId == id && x.TenantId == tenantId && x.PlantId == plantId).InitialPublicationActor = recordedBy;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            if (staged.Changed) created++; else already++;
        }
        return new(Math.Min(ids.Length, limit), created, already, ineligible, unavailable,
            ids.Length > limit ? ids[limit - 1] : null);
    }

    // Guard before any connection is opened; never print the connection string or credentials.
    public static void ValidateTarget(string environment, string? connectionString)
    {
        if (!string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("initial_publication_is_development_only");
        NpgsqlConnectionStringBuilder target;
        try { target = new(connectionString); }
        catch (ArgumentException) { throw new InvalidOperationException("invalid_initial_publication_target"); }
        if (!(string.Equals(target.Host, "localhost", StringComparison.OrdinalIgnoreCase) || target.Host == "127.0.0.1") ||
            target.Port != 15432 || target.Database != "laundry")
            throw new InvalidOperationException("invalid_initial_publication_target");
    }

    public static async Task<int> ExecuteCommandAsync(IHostEnvironment environment, IConfiguration configuration, CancellationToken ct = default)
    {
        var runId = Guid.NewGuid();
        try
        {
            var connection = configuration.GetConnectionString("Laundry");
            ValidateTarget(environment.EnvironmentName, connection);
            var options = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(connection,
                p => p.MigrationsHistoryTable("__order_migrations_history", "orders")).Options;
            var runner = new DevelopmentInitialOrderPublication(options, TimeProvider.System);
            await runner.CheckPrerequisitesAsync(ct);
            var actor = "local-development-initial-publication:" + Environment.UserName;
            Guid? cursor = null;
            do
            {
                var batch = await runner.RunBatchAsync(ManagementAccessStore.DevelopmentTenant, ManagementAccessStore.DevelopmentPlant, actor, afterId: cursor, ct: ct);
                Console.WriteLine($"Initial publication run {runId:D}: examined={batch.Examined}, created={batch.Created}, alreadyPresent={batch.AlreadyPresent}, ineligibleCustomer={batch.IneligibleCustomer}, unavailableOrder={batch.UnavailableOrder}.");
                cursor = batch.NextAfterId;
            } while (cursor.HasValue);
            Console.WriteLine($"Initial publication run {runId:D} finished. Orders and existing published copies were not changed. No gateway delivery was attempted.");
            return 0;
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Initial publication run {runId:D} stopped. Verify Development target, migrations and database availability. Earlier orders may have committed; rerunning from the beginning is safe. No credentials or order contents were printed.");
            return 1;
        }
    }
}
