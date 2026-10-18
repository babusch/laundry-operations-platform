using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Laundry.Edge.Orders;
using Laundry.Edge.Persistence;
using Laundry.Edge.Security;
using Laundry.Edge.Textiles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Laundry.Edge.Tests;

[Trait("Category", "Database")]
public sealed class DevelopmentScanPreviewTests(AcceptanceFixture fixture) : IClassFixture<AcceptanceFixture>
{
    private const string Route = "/api/development/scan-preview";
    private const string Towel = "SIMULATED-RFID-TOWEL-001";
    private static readonly OrderDownloadScope Scope = new(DevelopmentItemCatalog.TenantId, DevelopmentItemCatalog.PlantId);
    private DbContextOptions<PlantDbContext> Options => new DbContextOptionsBuilder<PlantDbContext>().UseNpgsql(fixture.ConnectionString).Options;
    private WebApplicationFactory<Program> App(string environment = "Development", bool enabled = true, bool lookup = true, bool list = true, bool scans = true) =>
        fixture.CreateApplication(environment, enabled: scans).WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["DevelopmentScanPreview:Enabled"] = enabled.ToString(), ["DevelopmentItemLookup:Enabled"] = lookup.ToString(), ["LocalOrderList:Enabled"] = list.ToString() })));
    private static HttpClient Client(WebApplicationFactory<Program> app) => app.CreateClient(new() { BaseAddress = new("https://localhost:7200"), AllowAutoRedirect = false });
    private async Task<(HttpClient Client, Guid Source)> Enroll(WebApplicationFactory<Program> app)
    {
        var client = Client(app); using var created = await client.PostAsync("/api/development/source-enrollments", null);
        var enrollment = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/source-enrollment/exchange", new { enrollmentCode = enrollment["enrollmentCode"]!.GetValue<string>() })).StatusCode);
        var session = (await client.GetFromJsonAsync<JsonObject>("/api/source-session"))!;
        client.DefaultRequestHeaders.Add(session["antiforgeryHeaderName"]!.GetValue<string>(), session["antiforgeryToken"]!.GetValue<string>());
        var id = enrollment["sourceId"]!.GetValue<Guid>();
        await using var db = new PlantDbContext(Options);
        db.SourcePermissions.Add(new() { SourceId = id, Permission = SourcePermissions.ReadOrders }); await db.SaveChangesAsync();
        return (client, id);
    }
    private static Task<HttpResponseMessage> Preview(HttpClient client, string tag = Towel) => client.PostAsJsonAsync(Route, new { identifier = new { technology = "rfid", value = tag } });
    private JsonObject Order(Guid id, Guid customer)
    {
        var item = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "order-plan.v1.visible.json")))!.AsObject();
        item["tenantId"] = Scope.TenantId; item["plantId"] = Scope.PlantId; item["orderId"] = id;
        item["customer"]!["id"] = customer; item["notes"] = "private synthetic note not for preview";
        return item;
    }
    private async Task Publish(params JsonObject[] items)
    {
        var store = new OrderDownloadStore(Options, Scope, TimeProvider.System); var progress = await store.ReadProgressAsync();
        var json = new JsonObject { ["items"] = new JsonArray(items.Select(x => x.DeepClone()).ToArray()), ["nextCursor"] = Guid.NewGuid().ToString(), ["hasMore"] = false };
        await store.ApplyAsync(Guid.NewGuid(), progress.Revision, progress.Cursor, json.ToJsonString());
    }

    [Fact]
    public async Task RealGatewayFindsMatchesBeyondTheFirstPageAndNeverWritesOrExposesOtherCustomers()
    {
        using var app = App(); var session = await Enroll(app); using var client = session.Client;
        var customer = DevelopmentItemCatalog.Lookup(new(Scope.TenantId, Scope.PlantId), Towel).Item!.Customer.Id;
        var unrelated = Enumerable.Range(1, 60).Select(i => Order(Guid.Parse($"00000000-0000-4000-8000-{i:x12}"), Guid.NewGuid())).ToArray();
        var matchId = Guid.Parse("ffffffff-ffff-4fff-8fff-fffffffffff1");
        await Publish(unrelated.Append(Order(matchId, customer)).ToArray());
        var foreignScope = new OrderDownloadScope(Scope.TenantId, Guid.NewGuid());
        var foreignOrder = Order(Guid.NewGuid(), customer); foreignOrder["plantId"] = foreignScope.PlantId;
        await new OrderDownloadStore(Options, foreignScope, TimeProvider.System).ApplyAsync(Guid.NewGuid(), 0, null,
            new JsonObject { ["items"] = new JsonArray(foreignOrder), ["nextCursor"] = "foreign", ["hasMore"] = false }.ToJsonString());
        await using var db = new PlantDbContext(Options);
        async Task<long[]> Snapshot() => new[] { await db.Observations.LongCountAsync(), await db.Outbox.LongCountAsync(), await db.LocalOrderPlans.LongCountAsync(),
            await db.OrderDownloadReceipts.LongCountAsync(), await db.OrderDownloadProgress.LongCountAsync(), await db.SourceSecurityAudits.LongCountAsync(),
            await db.SourceCredentials.LongCountAsync(), (await db.TrustedSources.AsNoTracking().SingleAsync(x => x.SourceId == session.Source)).ConfigurationVersion };
        var before = await Snapshot();
        using var first = await Preview(client); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore); Assert.Contains("Cookie", first.Headers.Vary);
        var result = (await first.Content.ReadFromJsonAsync<DevelopmentScanPreviewResult>())!;
        Assert.Equal("onePlannedOrder", result.Status); Assert.Equal(matchId, Assert.Single(result.Orders).Id); Assert.NotNull(result.LastSuccessfulPageAtUtc);
        var raw = await first.Content.ReadAsStringAsync();
        foreach (var value in new[] { "notes", "customerReference", "tenantId", "plantId", "sourceId", "private synthetic", "nextCursor", Towel }) Assert.DoesNotContain(value, raw);
        Assert.Equal(before, await Snapshot());
        foreach (var response in await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Preview(client))))
        { Assert.Equal(raw, await response.Content.ReadAsStringAsync()); response.Dispose(); }
        using var mop = await Preview(client, "SIMULATED-RFID-MOP-001");
        var noMatches = (await mop.Content.ReadFromJsonAsync<DevelopmentScanPreviewResult>())!;
        Assert.Equal("noLocalOrders", noMatches.Status); Assert.Empty(noMatches.Orders); Assert.NotNull(noMatches.Item);
        using var unknown = await Preview(client, "SIMULATED-RFID-UNKNOWN");
        var notRecognized = (await unknown.Content.ReadFromJsonAsync<DevelopmentScanPreviewResult>())!;
        Assert.Equal("notRecognized", notRecognized.Status); Assert.Null(notRecognized.Item); Assert.Empty(notRecognized.Orders); Assert.Null(notRecognized.LastSuccessfulPageAtUtc);
        Assert.Equal(before, await Snapshot());
        await Publish(Order(Guid.Parse("ffffffff-ffff-4fff-8fff-fffffffffff2"), customer));
        using var multiple = await Preview(client); var many = (await multiple.Content.ReadFromJsonAsync<DevelopmentScanPreviewResult>())!;
        Assert.Equal("multiplePlannedOrders", many.Status); Assert.Equal(2, many.Orders.Length);
        var beforeCorruption = await db.LocalOrderPlans.AsNoTracking().SingleAsync(x => x.OrderId == matchId);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plant.order_plans SET payload='invalid' WHERE order_id={matchId}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Preview(client)).StatusCode);
        }
        finally { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plant.order_plans SET payload={beforeCorruption.Payload} WHERE order_id={matchId}"); }
    }

    [Theory]
    [InlineData("scans.submit")]
    [InlineData("orders.read")]
    public async Task BothPermissionsAreRequiredAndRemovalTakesEffectImmediately(string permission)
    {
        using var app = App(); var session = await Enroll(app); using var client = session.Client;
        await using var db = new PlantDbContext(Options);
        db.SourcePermissions.Remove(await db.SourcePermissions.SingleAsync(x => x.SourceId == session.Source && x.Permission == permission)); await db.SaveChangesAsync();
        using var response = await Preview(client, "SIMULATED-RFID-UNKNOWN"); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("development_item_permission_required", (await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, true, true, true, "Development")]
    [InlineData(true, false, true, true, "Development")]
    [InlineData(true, true, false, true, "Development")]
    [InlineData(true, true, true, false, "Development")]
    [InlineData(true, true, true, true, "Production")]
    public async Task AllDevelopmentFeatureGatesMustBeExplicit(bool enabled, bool lookup, bool list, bool scans, string environment)
    {
        using var app = App(environment, enabled, lookup, list, scans); using var client = Client(app);
        Assert.Equal(HttpStatusCode.NotFound, (await Preview(client)).StatusCode);
    }

    [Fact]
    public async Task AnonymousAntiforgeryOriginAndCallerScopeFailClosed()
    {
        using var app = App(); using var anonymous = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Preview(anonymous)).StatusCode);
        var session = await Enroll(app); using var client = session.Client;
        using var bad = new StringContent("{\"identifier\":{\"technology\":\"rfid\",\"value\":\"SIMULATED-RFID-TOWEL-001\"},\"customerId\":\"other\"}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Route, bad)).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://other.example"); Assert.Equal(HttpStatusCode.Forbidden, (await Preview(client)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Remove(SourceEnrollmentEndpoints.AntiforgeryHeaderName);
        Assert.Equal(HttpStatusCode.BadRequest, (await Preview(client)).StatusCode);
    }

    [Fact]
    public async Task StorageOutageIsNotAnEmptyMatchAndRecovers()
    {
        using var app = App(); var session = await Enroll(app); using var client = session.Client;
        var config = app.Services.GetRequiredService<IConfiguration>(); var original = config["ConnectionStrings:Plant"];
        try
        {
            config["ConnectionStrings:Plant"] = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Port = 1 }.ConnectionString;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Preview(client)).StatusCode);
        }
        finally { config["ConnectionStrings:Plant"] = original; }
        Assert.Equal(HttpStatusCode.OK, (await Preview(client, "SIMULATED-RFID-UNKNOWN")).StatusCode);
    }

    private static DevelopmentItemLookupResult Known => DevelopmentItemCatalog.Lookup(new(Scope.TenantId, Scope.PlantId), Towel);
    private static LocalOrderSummary Summary(Guid? customer = null) => new(Guid.NewGuid(), "SYNTHETIC-PLAN", new(customer ?? Known.Item!.Customer.Id, "QA", "Synthetic customer"),
        new(2030, 1, 2), new(2030, 1, 3), "planned", 1);

    [Fact]
    public async Task UnknownSkipsOrderReadsAndNoMatchBeforeDownloadIsExplicit()
    {
        var unknown = await DevelopmentScanPreview.ResolveAsync(new(true, "notRecognized", null), (_, _) => throw new InvalidOperationException(), default);
        Assert.Equal("notRecognized", unknown.Status);
        var noOrders = await DevelopmentScanPreview.ResolveAsync(Known, (_, _) => Task.FromResult(new LocalOrderPage([], null, false, null)), default);
        Assert.Equal("noLocalOrders", noOrders.Status); Assert.Null(noOrders.LastSuccessfulPageAtUtc);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("duplicate")]
    [InlineData("cursor")]
    [InlineData("empty-continuation")]
    [InlineData("status")]
    [InlineData("page-bound")]
    [InlineData("match-bound")]
    public async Task ChangingOrUnboundedReadsNeverReturnPartialOrFalseSingleResults(string mode)
    {
        var first = Summary(mode == "page-bound" ? Guid.NewGuid() : null); var count = 0;
        Task<LocalOrderPage> Read(string? _, CancellationToken ct)
        {
            count++;
            var row = mode == "duplicate" ? first : mode == "status" ? first with { Status = "receiving" } : Summary(mode == "page-bound" ? Guid.NewGuid() : null);
            return Task.FromResult(new LocalOrderPage(mode == "empty-continuation" ? [] : [row], mode == "cursor" ? "same" : "next-" + count, true,
                mode == "changed" && count > 1 ? DateTimeOffset.UnixEpoch : null));
        }
        await Assert.ThrowsAsync<LocalOrderListException>(() => DevelopmentScanPreview.ResolveAsync(Known, Read, default));
        Assert.InRange(count, 1, 100);
    }

    [Fact]
    public async Task CancelledResolutionDoesNotContinueReading()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => DevelopmentScanPreview.ResolveAsync(Known, (_, _) => throw new InvalidOperationException(), cancelled.Token));
    }
}
