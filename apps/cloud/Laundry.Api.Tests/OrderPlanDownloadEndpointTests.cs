using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Laundry.Api.Orders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Laundry.Api.Tests;

[Trait("Category", "Database")]
public sealed class OrderPlanDownloadEndpointTests(ScanIngestionFixture fixture) : IClassFixture<ScanIngestionFixture>
{
    [Fact]
    public void DownloadEndpoint_AmbiguousScopeClaimsAreRejected()
    {
        var identity = new System.Security.Claims.ClaimsIdentity([
            new("tenant_id", ScanIngestionFixture.DefaultTenant),
            new("tenant_id", Guid.NewGuid().ToString()),
            new("plant_id", ScanIngestionFixture.DefaultPlant)], "synthetic");
        Assert.False(Laundry.Api.Security.GatewayScope.TryRead(new(identity), out _));
    }

    private WebApplicationFactory<Program> Application(string environment = "Development", bool enabled = true, string remote = "127.0.0.1") =>
        fixture.CreateApplication(environment, remote).WithWebHostBuilder(builder => builder.UseSetting("OrderPlanDownload:Enabled", enabled.ToString()));

    private static void Https(HttpClient client) => client.BaseAddress = new Uri("https://localhost:7100");

    [Fact]
    public async Task DownloadEndpoint_RequiresBearerAndDedicatedPermission()
    {
        await using var app = Application();
        using var anonymous = app.CreateClient(); Https(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(DevelopmentOrderPlanDownload.Path)).StatusCode);
        using var scansOnly = fixture.CreateAuthorizedClient(app); Https(scansOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await scansOnly.GetAsync(DevelopmentOrderPlanDownload.Path)).StatusCode);
        using var wrongSignature = RSA.Create(2048);
        using var invalid = fixture.CreateAuthorizedClient(app, signingKey: new RsaSecurityKey(wrongSignature), permission: "orders.download"); Https(invalid);
        Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.GetAsync(DevelopmentOrderPlanDownload.Path)).StatusCode);
        using var expired = fixture.CreateAuthorizedClient(app, expires: DateTime.UtcNow.AddMinutes(-2), permission: "orders.download"); Https(expired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await expired.GetAsync(DevelopmentOrderPlanDownload.Path)).StatusCode);
        using var wrongAudience = fixture.CreateAuthorizedClient(app, audience: "another-api", permission: "orders.download"); Https(wrongAudience);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongAudience.GetAsync(DevelopmentOrderPlanDownload.Path)).StatusCode);
        using var wrongIssuer = fixture.CreateAuthorizedClient(app, issuer: "https://untrusted.example.test", permission: "orders.download"); Https(wrongIssuer);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongIssuer.GetAsync(DevelopmentOrderPlanDownload.Path)).StatusCode);
    }

    [Theory]
    [InlineData(null, ScanIngestionFixture.DefaultPlant)]
    [InlineData(ScanIngestionFixture.DefaultTenant, "invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000", ScanIngestionFixture.DefaultPlant)]
    public async Task DownloadEndpoint_RejectsMissingMalformedScope(string? tenant, string plant)
    {
        await using var app = Application();
        using var client = fixture.CreateAuthorizedClient(app, tenant: tenant, plant: plant, permission: "orders.download"); Https(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(DevelopmentOrderPlanDownload.Path)).StatusCode);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?tenantId=other")]
    [InlineData("?cursor=")]
    [InlineData("?cursor=broken")]
    [InlineData("?cursor=x&cursor=y")]
    public async Task DownloadEndpoint_InvalidQueriesAreExplicit(string query)
    {
        await using var app = Application();
        using var client = fixture.CreateAuthorizedClient(app, permission: "orders.download"); Https(client);
        using var response = await client.GetAsync(DevelopmentOrderPlanDownload.Path + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("Production", true, "127.0.0.1", "https://localhost:7100", 404)]
    [InlineData("Development", false, "127.0.0.1", "https://localhost:7100", 404)]
    [InlineData("Development", true, "192.0.2.10", "https://localhost:7100", 403)]
    [InlineData("Development", true, "127.0.0.1", "http://localhost:7100", 403)]
    [InlineData("Development", true, "127.0.0.1", "https://other.test:7100", 403)]
    public async Task DownloadEndpoint_RetainsDevelopmentLoopbackBoundary(string environment, bool enabled, string remote, string origin, int status)
    {
        await using var app = Application(environment, enabled, remote);
        using var client = fixture.CreateAuthorizedClient(app, permission: "orders.download"); client.BaseAddress = new Uri(origin);
        Assert.Equal(status, (int)(await client.GetAsync(DevelopmentOrderPlanDownload.Path)).StatusCode);
    }

    [Fact]
    public async Task DownloadEndpoint_ReturnsOnlyAuthenticatedScopeAndCurrentFilteredCopy()
    {
        var tenant = Guid.NewGuid(); var plant = Guid.NewGuid();
        await using var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(fixture.ConnectionString,
            p => p.MigrationsHistoryTable("__order_migrations_history", "orders")).Options);
        await db.Database.MigrateAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await OrderPublicationStorage.LockPlantAsync(db, tenant, plant);
            var order = new ServiceOrder
            {
                Id = Guid.NewGuid(),
                TenantId = tenant,
                PlantId = plant,
                OrderNumber = "SYNTHETIC-DOWNLOAD",
                CustomerId = Guid.NewGuid(),
                PlannedReceivingDate = new(2030, 1, 1),
                PlannedDeliveryDate = new(2030, 1, 2),
                Notes = "Synthetic hidden management note",
                CustomerReference = "Synthetic private reference",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            db.Orders.Add(order);
            await OrderPublicationStorage.StageAsync(db, tenant, plant, order,
                new(tenant, order.CustomerId, "SYNTHETIC", "Synthetic customer"), TimeProvider.System);
            await db.SaveChangesAsync(); await transaction.CommitAsync();
        }
        await using var app = Application();
        using var client = fixture.CreateAuthorizedClient(app, tenant: tenant.ToString(), plant: plant.ToString(), permission: "orders.download"); Https(client);
        using var response = await client.GetAsync(DevelopmentOrderPlanDownload.Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Synthetic hidden management note", body); Assert.DoesNotContain("customerReference", body);
        using var json = JsonDocument.Parse(body);
        var item = json.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(tenant, item.GetProperty("tenantId").GetGuid()); Assert.Equal(plant, item.GetProperty("plantId").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("notes").ValueKind);
        var bookmark = json.RootElement.GetProperty("nextCursor").GetString();
        using var anotherPlant = fixture.CreateAuthorizedClient(app, tenant: tenant.ToString(), plant: Guid.NewGuid().ToString(), permission: "orders.download"); Https(anotherPlant);
        Assert.Equal(HttpStatusCode.BadRequest, (await anotherPlant.GetAsync(DevelopmentOrderPlanDownload.Path + "?cursor=" + Uri.EscapeDataString(bookmark!))).StatusCode);
        using var otherResponse = await anotherPlant.GetAsync(DevelopmentOrderPlanDownload.Path);
        using var otherJson = JsonDocument.Parse(await otherResponse.Content.ReadAsStringAsync());
        Assert.Empty(otherJson.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task DownloadEndpoint_EmptyBatchThenStorageOutageIs503NotAnEmptySuccess()
    {
        await using var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(fixture.ConnectionString,
            p => p.MigrationsHistoryTable("__order_migrations_history", "orders")).Options);
        await db.Database.MigrateAsync();
        await using var app = Application();
        using var client = fixture.CreateAuthorizedClient(app, permission: "orders.download"); Https(client);
        using var response = await client.GetAsync(DevelopmentOrderPlanDownload.Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(json.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.NotEmpty(json.RootElement.GetProperty("nextCursor").GetString()!);
        await using var unavailable = Application().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Laundry",
            "Host=127.0.0.1;Port=1;Database=unavailable;Username=synthetic;Password=synthetic;Timeout=1;Pooling=false"));
        using var down = fixture.CreateAuthorizedClient(unavailable, permission: "orders.download"); Https(down);
        using var failure = await down.GetAsync(DevelopmentOrderPlanDownload.Path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Contains("order_download_unavailable", await failure.Content.ReadAsStringAsync());
    }
}
