using System.Net;
using System.Text.Json;
using Laundry.Api.Customers;
using Laundry.Api.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Laundry.Api.Tests;

public sealed partial class OrderCreateEndpointTests
{
    private const string InitialActor = "local-development-initial-publication:synthetic-test";
    private static Guid InitialId(int suffix) => Guid.Parse($"40000000-0000-4000-8000-{suffix:D12}");
    private DevelopmentInitialOrderPublication InitialRunner(DbContextOptions<OrderDbContext>? options = null) => new(options ?? _orders, TimeProvider.System);
    private async Task SeedInitialCandidate(int suffix, bool visible = false, Guid? tenant = null, Guid? plant = null, Guid? customer = null)
    {
        await using var db = new OrderDbContext(_orders);
        db.Orders.Add(new()
        {
            Id = InitialId(suffix),
            TenantId = tenant ?? Tenant,
            PlantId = plant ?? Plant,
            CustomerId = customer ?? Customer,
            OrderNumber = $"SYNTHETIC-LEGACY-{suffix}",
            PlannedReceivingDate = new(2030, 1, 1),
            PlannedDeliveryDate = new(2030, 1, 3),
            Notes = "Synthetic initial-publication note",
            NotesVisibleToOperators = visible,
            CustomerReference = "Synthetic management-only reference",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task InitialPublication_ScopesFiltersAttributesAndPreservesOrderEvidenceOnRepeat()
    {
        using var existing = await Create(_client, Guid.NewGuid()); Assert.Equal(HttpStatusCode.Created, existing.StatusCode);
        await SeedInitialCandidate(1, visible: true); await SeedInitialCandidate(2);
        await SeedInitialCandidate(3, tenant: Guid.NewGuid()); await SeedInitialCandidate(4, plant: Guid.NewGuid());
        await using var db = new OrderDbContext(_orders);
        var beforeOrders = JsonSerializer.Serialize(await db.Orders.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync());
        var beforeAudit = JsonSerializer.Serialize(await db.AuditEntries.AsNoTracking().ToArrayAsync());
        var beforeReceipts = JsonSerializer.Serialize(await db.CommandReceipts.AsNoTracking().ToArrayAsync());
        var original = await db.PublicationOutbox.AsNoTracking().SingleAsync();
        await InitialRunner().CheckPrerequisitesAsync();
        var first = await InitialRunner().RunBatchAsync(Tenant, Plant, InitialActor);
        Assert.Equal(2, first.Created); Assert.Equal(2, first.Examined); Assert.Null(first.NextAfterId);
        Assert.Equal(0, (await InitialRunner().RunBatchAsync(Tenant, Plant, InitialActor)).Examined);
        var entries = await db.PublicationOutbox.AsNoTracking().OrderBy(x => x.Version).ToArrayAsync(); Assert.Equal(3, entries.Length);
        Assert.All(entries.Where(x => x.Id != original.Id), x => { Assert.Equal(InitialActor, x.InitialPublicationActor); Assert.Equal(1, x.Version); Assert.DoesNotContain(InitialActor, x.Payload); });
        var untouched = entries.Single(x => x.Id == original.Id); Assert.Equal(original.Payload, untouched.Payload); Assert.Null(untouched.InitialPublicationActor);
        var visible = entries.Single(x => x.OrderId == InitialId(1)); Assert.Contains("Synthetic initial-publication note", visible.Payload);
        var hidden = entries.Single(x => x.OrderId == InitialId(2)); Assert.DoesNotContain("Synthetic initial-publication note", hidden.Payload);
        using var json = JsonDocument.Parse(hidden.Payload); Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("notes").ValueKind);
        Assert.Equal(beforeOrders, JsonSerializer.Serialize(await db.Orders.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()));
        Assert.Equal(beforeAudit, JsonSerializer.Serialize(await db.AuditEntries.AsNoTracking().ToArrayAsync()));
        Assert.Equal(beforeReceipts, JsonSerializer.Serialize(await db.CommandReceipts.AsNoTracking().ToArrayAsync()));
        Assert.Equal(1, (await db.NumberCounters.SingleAsync()).LastNumber);
    }

    [Fact]
    public async Task InitialPublication_BoundedPagesMakeProgressPastIneligibleCustomersAndCanRecoverLater()
    {
        var customerId = Guid.NewGuid();
        await using var customers = new CustomerDbContext(_customers);
        customers.Customers.Add(new() { Id = customerId, TenantId = Tenant, Code = "SYNTHETIC-SKIP", DisplayName = "Synthetic ineligible customer", IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow });
        customers.PlantServices.Add(new() { TenantId = Tenant, PlantId = Plant, CustomerId = customerId, IsActive = false });
        await customers.SaveChangesAsync();
        await SeedInitialCandidate(1); await SeedInitialCandidate(2, customer: customerId); await SeedInitialCandidate(3);
        var first = await InitialRunner().RunBatchAsync(Tenant, Plant, InitialActor, limit: 1);
        Assert.Equal(1, first.Created); Assert.Equal(InitialId(1), first.NextAfterId);
        var second = await InitialRunner().RunBatchAsync(Tenant, Plant, InitialActor, limit: 1, afterId: first.NextAfterId);
        Assert.Equal(1, second.IneligibleCustomer); Assert.Equal(InitialId(2), second.NextAfterId);
        var third = await InitialRunner().RunBatchAsync(Tenant, Plant, InitialActor, limit: 1, afterId: second.NextAfterId);
        Assert.Equal(1, third.Created); Assert.Null(third.NextAfterId);
        await customers.Database.ExecuteSqlInterpolatedAsync($"UPDATE customers.plant_services SET is_active = true WHERE customer_id = {customerId}");
        Assert.Equal(1, (await InitialRunner().RunBatchAsync(Tenant, Plant, InitialActor)).Created);
        await using var db = new OrderDbContext(_orders); Assert.Equal(3, await db.PublicationOutbox.CountAsync());
    }

    [Fact]
    public async Task InitialPublication_ConcurrentInitializationAndOrdinaryEditDoNotOverwriteNewerCopy()
    {
        await SeedInitialCandidate(1, visible: true);
        var id = InitialId(1);
        var initial = InitialRunner().RunBatchAsync(Tenant, Plant, InitialActor);
        using var edited = await Update(_client, Guid.NewGuid(), Path + $"/{id:D}/plan", $"\"{id:D}:1\"", WithVisibility(UpdateBody(notes: "Synthetic latest edited note"), "true"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode); await initial;
        await using var db = new OrderDbContext(_orders); var head = await db.PublicationHeads.AsNoTracking().SingleAsync();
        Assert.Contains("Synthetic latest edited note", head.Payload); Assert.Equal(2, (await db.Orders.SingleAsync()).Version);
        var count = await db.PublicationOutbox.CountAsync(); Assert.InRange(count, 1, 2); Assert.Equal(count, head.Version);
        Assert.Equal(0, (await InitialRunner().RunBatchAsync(Tenant, Plant, InitialActor)).Created);
        Assert.Equal(head.Payload, (await db.PublicationHeads.AsNoTracking().SingleAsync()).Payload);
    }

    [Fact]
    public async Task InitialPublication_PartialFailurePreservesEarlierCommitAndRerunCreatesOnlyMissingCopy()
    {
        await SeedInitialCandidate(1); await SeedInitialCandidate(2);
        await using var db = new OrderDbContext(_orders);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.publication_outbox ADD CONSTRAINT synthetic_fail_second CHECK (order_id <> '40000000-0000-4000-8000-000000000002'::uuid)");
        await Assert.ThrowsAsync<DbUpdateException>(() => InitialRunner().RunBatchAsync(Tenant, Plant, InitialActor));
        var original = await db.PublicationOutbox.AsNoTracking().SingleAsync(); Assert.Equal(InitialId(1), original.OrderId);
        Assert.Single(await db.PublicationHeads.AsNoTracking().ToArrayAsync());
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.publication_outbox DROP CONSTRAINT synthetic_fail_second");
        Assert.Equal(1, (await InitialRunner().RunBatchAsync(Tenant, Plant, InitialActor)).Created);
        Assert.Equal(2, await db.PublicationOutbox.CountAsync());
        Assert.Equal(original.Payload, (await db.PublicationOutbox.AsNoTracking().SingleAsync(x => x.Id == original.Id)).Payload);
        Assert.All(await db.Orders.ToArrayAsync(), x => Assert.Equal(1, x.Version));
    }

    [Fact]
    public async Task InitialPublication_LostAcknowledgementRetryRetainsOneAttributedSendRecord()
    {
        await SeedInitialCandidate(1);
        var options = new DbContextOptionsBuilder<OrderDbContext>(_orders).AddInterceptors(new LoseCommitAcknowledgement()).Options;
        await Assert.ThrowsAsync<NpgsqlException>(() => InitialRunner(options).RunBatchAsync(Tenant, Plant, InitialActor));
        await using var db = new OrderDbContext(_orders); var original = await db.PublicationOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(0, (await InitialRunner(options).RunBatchAsync(Tenant, Plant, InitialActor)).Created);
        var retry = await db.PublicationOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, retry.Id); Assert.Equal(original.Payload, retry.Payload); Assert.Equal(InitialActor, retry.InitialPublicationActor);
    }

    [Fact]
    public async Task InitialPublication_RestartKeepsProgressAndAllowsSafeFullRerun()
    {
        await SeedInitialCandidate(1); await SeedInitialCandidate(2);
        Assert.Equal(1, (await InitialRunner().RunBatchAsync(Tenant, Plant, InitialActor, limit: 1)).Created);
        await _postgres.StopAsync(); await _postgres.StartAsync();
        var options = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(_postgres.GetConnectionString(),
            p => p.MigrationsHistoryTable("__order_migrations_history", "orders")).Options;
        Assert.Equal(1, (await InitialRunner(options).RunBatchAsync(Tenant, Plant, InitialActor)).Created);
        await using var db = new OrderDbContext(options); Assert.Equal(2, await db.PublicationOutbox.CountAsync());
    }

    [Fact]
    public async Task InitialPublication_PrerequisiteCheckRejectsMissingMigrationWithoutPublishing()
    {
        await SeedInitialCandidate(1);
        await using var db = new OrderDbContext(_orders);
        var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261016194052_OrderPublicationStorage");
        Assert.Equal("apply_initial_publication_prerequisite_migrations", (await Assert.ThrowsAsync<InvalidOperationException>(() => InitialRunner().CheckPrerequisitesAsync())).Message);
        Assert.Empty(await db.PublicationHeads.ToArrayAsync());
        await migrator.MigrateAsync(); await InitialRunner().CheckPrerequisitesAsync();
        Assert.Empty(await db.PublicationHeads.ToArrayAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task InitialPublication_InvalidInputsAndNonDevelopmentCommandRejectBeforeWriting()
    {
        foreach (var limit in new[] { 0, 101 })
            await Assert.ThrowsAsync<InvalidOperationException>(() => InitialRunner().RunBatchAsync(Tenant, Plant, InitialActor, limit));
        await Assert.ThrowsAsync<InvalidOperationException>(() => InitialRunner().RunBatchAsync(Guid.Empty, Plant, InitialActor));
        await Assert.ThrowsAsync<InvalidOperationException>(() => InitialRunner().RunBatchAsync(Tenant, Plant, " "));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Laundry"] = "Host=unreachable.invalid;Password=synthetic-not-for-logging" }).Build();
        Assert.Equal(1, await DevelopmentInitialOrderPublication.ExecuteCommandAsync(new InitialTestEnvironment(), configuration));
        await using var db = new OrderDbContext(_orders); Assert.Empty(await db.PublicationHeads.ToArrayAsync());
    }

    private sealed class InitialTestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Synthetic command guard test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class InitialOrderPublicationTargetTests
{
    [Theory]
    [InlineData("Production", "Host=localhost;Port=15432;Database=laundry")]
    [InlineData("Staging", "Host=localhost;Port=15432;Database=laundry")]
    [InlineData("Development", "Host=example.invalid;Port=15432;Database=laundry")]
    [InlineData("Development", "Host=localhost;Port=5432;Database=laundry")]
    [InlineData("Development", "Host=localhost;Port=15432;Database=other")]
    [InlineData("Development", "not a connection string")]
    [InlineData("Development", null)]
    public void TargetGuardRejectsUnexpectedEnvironmentOrDatabase(string environment, string? connection)
        => Assert.Throws<InvalidOperationException>(() => DevelopmentInitialOrderPublication.ValidateTarget(environment, connection));

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public void TargetGuardAcceptsOnlyNamedLoopbackDevelopmentTarget(string host)
        => DevelopmentInitialOrderPublication.ValidateTarget("Development", $"Host={host};Port=15432;Database=laundry");
}
