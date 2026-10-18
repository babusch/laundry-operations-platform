using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Laundry.Edge.Orders;
using Laundry.Edge.Persistence;
using Laundry.Edge.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Laundry.Edge.Tests;

[Trait("Category", "Database")]
public sealed class LocalOrderListTests(AcceptanceFixture fixture) : IClassFixture<AcceptanceFixture>
{
    private readonly OrderDownloadScope _scope = new(Guid.NewGuid(), Guid.NewGuid());
    private DbContextOptions<PlantDbContext> Options => new DbContextOptionsBuilder<PlantDbContext>().UseNpgsql(fixture.ConnectionString).Options;
    private OrderDownloadStore Store => new(Options, _scope, TimeProvider.System);
    private WebApplicationFactory<Program> App(bool enabled = true, string environment = "Development", string address = "127.0.0.1") =>
        fixture.CreateApplication(environment, address, tenant: _scope.TenantId.ToString(), plant: _scope.PlantId.ToString())
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["LocalOrderList:Enabled"] = enabled.ToString(), ["AllowedHosts"] = "*" })));
    private static HttpClient Client(WebApplicationFactory<Program> app, string? cookie = null, string origin = "https://localhost:7200")
    {
        var client = app.CreateClient(new() { BaseAddress = new(origin), HandleCookies = false, AllowAutoRedirect = false });
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }
    private async Task<(Guid SourceId, Guid CredentialId, string Cookie)> Source(bool read = true, string mode = "active")
    {
        var sourceId = Guid.NewGuid(); var credentialId = Guid.NewGuid(); var bytes = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        await using var db = new PlantDbContext(Options);
        db.TrustedSources.Add(new()
        {
            SourceId = sourceId,
            TenantId = mode == "wrong-tenant" ? Guid.NewGuid() : _scope.TenantId,
            PlantId = mode == "wrong-plant" ? Guid.NewGuid() : _scope.PlantId,
            StationId = Guid.NewGuid(),
            Kind = mode == "adapter" ? TrustedSourceKind.RemoteAdapter : TrustedSourceKind.BrowserStation,
            Status = mode == "disabled" ? TrustedSourceStatus.Disabled : TrustedSourceStatus.Active,
            ConfigurationVersion = 1,
            CreatedAtUtc = now,
            StatusChangedAtUtc = now
        });
        db.SourceCredentials.Add(new()
        {
            CredentialId = credentialId,
            SourceId = sourceId,
            Kind = SourceCredentialKind.BrowserSecret,
            VerifierDigest = SHA256.HashData(bytes),
            Status = mode == "revoked" ? SourceCredentialStatus.Revoked : SourceCredentialStatus.Active,
            IssuedAtUtc = now.AddHours(-2),
            ExpiresAtUtc = mode == "expired" ? now.AddHours(-1) : now.AddHours(1)
        });
        db.SourcePermissions.Add(new() { SourceId = sourceId, Permission = SourcePermissions.SubmitScans });
        if (read) db.SourcePermissions.Add(new() { SourceId = sourceId, Permission = SourcePermissions.ReadOrders });
        await db.SaveChangesAsync();
        return (sourceId, credentialId, $"{SourceEnrollmentEndpoints.SourceCookieName}={credentialId:N}.{WebEncoders.Base64UrlEncode(bytes)}");
    }
    private JsonObject Order(Guid? id = null)
    {
        var item = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "order-plan.v1.visible.json")))!.AsObject();
        item["tenantId"] = _scope.TenantId; item["plantId"] = _scope.PlantId; item["orderId"] = id ?? Guid.NewGuid();
        item["notes"] = "synthetic private operator instruction";
        return item;
    }
    private async Task Publish(params JsonObject[] items)
    {
        var progress = await Store.ReadProgressAsync();
        var page = new JsonObject { ["items"] = new JsonArray(items.Select(x => x.DeepClone()).ToArray()), ["nextCursor"] = Guid.NewGuid().ToString(), ["hasMore"] = false };
        await Store.ApplyAsync(Guid.NewGuid(), progress.Revision, progress.Cursor, page.ToJsonString());
    }
    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task LocalOrders_ListIsScopedBoundedReadOnlyAndOmitsNotesAndInternalFields()
    {
        var source = await Source(); var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).OrderBy(x => x.ToString(), StringComparer.Ordinal).ToArray();
        await Publish(ids.Select(x => Order(x)).ToArray());
        var foreignScope = new OrderDownloadScope(_scope.TenantId, Guid.NewGuid());
        await using (var db = new PlantDbContext(Options))
        {
            // A separate scope must not affect this list or its metadata.
            db.LocalOrderPlans.Add(new() { TenantId = foreignScope.TenantId, PlantId = foreignScope.PlantId, OrderId = Guid.NewGuid(), Version = 1, Payload = "{}", ContentFingerprint = "foreign", StoredAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var app = App(); using var client = Client(app, source.Cookie);
        var before = await Store.ReadProgressAsync();
        var first = await client.GetAsync("/api/order-plans?limit=2"); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl!.ToString());
        var text = await first.Content.ReadAsStringAsync();
        foreach (var excluded in new[] { "notes", "tenantId", "plantId", "sourceId", "stationId", "credential", "customerReference", "synthetic private", "payload", "fingerprint" })
            Assert.DoesNotContain(excluded, text, StringComparison.OrdinalIgnoreCase);
        var page = JsonSerializer.Deserialize<LocalOrderPage>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(ids.Take(2), page.Items.Select(x => x.Id)); Assert.True(page.HasMore); Assert.NotNull(page.NextCursor);
        Assert.Equal(before.LastSuccessfulCheckUtc, page.LastSuccessfulPageAtUtc);
        var next = await client.GetFromJsonAsync<LocalOrderPage>("/api/order-plans?limit=2&cursor=" + Uri.EscapeDataString(page.NextCursor));
        Assert.Equal(ids.Skip(2), next!.Items.Select(x => x.Id)); Assert.False(next.HasMore); Assert.Null(next.NextCursor);
        Assert.Equal(before, await Store.ReadProgressAsync());
        await using var after = new PlantDbContext(Options);
        Assert.Equal(1, await after.OrderDownloadReceipts.CountAsync(x => x.TenantId == _scope.TenantId && x.PlantId == _scope.PlantId));
        Assert.Equal(3, await after.LocalOrderPlans.CountAsync(x => x.TenantId == _scope.TenantId && x.PlantId == _scope.PlantId));
    }

    [Fact]
    public async Task LocalOrders_EmptyBeforeFirstDownloadIsNotKnownCurrent()
    {
        var source = await Source(); using var app = App(); using var client = Client(app, source.Cookie);
        var page = await client.GetFromJsonAsync<LocalOrderPage>("/api/order-plans");
        Assert.Empty(page!.Items); Assert.Null(page.LastSuccessfulPageAtUtc); Assert.False(page.HasMore); Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task LocalOrders_DefaultPageIsBoundedAndPartialDownloadTimeIsOnlyActivityMetadata()
    {
        var source = await Source(); await Publish(Enumerable.Range(0, 51).Select(_ => Order()).ToArray());
        using var app = App(); using var client = Client(app, source.Cookie);
        var page = await client.GetFromJsonAsync<LocalOrderPage>("/api/order-plans");
        Assert.Equal(50, page!.Items.Length); Assert.True(page.HasMore); Assert.NotNull(page.NextCursor);
        var progress = await Store.ReadProgressAsync();
        var partial = new JsonObject { ["items"] = new JsonArray(Order()), ["nextCursor"] = "partial-cloud-bookmark", ["hasMore"] = true };
        await Store.ApplyAsync(Guid.NewGuid(), progress.Revision, progress.Cursor, partial.ToJsonString());
        var updated = await client.GetFromJsonAsync<JsonObject>("/api/order-plans?limit=1");
        Assert.Equal(4, updated!.Count); // No invented online/current/caught-up flags.
        Assert.NotNull(updated["lastSuccessfulPageAtUtc"]);
    }

    [Fact]
    public async Task LocalOrders_AdapterCannotUseBrowserOnlyListEvenWithPermission()
    {
        var source = await Source(mode: "adapter"); using var app = App(); using var client = Client(app, source.Cookie);
        await Error(await client.GetAsync("/api/order-plans"), HttpStatusCode.Forbidden, "local_order_permission_required");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("disabled")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-plant")]
    public async Task LocalOrders_InvalidSourceCannotRead(string mode)
    {
        var source = await Source(mode: mode); using var app = App();
        using var client = Client(app, mode == "missing" ? null : mode == "invalid" ? SourceEnrollmentEndpoints.SourceCookieName + "=invalid" : source.Cookie);
        await Error(await client.GetAsync("/api/order-plans"), HttpStatusCode.Unauthorized, "local_order_source_required");
    }

    [Fact]
    public async Task LocalOrders_ScanOnlyPermissionDoesNotGrantOrderReadAndRevocationIsImmediate()
    {
        var source = await Source(read: false); using var app = App(); using var client = Client(app, source.Cookie);
        await Error(await client.GetAsync("/api/order-plans"), HttpStatusCode.Forbidden, "local_order_permission_required");
        await using var db = new PlantDbContext(Options);
        db.SourcePermissions.Add(new() { SourceId = source.SourceId, Permission = SourcePermissions.ReadOrders }); await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/order-plans")).StatusCode);
        db.SourcePermissions.Remove(await db.SourcePermissions.SingleAsync(x => x.SourceId == source.SourceId && x.Permission == SourcePermissions.ReadOrders));
        await db.SaveChangesAsync();
        await Error(await client.GetAsync("/api/order-plans"), HttpStatusCode.Forbidden, "local_order_permission_required");
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=51")]
    [InlineData("?limit=-1")]
    [InlineData("?limit=x")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?plantId=other")]
    [InlineData("?cursor=")]
    [InlineData("?cursor=invalid")]
    [InlineData("?cursor=a&cursor=b")]
    public async Task LocalOrders_RejectsInvalidOrCallerScopedQueries(string query)
    {
        var source = await Source(); using var app = App(); using var client = Client(app, source.Cookie);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/order-plans" + query)).StatusCode);
    }

    [Fact]
    public async Task LocalOrders_CursorRejectsTamperingWrongScopeAndDownloadedChanges()
    {
        var source = await Source(); await Publish(Order(), Order()); using var app = App(); using var client = Client(app, source.Cookie);
        var page = await client.GetFromJsonAsync<LocalOrderPage>("/api/order-plans?limit=1");
        await Error(await client.GetAsync("/api/order-plans?cursor=" + Uri.EscapeDataString(page!.NextCursor! + "x")), HttpStatusCode.BadRequest, "invalid_local_order_cursor");
        // Direct query uses the same protector, but a different trusted scope.
        await using (var db = new PlantDbContext(Options))
        {
            var protection = app.Services.GetService(typeof(Microsoft.AspNetCore.DataProtection.IDataProtectionProvider)) as Microsoft.AspNetCore.DataProtection.IDataProtectionProvider;
            var query = new LocalOrderList(db, protection!);
            var error = await Assert.ThrowsAsync<LocalOrderListException>(() => query.ReadAsync(new(_scope.TenantId, Guid.NewGuid()), 1, page.NextCursor, default));
            Assert.Equal("invalid_local_order_cursor", error.Code);
        }
        await Publish(); // Even a successful empty poll changes local progress: restart list explicitly.
        await Error(await client.GetAsync("/api/order-plans?cursor=" + Uri.EscapeDataString(page.NextCursor!)), HttpStatusCode.Conflict, "local_order_list_changed");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/order-plans")).StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:7200", "127.0.0.1", null, null)]
    [InlineData("https://other.invalid:7200", "127.0.0.1", null, null)]
    [InlineData("https://localhost:7200", "192.0.2.1", null, null)]
    [InlineData("https://localhost:7200", "127.0.0.1", "https://other.invalid", null)]
    [InlineData("https://localhost:7200", "127.0.0.1", null, "cross-site")]
    public async Task LocalOrders_RejectsUntrustedTransportAndOrigin(string origin, string address, string? headerOrigin, string? fetchSite)
    {
        using var app = App(address: address); using var client = Client(app, origin: origin);
        if (headerOrigin is not null) client.DefaultRequestHeaders.Add("Origin", headerOrigin);
        if (fetchSite is not null) client.DefaultRequestHeaders.Add("Sec-Fetch-Site", fetchSite);
        await Error(await client.GetAsync("/api/order-plans"), HttpStatusCode.Forbidden, "local_order_origin_forbidden");
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("Development", false)]
    public async Task LocalOrders_UnmappedOutsideExplicitDevelopmentFlag(string environment, bool enabled)
    {
        using var app = App(enabled, environment); using var client = Client(app);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/order-plans")).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("null")]
    public async Task LocalOrders_CorruptLocalCopyFailsClosedWithoutPayload(string payload)
    {
        var source = await Source(); await Publish(Order());
        await using (var db = new PlantDbContext(Options))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plant.order_plans SET payload={payload} WHERE tenant_id={_scope.TenantId} AND plant_id={_scope.PlantId}");
        using var app = App(); using var client = Client(app, source.Cookie);
        await Error(await client.GetAsync("/api/order-plans"), HttpStatusCode.ServiceUnavailable, "local_order_copy_invalid");
    }

    [Fact]
    public async Task LocalOrders_DatabaseOutageReturns503NotAnEmptyListAndRecovers()
    {
        var source = await Source(); await Publish(Order()); using var app = App(); using var client = Client(app, source.Cookie);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/order-plans")).StatusCode);
        await fixture.Postgres.StopAsync();
        try { await Error(await client.GetAsync("/api/order-plans"), HttpStatusCode.ServiceUnavailable, "local_order_storage_unavailable"); }
        finally
        {
            await fixture.Postgres.StartAsync();
            // Disposable containers can receive a different host port after restart.
            app.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Plant"] = fixture.ConnectionString;
        }
        // Restart can also invalidate pooled connections; allow bounded recovery.
        HttpResponseMessage? recovered = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            recovered?.Dispose();
            recovered = await client.GetAsync("/api/order-plans");
            if (recovered.IsSuccessStatusCode) break;
            await Task.Delay(100);
        }
        using (recovered)
        {
            Assert.Equal(HttpStatusCode.OK, recovered!.StatusCode);
            Assert.Single((await recovered.Content.ReadFromJsonAsync<LocalOrderPage>())!.Items);
        }
    }
}
