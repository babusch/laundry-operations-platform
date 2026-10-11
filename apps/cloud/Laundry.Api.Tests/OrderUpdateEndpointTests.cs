using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Laundry.Api.Customers;
using Laundry.Api.Orders;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Laundry.Api.Tests;

public sealed partial class OrderCreateEndpointTests
{
    private static string UpdateBody(Guid? customer = null, string? reference = "Synthetic updated reference", string? notes = "Synthetic updated notes") =>
        JsonSerializer.Serialize(new { customerId = customer ?? Customer, plannedReceivingDate = "2030-01-02", plannedDeliveryDate = "2030-01-04", customerReference = reference, notes });

    private static Task<HttpResponseMessage> Update(HttpClient client, Guid key, string path, string? etag, string? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(body ?? UpdateBody(), Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Update_ChangesAllPlanningFieldsWithTrustedAuditAndStableIdentity()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var original = await created.Content.ReadFromJsonAsync<JsonElement>();
        var otherCustomer = Guid.NewGuid();
        await using var customers = new CustomerDbContext(_customers);
        customers.Customers.Add(new LaundryCustomer { Id = otherCustomer, TenantId = Tenant, Code = "SYNTHETIC-2", DisplayName = "Synthetic second customer", IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow });
        customers.PlantServices.Add(new CustomerPlantService { TenantId = Tenant, PlantId = Plant, CustomerId = otherCustomer, IsActive = true });
        await customers.SaveChangesAsync();
        using var response = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", created.Headers.ETag!.ToString(), UpdateBody(otherCustomer));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var field in new[] { "id", "plantId", "orderNumber", "status", "createdAtUtc" })
            Assert.Equal(original.GetProperty(field).ToString(), result.GetProperty(field).ToString());
        Assert.Equal(otherCustomer, result.GetProperty("customerId").GetGuid());
        Assert.Equal("2030-01-02", result.GetProperty("plannedReceivingDate").GetString());
        Assert.Equal("2030-01-04", result.GetProperty("plannedDeliveryDate").GetString());
        Assert.Equal("Synthetic updated reference", result.GetProperty("customerReference").GetString());
        Assert.Equal("Synthetic updated notes", result.GetProperty("notes").GetString());
        Assert.Equal(2, result.GetProperty("version").GetInt64());
        Assert.NotEqual(created.Headers.ETag, response.Headers.ETag);
        Assert.Equal(created.Headers.Location, response.Headers.Location);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.DoesNotContain(_subject, result.GetRawText());
        await using var db = new OrderDbContext(_orders);
        var audit = await db.AuditEntries.SingleAsync(x => x.Action == "order_plan_changed");
        Assert.Equal(_subject, audit.ActorSubject);
        Assert.Equal(2, audit.OrderVersion);
        Assert.Equal(Customer, JsonDocument.Parse(audit.BeforePlan!).RootElement.GetProperty("customerId").GetGuid());
        Assert.Equal(otherCustomer, JsonDocument.Parse(audit.AfterPlan).RootElement.GetProperty("customerId").GetGuid());
        Assert.Equal(_subject, (await db.Orders.SingleAsync()).UpdatedBySubject);
        Assert.Equal(2, await db.CommandReceipts.CountAsync());
        Assert.Equal(1, (await db.NumberCounters.SingleAsync()).LastNumber);
    }

    [Fact]
    public async Task Update_ConcurrentDifferentEditsCannotOverwriteAndUnchangedSaveHasNoChangeAudit()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var path = created.Headers.Location + "/plan";
        var tag = created.Headers.ETag!.ToString();
        var replies = await Task.WhenAll(Update(_client, Guid.NewGuid(), path, tag), Update(_client, Guid.NewGuid(), path, tag, UpdateBody(notes: "Other synthetic edit")));
        Assert.Equal(1, replies.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, replies.Count(x => x.StatusCode == HttpStatusCode.PreconditionFailed));
        var winner = replies.Single(x => x.IsSuccessStatusCode);
        var saved = await winner.Content.ReadAsStringAsync();
        using var unchanged = await Update(_client, Guid.NewGuid(), path, winner.Headers.ETag!.ToString(), PlanFrom(saved));
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal(saved, await unchanged.Content.ReadAsStringAsync());
        await using var db = new OrderDbContext(_orders);
        Assert.Equal(2, (await db.Orders.SingleAsync()).Version);
        Assert.Equal(2, await db.AuditEntries.CountAsync());
        Assert.Equal(3, await db.CommandReceipts.CountAsync());
        foreach (var reply in replies) reply.Dispose();
    }

    private static string PlanFrom(string representation)
    {
        using var document = JsonDocument.Parse(representation);
        var fields = new[] { "customerId", "plannedReceivingDate", "plannedDeliveryDate", "customerReference", "notes" };
        return JsonSerializer.Serialize(document.RootElement.EnumerateObject().Where(x => fields.Contains(x.Name)).ToDictionary(x => x.Name, x => x.Value));
    }

    [Fact]
    public async Task Update_ConcurrentExactRetryAndDelayedRetryNeverReapplyHistoricalChanges()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var path = created.Headers.Location + "/plan";
        var tag = created.Headers.ETag!.ToString();
        var key = Guid.NewGuid();
        var replies = await Task.WhenAll(Update(_client, key, path, tag), Update(_client, key, path, tag));
        Assert.All(replies, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        var saved = await replies[0].Content.ReadAsStringAsync();
        Assert.Equal(saved, await replies[1].Content.ReadAsStringAsync());
        using var later = await Update(_client, Guid.NewGuid(), path, replies[0].Headers.ETag!.ToString(), UpdateBody(notes: "Later synthetic edit"));
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        using var retry = await Update(_client, key, path, tag);
        Assert.Equal(saved, await retry.Content.ReadAsStringAsync());
        Assert.Equal(replies[0].Headers.ETag, retry.Headers.ETag);
        using var changed = await Update(_client, key, path, tag, UpdateBody(notes: "Changed retry"));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var changedCondition = await Update(_client, key, path, later.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, changedCondition.StatusCode);
        await using var db = new OrderDbContext(_orders);
        var current = await db.Orders.SingleAsync();
        Assert.Equal(3, current.Version);
        Assert.Equal("Later synthetic edit", current.Notes);
        Assert.Equal(3, await db.AuditEntries.CountAsync());
        Assert.Equal(3, await db.CommandReceipts.CountAsync());
        foreach (var reply in replies) reply.Dispose();
    }

    [Theory]
    [InlineData(null, 428)]
    [InlineData("*", 400)]
    [InlineData("weak", 400)]
    [InlineData("list", 400)]
    [InlineData("zero", 400)]
    [InlineData("overflow", 400)]
    [InlineData("wrong_order", 412)]
    public async Task Update_RequiresOneStrongOrderSpecificVersion(string? invalid, int status)
    {
        using var created = await Create(_client, Guid.NewGuid());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var tag = created.Headers.ETag!.ToString();
        var condition = invalid switch { "weak" => "W/" + tag, "list" => tag + ", " + tag, "zero" => $"\"{id:D}:0\"", "overflow" => $"\"{id:D}:9999999999999999999999\"", "wrong_order" => $"\"{Guid.NewGuid():D}:1\"", _ => invalid };
        using var rejected = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", condition);
        Assert.Equal(status, (int)rejected.StatusCode);
        await AssertCounts(1);
    }

    [Theory]
    [InlineData("missing_reference")]
    [InlineData("missing_notes")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("reversed")]
    [InlineData("reference_limit")]
    [InlineData("notes_limit")]
    public async Task Update_InvalidReplacementDoesNotWrite(string invalid)
    {
        using var created = await Create(_client, Guid.NewGuid());
        var body = invalid switch
        {
            "missing_reference" => Body,
            "missing_notes" => UpdateBody().Replace(",\"notes\":\"Synthetic updated notes\"", ""),
            "unknown" => UpdateBody().TrimEnd('}') + ",\"status\":\"planned\"}",
            "duplicate" => UpdateBody().TrimEnd('}') + ",\"notes\":null}",
            "reversed" => UpdateBody().Replace("2030-01-04", "2029-01-01"),
            "reference_limit" => UpdateBody(reference: new string('x', 101)),
            _ => UpdateBody(notes: new string('x', 2001))
        };
        using var rejected = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", created.Headers.ETag!.ToString(), body);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        await AssertCounts(1);
    }

    [Fact]
    public async Task Update_NullClearsOptionalFieldsAndCurrentEligibilityAndEditPermissionAreRequiredOnRetries()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var path = created.Headers.Location + "/plan";
        var key = Guid.NewGuid();
        var tag = created.Headers.ETag!.ToString();
        using var cleared = await Update(_client, key, path, tag, UpdateBody(reference: null, notes: null));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notes").ValueKind);
        await using var customers = new CustomerDbContext(_customers);
        await customers.Database.ExecuteSqlRawAsync("UPDATE customers.plant_services SET is_active = false");
        using var historical = await Update(_client, key, path, tag, UpdateBody(reference: null, notes: null));
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        using var ineligible = await Update(_client, Guid.NewGuid(), path, cleared.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, ineligible.StatusCode);
        await using var members = new ManagementAccessDbContext(_members);
        await members.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.plan.edit'");
        using var revoked = await Update(_client, key, path, tag, UpdateBody(reference: null, notes: null));
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(created.Headers.Location)).StatusCode);
        await using var db = new OrderDbContext(_orders);
        Assert.Equal(2, await db.CommandReceipts.CountAsync());
    }

    [Fact]
    public async Task Update_UnknownAndOtherTenantOrdersStayHiddenAndWrongPlantIsDenied()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var missing = Guid.NewGuid();
        using var unknown = await Update(_client, Guid.NewGuid(), $"{Path}/{missing:D}/plan", $"\"{missing:D}:1\"");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var wrongPlant = await Update(_client, Guid.NewGuid(), $"/api/v1/plants/{Guid.NewGuid():D}/orders/{missing:D}/plan", $"\"{missing:D}:1\"");
        Assert.Equal(HttpStatusCode.Forbidden, wrongPlant.StatusCode);
        await using var members = new ManagementAccessDbContext(_members);
        await members.Database.ExecuteSqlRawAsync("UPDATE identity_access.plant_memberships SET tenant_id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'");
        // Use a customer eligible in the new authorized tenant so the target scope is tested.
        await using var customers = new CustomerDbContext(_customers);
        await customers.Database.ExecuteSqlRawAsync("UPDATE customers.plant_services SET is_active = false");
        var other = Guid.NewGuid();
        var tenant = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        customers.Customers.Add(new LaundryCustomer { Id = other, TenantId = tenant, Code = "SYNTHETIC-OTHER", DisplayName = "Synthetic other tenant", IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow });
        customers.PlantServices.Add(new CustomerPlantService { TenantId = tenant, PlantId = Plant, CustomerId = other, IsActive = true });
        await customers.SaveChangesAsync();
        using var hidden = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", created.Headers.ETag!.ToString(), UpdateBody(other));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        await AssertCounts(1);
    }

    [Fact]
    public async Task Update_CsrfAndSessionAreRequiredAndAuditFailureRollsBackThenRecovers()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var path = created.Headers.Location + "/plan";
        var tag = created.Headers.ETag!.ToString();
        using var anonymous = Client(_app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Update(anonymous, Guid.NewGuid(), path, tag)).StatusCode);
        _client.DefaultRequestHeaders.Remove("X-Management-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await Update(_client, Guid.NewGuid(), path, tag)).StatusCode);
        await Csrf(_client);
        await using var db = new OrderDbContext(_orders);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.order_audit_entries ADD CONSTRAINT update_test_failure CHECK (order_version < 2)");
        var key = Guid.NewGuid();
        using var failed = await Update(_client, key, path, tag);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.DoesNotContain("Synthetic updated", await failed.Content.ReadAsStringAsync());
        await AssertCounts(1);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.order_audit_entries DROP CONSTRAINT update_test_failure");
        Assert.Equal(HttpStatusCode.OK, (await Update(_client, key, path, tag)).StatusCode);
    }

    [Fact]
    public async Task Update_LostCommitAcknowledgementAndDatabaseOutageRecoverUsingSameRequest()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var path = created.Headers.Location + "/plan";
        var tag = created.Headers.ETag!.ToString();
        var key = Guid.NewGuid();
        await using var app = Application().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OrderDbContext>(options => options.AddInterceptors(new LoseCommitAcknowledgement()))));
        using var client = Client(app);
        Cookie(client, app);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Update(client, key, path, tag)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Update(client, key, path, tag)).StatusCode);
        await _postgres.StopAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Update(_client, key, path, tag)).StatusCode);
        await _postgres.StartAsync();
        _app.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Laundry"] = _postgres.GetConnectionString();
        _orders = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options;
        Assert.Equal(HttpStatusCode.OK, (await Update(_client, key, path, tag)).StatusCode);
        await using var db = new OrderDbContext(_orders);
        Assert.Equal(2, (await db.Orders.SingleAsync()).Version);
        Assert.Equal(2, await db.AuditEntries.CountAsync());
        Assert.Equal(2, await db.CommandReceipts.CountAsync());
    }

    [Fact]
    public async Task Update_NonPlannedOrderIsBlockedWithoutSpeculativeProductionSchemaChange()
    {
        using var created = await Create(_client, Guid.NewGuid());
        await using var db = new OrderDbContext(_orders);
        // Test-only fixture: production migration still permits planned orders only.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.service_orders DROP CONSTRAINT ck_order_status; UPDATE orders.service_orders SET status = 'receiving'");
        using var rejected = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", created.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("order_not_planned", await rejected.Content.ReadAsStringAsync());
        await AssertCounts(1);
    }

    [Fact]
    public async Task Update_RequestBoundsAndInvalidCsrfAreRejected()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var path = created.Headers.Location + "/plan";
        var tag = created.Headers.ETag!.ToString();
        Assert.Equal(HttpStatusCode.BadRequest, (await Update(_client, Guid.Empty, path, tag)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Update(_client, Guid.NewGuid(), path, tag, new string('x', 17000))).StatusCode);
        using var wrongType = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(UpdateBody()) };
        wrongType.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        wrongType.Headers.TryAddWithoutValidation("If-Match", tag);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _client.SendAsync(wrongType)).StatusCode);
        _client.DefaultRequestHeaders.Remove("X-Management-CSRF");
        _client.DefaultRequestHeaders.Add("X-Management-CSRF", "invalid");
        Assert.Equal(HttpStatusCode.BadRequest, (await Update(_client, Guid.NewGuid(), path, tag)).StatusCode);
        await AssertCounts(1);
    }
}
