using System.Net;
using System.Text.Json;
using Laundry.Api.Integrations.Scans;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Laundry.Api.Tests;

[Trait("Category", "Database")]
public sealed class ScanDatabaseTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23")
        .Build();
    private WebApplicationFactory<Program> _application = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Laundry", _postgres.GetConnectionString()));
        using var scope = _application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ScanDbContext>();
        await database.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_application is not null)
        {
            await _application.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    [Theory]
    [InlineData("barcode")]
    [InlineData("rfid")]
    public async Task ContractExample_RoundTripsThroughPostgres(string technology)
    {
        var scan = ReadExample(technology);
        await InsertAsync(scan);

        using var scope = _application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ScanDbContext>();
        var stored = await database.Observations.AsNoTracking().SingleAsync(x => x.EventId == scan.EventId);

        Assert.Equal(scan, stored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedEventId_CannotInsertOrOverwriteOriginal(bool changedPayload)
    {
        var original = ReadExample("barcode");
        await InsertAsync(original);
        var replay = changedPayload ? original with { IdentifierValue = "SIMULATED-CHANGED" } : original;

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(replay));
        var postgresError = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresError.SqlState);
        Assert.Equal("pk_scan_observations", postgresError.ConstraintName);

        using var scope = _application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ScanDbContext>();
        Assert.Equal(original, await database.Observations.AsNoTracking().SingleAsync());
    }

    [Fact]
    public async Task ConcurrentDuplicates_StoreExactlyOneObservation()
    {
        var scan = ReadExample("barcode");
        async Task<bool> TryInsertAsync()
        {
            try
            {
                await InsertAsync(scan);
                return true;
            }
            catch (DbUpdateException exception) when (
                exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return false;
            }
        }

        var results = await Task.WhenAll(TryInsertAsync(), TryInsertAsync());
        Assert.Single(results, inserted => inserted);
        using var scope = _application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ScanDbContext>();
        Assert.Equal(1, await database.Observations.CountAsync());
    }

    [Fact]
    public async Task DelayedObservationsAndWrongSourceClock_PreserveOriginalTimes()
    {
        var first = ReadExample("barcode");
        // A device clock can be ahead of the gateway; arrival order is not event order.
        first = first with { ObservedAtUtc = first.GatewayAcceptedAtUtc.AddDays(1) };
        var delayed = first with
        {
            EventId = Guid.NewGuid(),
            ObservedAtUtc = first.ObservedAtUtc.AddDays(-3),
            GatewayAcceptedAtUtc = first.GatewayAcceptedAtUtc.AddDays(-2),
            CloudReceivedAtUtc = first.CloudReceivedAtUtc.AddSeconds(1)
        };
        await InsertAsync(first);
        await InsertAsync(delayed);

        using var scope = _application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ScanDbContext>();
        var stored = await database.Observations.AsNoTracking().OrderBy(x => x.CloudReceivedAtUtc).ToListAsync();
        Assert.Equal(new[] { first, delayed }, stored);
    }

    [Fact]
    public async Task ApplyingMigrationsAgain_PreservesExistingObservations()
    {
        var scan = ReadExample("rfid");
        await InsertAsync(scan);
        using var scope = _application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ScanDbContext>();

        await database.Database.MigrateAsync();

        Assert.False(database.Database.HasPendingModelChanges());
        Assert.Empty(await database.Database.GetPendingMigrationsAsync());
        Assert.Equal(scan, await database.Observations.AsNoTracking().SingleAsync());
    }

    [Fact]
    public async Task DatabaseOutage_ChangesReadinessAndRecoversWithoutLosingData()
    {
        var scan = ReadExample("barcode");
        await InsertAsync(scan);
        using var client = _application.CreateClient();
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        await _postgres.StopAsync();
        try
        {
            using var unavailable = await client.GetAsync("/health/ready");
            using var alive = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Equal("Unhealthy", await unavailable.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        }
        finally
        {
            await _postgres.StartAsync();
            // Docker can assign a new random host port when this test container restarts.
            // Point new API scopes at that endpoint without restarting the application.
            _application.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Laundry"] =
                _postgres.GetConnectionString();
        }

        // PostgreSQL startup and replacement of stale pooled connections are asynchronous.
        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            using var recovered = await client.GetAsync("/health/ready", recoveryTimeout.Token);
            if (recovered.StatusCode == HttpStatusCode.OK)
            {
                break;
            }

            Assert.Equal(HttpStatusCode.ServiceUnavailable, recovered.StatusCode);
            await Task.Delay(TimeSpan.FromMilliseconds(250), recoveryTimeout.Token);
        }
        using var scope = _application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ScanDbContext>();
        Assert.Equal(scan, await database.Observations.AsNoTracking().SingleAsync());
    }

    private async Task InsertAsync(ScanObservation scan)
    {
        using var scope = _application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ScanDbContext>();
        database.Observations.Add(scan);
        await database.SaveChangesAsync();
    }

    private static ScanObservation ReadExample(string technology)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", $"scan-observed.v1.{technology}.json")));
        var json = document.RootElement;
        return new ScanObservation
        {
            EventId = json.GetProperty("eventId").GetGuid(),
            SchemaVersion = json.GetProperty("schemaVersion").GetInt32(),
            EventType = json.GetProperty("eventType").GetString()!,
            CorrelationId = json.GetProperty("correlationId").GetGuid(),
            TenantId = json.GetProperty("tenantId").GetGuid(),
            PlantId = json.GetProperty("plantId").GetGuid(),
            StationId = json.GetProperty("stationId").GetGuid(),
            DeviceId = json.GetProperty("deviceId").GetGuid(),
            ObservedAtUtc = json.GetProperty("observedAtUtc").GetDateTimeOffset(),
            GatewayAcceptedAtUtc = json.GetProperty("gatewayAcceptedAtUtc").GetDateTimeOffset(),
            CloudReceivedAtUtc = DateTimeOffset.Parse("2026-09-07T12:00:00Z"),
            IdentifierTechnology = json.GetProperty("identifier").GetProperty("technology").GetString()!,
            IdentifierValue = json.GetProperty("identifier").GetProperty("value").GetString()!
        };
    }
}
