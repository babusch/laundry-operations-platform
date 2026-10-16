using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Laundry.Api.Orders;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Laundry.Api.Tests;

public sealed partial class OrderCreateEndpointTests
{
    [Fact]
    public async Task PublicationIntegration_CreateAndConcurrentRetriesProduceOneScopedFilteredCopy()
    {
        var key = Guid.NewGuid(); var body = WithVisibility(Body, "true");
        var replies = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Create(_client, key, body)));
        Assert.All(replies, x => Assert.Equal(HttpStatusCode.Created, x.StatusCode));
        foreach (var reply in replies) reply.Dispose();
        await using var db = new OrderDbContext(_orders);
        var order = await db.Orders.SingleAsync(); var head = await db.PublicationHeads.SingleAsync(); var sent = await db.PublicationOutbox.SingleAsync();
        Assert.Equal(Tenant, head.TenantId); Assert.Equal(Plant, head.PlantId); Assert.Equal(order.Id, head.OrderId);
        Assert.Equal(head.Payload, sent.Payload); Assert.Equal(1, head.Version);
        using var json = JsonDocument.Parse(head.Payload);
        Assert.Equal(Customer, json.RootElement.GetProperty("customer").GetProperty("id").GetGuid());
        Assert.Equal("Demo laundry customer", json.RootElement.GetProperty("customer").GetProperty("displayName").GetString());
        Assert.Equal("Synthetic planned order", json.RootElement.GetProperty("notes").GetString());
        Assert.DoesNotContain("actorSubject", head.Payload); Assert.DoesNotContain("customerReference", head.Payload);
        Assert.Equal(1, await db.AuditEntries.CountAsync()); Assert.Equal(1, await db.CommandReceipts.CountAsync());
    }

    [Fact]
    public async Task PublicationIntegration_HiddenAndManagementOnlyEditsDoNotCreateExtraRevisions()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var original = await created.Content.ReadFromJsonAsync<JsonElement>();
        var body = JsonSerializer.Serialize(new { customerId = Customer, plannedReceivingDate = "2030-01-01", plannedDeliveryDate = "2030-01-03", customerReference = "Synthetic management reference", notes = "Synthetic hidden updated note" });
        using var hiddenEdit = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", created.Headers.ETag!.ToString(), body);
        Assert.Equal(HttpStatusCode.OK, hiddenEdit.StatusCode);
        await using var db = new OrderDbContext(_orders);
        Assert.Equal(1, (await db.PublicationHeads.AsNoTracking().SingleAsync()).Version);
        Assert.Equal(1, await db.PublicationOutbox.CountAsync());
        using var visible = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", hiddenEdit.Headers.ETag!.ToString(), WithVisibility(body, "true"));
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        Assert.Equal(2, (await db.PublicationHeads.AsNoTracking().SingleAsync()).Version);
        using var hiddenAgain = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", visible.Headers.ETag!.ToString(), WithVisibility(body, "false"));
        Assert.Equal(HttpStatusCode.OK, hiddenAgain.StatusCode);
        var current = await db.PublicationHeads.AsNoTracking().SingleAsync(); Assert.Equal(3, current.Version);
        Assert.DoesNotContain("Synthetic hidden updated note", current.Payload);
        using var json = JsonDocument.Parse(current.Payload); Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("notes").ValueKind);
        Assert.Equal(original.GetProperty("id").GetGuid(), current.OrderId);
    }

    [Fact]
    public async Task PublicationIntegration_NoopCommandCanRefreshCurrentCustomerLabelsWithoutChangingOrderVersion()
    {
        using var created = await Create(_client, Guid.NewGuid());
        await using var db = new OrderDbContext(_orders);
        await db.Database.ExecuteSqlRawAsync("UPDATE customers.laundry_customers SET display_name = 'Synthetic renamed customer', code = 'DEMO-RENAMED', version = version + 1");
        var body = Body[..^1] + ",\"customerReference\":null}";
        using var unchanged = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", created.Headers.ETag!.ToString(), body);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal(1, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64());
        var head = await db.PublicationHeads.SingleAsync(); Assert.Equal(2, head.Version);
        Assert.Contains("Synthetic renamed customer", head.Payload); Assert.Contains("DEMO-RENAMED", head.Payload);
        Assert.Equal(1, await db.AuditEntries.CountAsync()); Assert.Equal(2, await db.CommandReceipts.CountAsync());
        Assert.Equal(2, await db.PublicationOutbox.CountAsync());
    }

    [Fact]
    public async Task PublicationIntegration_DelayedCreateAndEditRetriesNeverRepublishHistoricalState()
    {
        var createKey = Guid.NewGuid();
        using var created = await Create(_client, createKey, WithVisibility(Body, "true"));
        var editKey = Guid.NewGuid(); var body = WithVisibility(UpdateBody(notes: "Synthetic earlier visible edit"), "true");
        using var edited = await Update(_client, editKey, created.Headers.Location + "/plan", created.Headers.ETag!.ToString(), body);
        using var hidden = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", edited.Headers.ETag!.ToString(), WithVisibility(UpdateBody(notes: "Synthetic current hidden edit"), "false"));
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        await using var db = new OrderDbContext(_orders);
        var before = (await db.PublicationHeads.AsNoTracking().SingleAsync()).Payload;
        await db.Database.ExecuteSqlRawAsync("UPDATE customers.laundry_customers SET is_active = false, display_name = 'Synthetic later label'");
        using var createRetry = await Create(_client, createKey, WithVisibility(Body, "true"));
        using var editRetry = await Update(_client, editKey, created.Headers.Location + "/plan", created.Headers.ETag!.ToString(), body);
        Assert.Equal(HttpStatusCode.Created, createRetry.StatusCode); Assert.Equal(HttpStatusCode.OK, editRetry.StatusCode);
        Assert.Equal(before, (await db.PublicationHeads.AsNoTracking().SingleAsync()).Payload);
        Assert.Equal(3, await db.PublicationOutbox.CountAsync());
        await db.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.plan.edit'");
        using var revoked = await Update(_client, editKey, created.Headers.Location + "/plan", created.Headers.ETag!.ToString(), body);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal(3, await db.PublicationOutbox.CountAsync());
    }

    [Fact]
    public async Task PublicationIntegration_OutboxInsertFailureRollsBackEntireCreateAndRecoversWithSameKey()
    {
        await using var db = new OrderDbContext(_orders);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.publication_outbox ADD CONSTRAINT synthetic_reject CHECK (version = 0)");
        var key = Guid.NewGuid(); using var failed = await Create(_client, key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(0, await db.Orders.CountAsync()); Assert.Equal(0, await db.CommandReceipts.CountAsync());
        Assert.Equal(0, await db.AuditEntries.CountAsync()); Assert.Equal(0, await db.NumberCounters.CountAsync());
        Assert.Equal(0, await db.PublicationHeads.CountAsync()); Assert.Equal(0, await db.PublicationOutbox.CountAsync());
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.publication_outbox DROP CONSTRAINT synthetic_reject");
        using var recovered = await Create(_client, key);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        Assert.Equal("ORD-000001", (await db.Orders.SingleAsync()).OrderNumber);
        Assert.Single(await db.PublicationOutbox.ToArrayAsync());
    }

    [Fact]
    public async Task PublicationIntegration_LostCommitAcknowledgementRetryKeepsOneExactSendRecord()
    {
        await using var app = Application().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OrderDbContext>(options => options.AddInterceptors(new LoseCommitAcknowledgement()))));
        using var client = Client(app); Cookie(client, app); await Csrf(client);
        var key = Guid.NewGuid(); using var failed = await Create(client, key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        await using var db = new OrderDbContext(_orders); var original = await db.PublicationOutbox.AsNoTracking().SingleAsync();
        using var retried = await Create(client, key); Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        var same = await db.PublicationOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, same.Id); Assert.Equal(original.Payload, same.Payload);
        Assert.Equal(original.RecordedAtUtc, same.RecordedAtUtc); Assert.Equal(1, await db.CommandReceipts.CountAsync());
    }

    [Fact]
    public async Task PublicationIntegration_IneligibleCustomerDoesNotCreatePublicationOrOrder()
    {
        await using var db = new OrderDbContext(_orders);
        await db.Database.ExecuteSqlRawAsync("UPDATE customers.plant_services SET is_active = false");
        using var rejected = await Create(_client, Guid.NewGuid()); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Empty(await db.Orders.ToArrayAsync()); Assert.Empty(await db.PublicationHeads.ToArrayAsync());
        Assert.Empty(await db.PublicationOutbox.ToArrayAsync());
    }
}
