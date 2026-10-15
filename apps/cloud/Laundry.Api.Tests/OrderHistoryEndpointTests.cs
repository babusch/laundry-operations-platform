using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Laundry.Api.Customers;
using Laundry.Api.Orders;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Laundry.Api.Tests;

public sealed partial class OrderCreateEndpointTests
{
    [Fact]
    public async Task History_ContainsOnlyPublicCreationAndChangedFieldsWithAcceptedAccountName()
    {
        using var creator = Client(_app); Cookie(creator, _app, displayName: "Synthetic creator"); await Csrf(creator);
        using var created = await Create(creator, Guid.NewGuid());
        var tag = created.Headers.ETag!.ToString();
        using var editor = Client(_app); Cookie(editor, _app, displayName: "Synthetic editor"); await Csrf(editor);
        var key = Guid.NewGuid();
        using var updated = await Update(editor, key, created.Headers.Location + "/plan", tag, UpdateBody(reference: null, notes: null));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var renamed = Client(_app); Cookie(renamed, _app, displayName: "Later account name"); await Csrf(renamed);
        using var retried = await Update(renamed, key, created.Headers.Location + "/plan", tag, UpdateBody(reference: null, notes: null));
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        using var unchanged = await Update(renamed, Guid.NewGuid(), created.Headers.Location + "/plan", updated.Headers.ETag!.ToString(), UpdateBody(reference: null, notes: null));
        using var failed = await Update(renamed, Guid.NewGuid(), created.Headers.Location + "/plan", tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, failed.StatusCode);
        _client.DefaultRequestHeaders.Remove("X-Management-CSRF");
        using var response = await _client.GetAsync(created.Headers.Location + "/history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var history = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entries = history.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Null(history.GetProperty("nextCursor").GetString());
        Assert.Equal("order_created", entries[0].GetProperty("action").GetString());
        Assert.Equal("Synthetic creator", entries[0].GetProperty("actorDisplayName").GetString());
        Assert.Equal(Customer, entries[0].GetProperty("initialPlan").GetProperty("customerId").GetGuid());
        Assert.Empty(entries[0].GetProperty("changes").EnumerateArray());
        Assert.Equal("Synthetic editor", entries[1].GetProperty("actorDisplayName").GetString());
        Assert.Equal(2, entries[1].GetProperty("version").GetInt64());
        Assert.Equal(JsonValueKind.Null, entries[1].GetProperty("initialPlan").ValueKind);
        var changes = entries[1].GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(new[] { "plannedReceivingDate", "plannedDeliveryDate", "notes" }, changes.Select(x => x.GetProperty("field").GetString()));
        Assert.Equal("Synthetic planned order", changes[2].GetProperty("before").GetString());
        Assert.Equal(JsonValueKind.Null, changes[2].GetProperty("after").ValueKind);
        Assert.DoesNotContain(_subject, history.GetRawText());
        foreach (var internalField in new[] { "actorSubject", "actorIssuer", "tenantId", "requestId", "requestFingerprint" })
            Assert.DoesNotContain(internalField, history.GetRawText());
        await using var db = new OrderDbContext(_orders);
        Assert.Equal(2, await db.AuditEntries.CountAsync());
        Assert.Equal(3, await db.CommandReceipts.CountAsync());
        Assert.Equal(1, (await db.NumberCounters.SingleAsync()).LastNumber);
        var accepted = await db.AuditEntries.SingleAsync(x => x.OrderVersion == 2);
        Assert.Equal(accepted.RecordedAtUtc, entries[1].GetProperty("recordedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task History_PagesInVersionOrderAndCursorIsBoundToOrderLimitTenantAndPlant()
    {
        using var created = await Create(_client, Guid.NewGuid());
        using var second = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", created.Headers.ETag!.ToString());
        using var third = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", second.Headers.ETag!.ToString(), UpdateBody(notes: "Third version"));
        var path = created.Headers.Location + "/history";
        var found = new List<long>();
        string? next = path + "?limit=1";
        string? cursor = null;
        while (next is not null)
        {
            var page = await _client.GetFromJsonAsync<JsonElement>(next);
            found.Add(Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("version").GetInt64());
            var token = page.GetProperty("nextCursor").GetString();
            cursor ??= token;
            next = token is null ? null : path + "?limit=1&cursor=" + Uri.EscapeDataString(token);
            Assert.True(found.Count <= 3);
        }
        Assert.Equal(new long[] { 1, 2, 3 }, found);
        var encoded = Uri.EscapeDataString(cursor!);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(path + "?limit=2&cursor=" + encoded)).StatusCode);
        var tampered = cursor![..20] + (cursor[20] == 'A' ? 'B' : 'A') + cursor[21..];
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(path + "?limit=1&cursor=" + Uri.EscapeDataString(tampered))).StatusCode);
        using var other = await Create(_client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(other.Headers.Location + "/history?limit=1&cursor=" + encoded)).StatusCode);
        await using var members = new ManagementAccessDbContext(_members);
        await members.Database.ExecuteSqlRawAsync("UPDATE identity_access.plant_memberships SET tenant_id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(path + "?limit=1&cursor=" + encoded)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(path)).StatusCode);
        var plant = Guid.NewGuid();
        await members.Database.ExecuteSqlInterpolatedAsync($"UPDATE identity_access.plant_memberships SET tenant_id = {Tenant}, plant_id = {plant}");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/v1/plants/{plant:D}/orders/{id:D}/history?limit=1&cursor={encoded}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?limit=bad")]
    [InlineData("?cursor=")]
    [InlineData("?cursor=bad")]
    [InlineData("?cursor=x&cursor=y")]
    [InlineData("?actorId=forbidden")]
    public async Task History_InvalidQueriesAreRejected(string query)
    {
        using var created = await Create(_client, Guid.NewGuid());
        using var response = await _client.GetAsync(created.Headers.Location + "/history" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_order_history_query", await response.Content.ReadAsStringAsync());
        await AssertCounts(1);
    }

    [Fact]
    public async Task History_ReadPermissionCurrentRevocationAndDeactivatedCustomer()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var path = created.Headers.Location + "/history";
        await using var customers = new CustomerDbContext(_customers);
        await customers.Database.ExecuteSqlRawAsync("UPDATE customers.laundry_customers SET is_active = false");
        await using var members = new ManagementAccessDbContext(_members);
        await members.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission <> 'orders.read'");
        var history = await _client.GetFromJsonAsync<JsonElement>(path);
        Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, history.GetProperty("items")[0].GetProperty("actorDisplayName").ValueKind);
        await members.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.read'");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(path)).StatusCode);
        await AssertCounts(1);
    }

    [Fact]
    public async Task History_AppendDuringPagingDoesNotDuplicateAndOutageReadRecovers()
    {
        using var created = await Create(_client, Guid.NewGuid());
        using var second = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", created.Headers.ETag!.ToString());
        var path = created.Headers.Location + "/history";
        var first = await _client.GetFromJsonAsync<JsonElement>(path + "?limit=1");
        var cursor = Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!);
        using var third = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", second.Headers.ETag!.ToString(), UpdateBody(notes: "Appended after read"));
        var continued = await _client.GetFromJsonAsync<JsonElement>(path + "?limit=1&cursor=" + cursor);
        Assert.Equal(2, continued.GetProperty("items")[0].GetProperty("version").GetInt64());
        await _postgres.StopAsync();
        using var failed = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Contains("order_history_unavailable", await failed.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Appended after read", await failed.Content.ReadAsStringAsync());
        await _postgres.StartAsync();
        _app.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["ConnectionStrings:Laundry"] = _postgres.GetConnectionString();
        var recovered = await _client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(3, recovered.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task History_DefaultAndMaximumPageSizesAreBounded()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var tag = created.Headers.ETag!.ToString();
        for (var version = 2; version <= 102; version++)
        {
            using var changed = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", tag, UpdateBody(notes: $"Synthetic version {version}"));
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            tag = changed.Headers.ETag!.ToString();
        }
        var path = created.Headers.Location + "/history";
        var first = await _client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(25, first.GetProperty("items").GetArrayLength());
        var maximum = await _client.GetFromJsonAsync<JsonElement>(path + "?limit=100");
        Assert.Equal(100, maximum.GetProperty("items").GetArrayLength());
        var cursor = Uri.EscapeDataString(maximum.GetProperty("nextCursor").GetString()!);
        var last = await _client.GetFromJsonAsync<JsonElement>(path + "?limit=100&cursor=" + cursor);
        Assert.Equal(new long[] { 101, 102 }, last.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("version").GetInt64()));
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task History_UnknownOrdersStayHiddenAndCallerCannotSupplyAuditName()
    {
        using var rejected = await Create(_client, Guid.NewGuid(), Body.TrimEnd('}') + ",\"actorDisplayName\":\"Forged request name\"}");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var response = await _client.GetAsync($"{Path}/{Guid.NewGuid():D}/history");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertCounts(0);
    }

    [Fact]
    public async Task History_AccountNameMigrationPreservesLegacyAuditAndAppendOnlyProtection()
    {
        await using var db = new OrderDbContext(_orders);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261011081625_InitialOrderPlanning");
        var id = Guid.NewGuid(); var auditId = Guid.NewGuid(); var key = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Orders.Add(new ServiceOrder { Id = id, TenantId = Tenant, PlantId = Plant, CustomerId = Customer, OrderNumber = "SYNTHETIC-LEGACY", PlannedReceivingDate = new DateOnly(2030, 1, 1), PlannedDeliveryDate = new DateOnly(2030, 1, 3), CreatedAtUtc = now, UpdatedAtUtc = now, CreatedByIssuer = ManagementAccessStore.DevelopmentIssuer, CreatedBySubject = _subject, UpdatedByIssuer = ManagementAccessStore.DevelopmentIssuer, UpdatedBySubject = _subject });
        await db.SaveChangesAsync();
        var plan = JsonSerializer.Serialize(new OrderPlan(Customer, new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 3)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO orders.order_audit_entries (id,tenant_id,plant_id,order_id,action,order_version,recorded_at_utc,actor_issuer,actor_subject,request_id,after_plan) VALUES ({auditId},{Tenant},{Plant},{id},'order_created',1,{now},{ManagementAccessStore.DevelopmentIssuer},{_subject},{key},{plan}::jsonb)");
        await migrator.MigrateAsync();
        var audit = await db.AuditEntries.SingleAsync();
        Assert.Null(audit.ActorDisplayName); Assert.Equal(auditId, audit.Id); Assert.Equal(key, audit.RequestId);
        Assert.Equal(_subject, audit.ActorSubject);
        var response = await _client.GetFromJsonAsync<JsonElement>($"{Path}/{id:D}/history");
        Assert.Equal(JsonValueKind.Null, response.GetProperty("items")[0].GetProperty("actorDisplayName").ValueKind);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM orders.order_audit_entries"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE orders.order_audit_entries SET actor_display_name = 'Do not rewrite history'"));
    }
}
