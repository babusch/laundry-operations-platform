using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Laundry.Api.Customers;
using Laundry.Api.Orders;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Laundry.Api.Tests;

[Trait("Category", "Database")]
public sealed class OrderCreateEndpointTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23").Build();
    private readonly string _subject = Guid.NewGuid().ToString();
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;
    private DbContextOptions<OrderDbContext> _orders = null!;
    private DbContextOptions<ManagementAccessDbContext> _members = null!;
    private DbContextOptions<CustomerDbContext> _customers = null!;
    private static Guid Tenant => ManagementAccessStore.DevelopmentTenant;
    private static Guid Plant => ManagementAccessStore.DevelopmentPlant;
    private static Guid Customer => CustomerStore.DevelopmentCustomer;
    private static string Path => $"/api/v1/plants/{Plant:D}/orders";
    private static string Body => JsonSerializer.Serialize(new
    {
        customerId = Customer,
        plannedReceivingDate = "2030-01-01",
        plannedDeliveryDate = "2030-01-03",
        notes = "Synthetic planned order"
    });
    private static readonly SecurityKey GatewayTestKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("synthetic-gateway-test-signing-key-only-32-bytes"));

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _orders = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(_postgres.GetConnectionString(),
            provider => provider.MigrationsHistoryTable("__order_migrations_history", "orders")).Options;
        _members = new DbContextOptionsBuilder<ManagementAccessDbContext>().UseNpgsql(_postgres.GetConnectionString(),
            provider => provider.MigrationsHistoryTable("__management_access_migrations", "identity_access")).Options;
        _customers = new DbContextOptionsBuilder<CustomerDbContext>().UseNpgsql(_postgres.GetConnectionString(),
            provider => provider.MigrationsHistoryTable("__customer_migrations", "customers")).Options;
        await using var orders = new OrderDbContext(_orders);
        await orders.Database.MigrateAsync();
        await using var members = new ManagementAccessDbContext(_members);
        await members.Database.MigrateAsync();
        await new ManagementAccessStore(members).InitializeDevelopmentAsync(_subject);
        await using var customers = new CustomerDbContext(_customers);
        await customers.Database.MigrateAsync();
        await new CustomerStore(customers).InitializeDevelopmentAsync(Tenant, Plant);
        _app = Application();
        _client = Client(_app);
        Cookie(_client, _app);
        await Csrf(_client);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task ManagementPages_ShowSignedOutHomeAndFriendlySessionExpiry()
    {
        using var client = Client(_app);
        using var home = await client.GetAsync("/management?lang=en");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var html = await home.Content.ReadAsStringAsync();
        Assert.Contains("Sign in to continue", html);
        Assert.Contains("/management/login?home=true", html);
        Assert.Equal("no-store", home.Headers.CacheControl!.ToString());
        Assert.NotNull(home.Headers.GetValues("Content-Security-Policy"));
        using var expired = await client.GetAsync($"/management/orders/{Plant:D}?lang=sv");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Contains("Logga in", await expired.Content.ReadAsStringAsync());
        Directory.CreateDirectory(System.IO.Path.Combine(AppContext.BaseDirectory, "preview"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "preview", "signed-out.html"), html);
    }

    [Fact]
    public async Task ManagementPages_ShowOrdersDetailsTranslationsAndEncodeUntrustedText()
    {
        using var created = await Create(_client, Guid.NewGuid(), Body.Replace("Synthetic planned order", "<script>alert(1)</script>"));
        var order = await created.Content.ReadFromJsonAsync<JsonElement>();
        var home = await _client.GetStringAsync("/management?lang=sv");
        Assert.Contains("Inloggad", home);
        Assert.Contains("Visa ordrar", home);
        var list = await _client.GetStringAsync($"/management/orders/{Plant:D}?lang=sv");
        Assert.Contains("ORD-000001", list);
        Assert.Contains("Sparade ordrar", list);
        var details = await _client.GetStringAsync($"/management/orders/{Plant:D}/{order.GetProperty("id").GetGuid():D}?lang=en");
        Assert.Contains("Order details", details);
        Assert.Contains("&lt;script&gt;", details);
        Assert.DoesNotContain("<script>", details);
        Assert.DoesNotContain(_subject, details);
        using var denied = await _client.GetAsync($"/management/orders/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Directory.CreateDirectory(System.IO.Path.Combine(AppContext.BaseDirectory, "preview"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "preview", "home.html"), home);
        await File.WriteAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "preview", "orders.html"), list);
        await File.WriteAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "preview", "details.html"), details);
    }

    [Fact]
    public async Task ManagementPages_SignOutRequiresCsrfAndReturnsHome()
    {
        _client.DefaultRequestHeaders.Remove("X-Management-CSRF");
        using var rejected = await _client.PostAsync("/management/sign-out", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var html = await _client.GetStringAsync("/management");
        var token = System.Text.RegularExpressions.Regex.Match(html, "name='([^']+)' value='([^']+)'");
        Assert.True(token.Success);
        using var signedOut = await _client.PostAsync("/management/sign-out", new FormUrlEncodedContent(new Dictionary<string, string>
        { [token.Groups[1].Value] = WebUtility.HtmlDecode(token.Groups[2].Value) }));
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/management", signedOut.Headers.Location!.OriginalString);
        Assert.Contains(signedOut.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-laundry-management=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_EmptyAndSingleOrderArePublicReadOnlyPages()
    {
        var empty = await _client.GetFromJsonAsync<JsonElement>(Path);
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextCursor").ValueKind);
        using var created = await Create(_client, Guid.NewGuid());
        _client.DefaultRequestHeaders.Remove("X-Management-CSRF");
        using var response = await _client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(12, item.EnumerateObject().Count());
        Assert.DoesNotContain(_subject, page.GetRawText());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        await using var db = new OrderDbContext(_orders);
        Assert.Equal(1, await db.AuditEntries.CountAsync());
        Assert.Equal(1, await db.CommandReceipts.CountAsync());
        Assert.Equal(1, (await db.NumberCounters.SingleAsync()).LastNumber);
    }

    [Fact]
    public async Task List_DefaultPageIsBoundedAndMaximumLimitIsAccepted()
    {
        for (var index = 0; index < 26; index++)
        {
            using var created = await Create(_client, Guid.NewGuid());
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        var first = await _client.GetFromJsonAsync<JsonElement>(Path);
        Assert.Equal(25, first.GetProperty("items").GetArrayLength());
        var token = Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!);
        var last = await _client.GetFromJsonAsync<JsonElement>(Path + "?cursor=" + token);
        Assert.Single(last.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextCursor").ValueKind);
        var maximum = await _client.GetFromJsonAsync<JsonElement>(Path + "?limit=100");
        Assert.Equal(26, maximum.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, maximum.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task List_PagesHaveStableDateIdOrderingAndNoDuplicates()
    {
        foreach (var date in new[] { "2030-01-02", "2030-01-01", "2030-01-01" })
        {
            using var created = await Create(_client, Guid.NewGuid(), Body.Replace("2030-01-01", date));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        await using var db = new OrderDbContext(_orders);
        var expected = await db.Orders.OrderBy(x => x.PlannedReceivingDate).ThenBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        var found = new List<Guid>();
        string? path = Path + "?limit=1";
        while (path is not null)
        {
            var page = await _client.GetFromJsonAsync<JsonElement>(path);
            found.Add(Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
            var cursor = page.GetProperty("nextCursor").GetString();
            path = cursor is null ? null : Path + "?limit=1&cursor=" + Uri.EscapeDataString(cursor);
            Assert.True(found.Count <= 3);
        }
        Assert.Equal(expected, found);
        var first = await _client.GetFromJsonAsync<JsonElement>(Path + "?limit=1");
        var token = Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!);
        var raw = first.GetProperty("nextCursor").GetString()!;
        var tampered = raw[..20] + (raw[20] == 'A' ? "B" : "A") + raw[21..];
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(Path + "?limit=1&cursor=" + Uri.EscapeDataString(tampered))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(Path + "?limit=2&cursor=" + token)).StatusCode);
        // Cursor is scoped to its original tenant, even with a valid grant for the new tenant.
        await using var members = new ManagementAccessDbContext(_members);
        await members.Database.ExecuteSqlRawAsync("UPDATE identity_access.plant_memberships SET tenant_id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(Path + "?limit=1&cursor=" + token)).StatusCode);
        var otherTenant = await _client.GetFromJsonAsync<JsonElement>(Path);
        Assert.Empty(otherTenant.GetProperty("items").EnumerateArray());
        var otherPlant = Guid.NewGuid();
        await members.Database.ExecuteSqlInterpolatedAsync($"UPDATE identity_access.plant_memberships SET tenant_id = {Tenant}, plant_id = {otherPlant}");
        var otherPath = $"/api/v1/plants/{otherPlant:D}/orders";
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>(otherPath)).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(otherPath + "?limit=1&cursor=" + token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(Path)).StatusCode);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?limit=abc")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?cursor=invalid")]
    [InlineData("?cursor=")]
    [InlineData("?customerId=unsupported")]
    public async Task List_InvalidOrUnsupportedQueriesAreRejected(string query)
    {
        using var response = await _client.GetAsync(Path + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_order_query", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Details_FollowsLocationWithoutCsrfAndExposesOnlyPublicFieldsWithoutWrites()
    {
        using var created = await Create(_client, Guid.NewGuid());
        _client.DefaultRequestHeaders.Remove("X-Management-CSRF");
        using var response = await _client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created.Headers.ETag, response.Headers.ETag);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var details = await response.Content.ReadFromJsonAsync<JsonElement>();
        var original = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12, details.EnumerateObject().Count());
        foreach (var property in original.EnumerateObject())
            Assert.Equal(property.Value.ToString(), details.GetProperty(property.Name).ToString());
        Assert.DoesNotContain(_subject, details.GetRawText());
        Assert.False(details.TryGetProperty("tenantId", out _));
        await using var db = new OrderDbContext(_orders);
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(1, await db.AuditEntries.CountAsync());
        Assert.Equal(1, await db.CommandReceipts.CountAsync());
        Assert.Equal(1, (await db.NumberCounters.SingleAsync()).LastNumber);
    }

    [Fact]
    public async Task Details_ReturnsCurrentPlanRatherThanHistoricalCreationReceipt()
    {
        var key = Guid.NewGuid();
        using var created = await Create(_client, key);
        var original = await created.Content.ReadFromJsonAsync<JsonElement>();
        var scope = new OrderPlanningScope(Tenant, Plant, ManagementAccessStore.DevelopmentIssuer, _subject);
        var actor = new ManagementActor(scope.ActorIssuer, scope.ActorSubject, ManagementActorKind.Human, true);
        var store = new OrderPlanningStore(_orders, new SessionOrderPlanningAccess(actor, _members, _customers), TimeProvider.System);
        await store.UpdateAsync(scope, Guid.NewGuid(), original.GetProperty("id").GetGuid(), 1,
            new OrderPlan(Customer, new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 4)));
        using var current = await _client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var details = await current.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, details.GetProperty("version").GetInt64());
        Assert.Equal("2030-01-04", details.GetProperty("plannedDeliveryDate").GetString());
        Assert.NotEqual(created.Headers.ETag, current.Headers.ETag);
        using var retry = await Create(_client, key);
        Assert.Equal(created.Headers.ETag, retry.Headers.ETag);
        Assert.Equal(1, (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Details_ReadOnlyPermissionWorksAndRevocationAppliesImmediately()
    {
        using var created = await Create(_client, Guid.NewGuid());
        await using var db = new ManagementAccessDbContext(_members);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission <> 'orders.read'");
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(created.Headers.Location)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(Path)).StatusCode);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.read'");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(created.Headers.Location)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task Details_CreatePermissionAloneDoesNotAuthorizeReads()
    {
        using var created = await Create(_client, Guid.NewGuid());
        await using var db = new ManagementAccessDbContext(_members);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.read'");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(created.Headers.Location)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task Details_DeactivatedCustomerDoesNotHideSavedHistory()
    {
        using var created = await Create(_client, Guid.NewGuid());
        await using var db = new CustomerDbContext(_customers);
        await db.Database.ExecuteSqlRawAsync("UPDATE customers.laundry_customers SET is_active = false");
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(created.Headers.Location)).StatusCode);
        Assert.Single((await _client.GetFromJsonAsync<JsonElement>(Path)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Details_UnknownOrderAndOrderInAnotherTenantAreNotFound()
    {
        using var created = await Create(_client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Path}/{Guid.NewGuid():D}")).StatusCode);
        await using var db = new ManagementAccessDbContext(_members);
        // Same plant ID with a different authorized tenant must not expose the original order.
        await db.Database.ExecuteSqlRawAsync("UPDATE identity_access.plant_memberships SET tenant_id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'");
        using var response = await _client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("order_not_found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Details_StorageOutageReturnsSafeFailureAndReadRecovers()
    {
        using var created = await Create(_client, Guid.NewGuid());
        await _postgres.StopAsync();
        using var failed = await _client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Contains("order_storage_unavailable", await failed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.GetAsync(Path)).StatusCode);
        await _postgres.StartAsync();
        // Docker may assign a new random host port; future request scopes use that endpoint.
        _app.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Laundry"] = _postgres.GetConnectionString();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            using var recovered = await _client.GetAsync(created.Headers.Location, timeout.Token);
            if (recovered.StatusCode == HttpStatusCode.OK) break;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, recovered.StatusCode);
            await Task.Delay(250, timeout.Token);
        }
    }

    [Fact]
    public async Task Create_ReturnsDurablePlannedOrderWithTrustedActorAuditAndReceipt()
    {
        using var response = await Create(_client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.NotNull(response.Headers.Location);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("planned", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("2030-01-01", json.RootElement.GetProperty("plannedReceivingDate").GetString());
        Assert.Equal(Customer, json.RootElement.GetProperty("customerId").GetGuid());
        await using var db = new OrderDbContext(_orders);
        var stored = await db.Orders.SingleAsync();
        Assert.Equal(Tenant, stored.TenantId);
        Assert.Equal(Plant, stored.PlantId);
        Assert.Equal(_subject, stored.CreatedBySubject);
        Assert.Equal(_subject, (await db.AuditEntries.SingleAsync()).ActorSubject);
        Assert.Equal(1, await db.CommandReceipts.CountAsync());
        Assert.DoesNotContain(_subject, json.RootElement.GetRawText());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task ConcurrentAndLostAcknowledgementRetries_ProduceOneOrderAndSameReceipt()
    {
        var key = Guid.NewGuid();
        var replies = await Task.WhenAll(Create(_client, key), Create(_client, key), Create(_client, key));
        var bodies = await Task.WhenAll(replies.Select(x => x.Content.ReadAsStringAsync()));
        Assert.All(replies, x => Assert.Equal(HttpStatusCode.Created, x.StatusCode));
        Assert.All(bodies, x => Assert.Equal(bodies[0], x));
        using var retry = await Create(_client, key);
        Assert.Equal(bodies[0], await retry.Content.ReadAsStringAsync());
        await using var db = new OrderDbContext(_orders);
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(1, await db.AuditEntries.CountAsync());
        Assert.Equal(1, await db.CommandReceipts.CountAsync());
        foreach (var reply in replies) reply.Dispose();
    }

    [Fact]
    public async Task ReusedKeyWithChangedRequest_ConflictsWithoutMutation()
    {
        var key = Guid.NewGuid();
        using var first = await Create(_client, key);
        using var changed = await Create(_client, key, Body.Replace("2030-01-03", "2030-01-04"));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        await using var db = new OrderDbContext(_orders);
        Assert.Equal(new DateOnly(2030, 1, 3), (await db.Orders.SingleAsync()).PlannedDeliveryDate);
    }

    [Fact]
    public async Task PermissionRemoval_DeniesNewWorkAndRetriesButCustomerDeactivationKeepsHistoricalReceipt()
    {
        var key = Guid.NewGuid();
        using var first = await Create(_client, key);
        await using var customers = new CustomerDbContext(_customers);
        await customers.Database.ExecuteSqlRawAsync("UPDATE customers.laundry_customers SET is_active = false");
        using var receipt = await Create(_client, key);
        Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
        using var rejected = await Create(_client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        await using var members = new ManagementAccessDbContext(_members);
        await members.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.create'");
        using var deniedRetry = await Create(_client, key);
        Assert.Equal(HttpStatusCode.Forbidden, deniedRetry.StatusCode);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("service")]
    [InlineData("tenant")]
    public async Task IneligibleCustomer_IsRejectedBeforeOrderPersistence(string invalid)
    {
        await using var db = new CustomerDbContext(_customers);
        if (invalid == "customer") await db.Database.ExecuteSqlRawAsync("UPDATE customers.laundry_customers SET is_active = false");
        if (invalid == "service") await db.Database.ExecuteSqlRawAsync("UPDATE customers.plant_services SET is_active = false");
        var body = invalid == "tenant" ? Body.Replace(Customer.ToString(), Guid.NewGuid().ToString()) : Body;
        using var response = await Create(_client, Guid.NewGuid(), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var orders = new OrderDbContext(_orders);
        Assert.Equal(0, await orders.Orders.CountAsync());
    }

    [Fact]
    public async Task AnonymousGatewayOnlyAndWrongPlant_AreRejected()
    {
        using var anonymous = Client(_app);
        using var response = await Create(anonymous, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        var gateway = new JwtSecurityToken(ManagementAccessStore.DevelopmentIssuer, "laundry-cloud-api",
            [new Claim("sub", _subject), new Claim("client_id", "gateway-development"), new Claim("laundry_permissions", "orders.create")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(GatewayTestKey, SecurityAlgorithms.HmacSha256));
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(gateway));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Create(anonymous, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Path}/{Guid.NewGuid():D}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        using var wrongPlant = await Create(_client, Guid.NewGuid(), path: $"/api/v1/plants/{Guid.NewGuid():D}/orders");
        Assert.Equal(HttpStatusCode.Forbidden, wrongPlant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync($"/api/v1/plants/{Guid.NewGuid():D}/orders/{Guid.NewGuid():D}")).StatusCode);
    }

    [Fact]
    public async Task AmbiguousPlantAcrossTenants_IsRejectedInsteadOfGuessingScope()
    {
        await using var db = new ManagementAccessDbContext(_members);
        var other = new PlantMembership
        {
            Id = Guid.NewGuid(),
            ActorIssuer = ManagementAccessStore.DevelopmentIssuer,
            ActorSubject = _subject,
            TenantId = Guid.NewGuid(),
            PlantId = Plant,
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        other.Permissions.Add(new PlantMembershipPermission { MembershipId = other.Id, Permission = OrderPlanningPermissions.Create });
        db.Memberships.Add(other);
        await db.SaveChangesAsync();
        using var response = await Create(_client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("plant_scope_ambiguous", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await _client.GetAsync($"{Path}/{Guid.NewGuid():D}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.GetAsync(Path)).StatusCode);
        await using var orders = new OrderDbContext(_orders);
        Assert.Equal(0, await orders.Orders.CountAsync());
    }

    [Fact]
    public async Task CsrfMissingOrInvalid_IsRejected()
    {
        _client.DefaultRequestHeaders.Remove("X-Management-CSRF");
        using var missing = await Create(_client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        _client.DefaultRequestHeaders.Add("X-Management-CSRF", "invalid");
        using var invalid = await Create(_client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("date")]
    [InlineData("reversed")]
    [InlineData("reference")]
    [InlineData("notes")]
    [InlineData("null_body")]
    public async Task InvalidFields_AreRejectedWithoutPersistence(string invalid)
    {
        var body = invalid switch
        {
            "unknown" => Body.TrimEnd('}') + ",\"tenantId\":\"11111111-1111-4111-8111-111111111111\"}",
            "duplicate" => Body.TrimEnd('}') + ",\"notes\":\"duplicate\"}",
            "date" => Body.Replace("2030-01-01", "2030-02-30"),
            "reversed" => Body.Replace("2030-01-03", "2029-12-31"),
            "reference" => Body.TrimEnd('}') + ",\"customerReference\":\"" + new string('x', 101) + "\"}",
            "notes" => Body.Replace("Synthetic planned order", new string('x', 2001)),
            _ => "null"
        };
        using var response = await Create(_client, Guid.NewGuid(), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = new OrderDbContext(_orders);
        Assert.Equal(0, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task BodyAndHeaderBounds_AreExplicit()
    {
        using var emptyKey = await Create(_client, Guid.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, emptyKey.StatusCode);
        using var oversized = await Create(_client, Guid.NewGuid(), new string('x', 17000));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(Body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var wrongType = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
    }

    [Theory]
    [InlineData(false, "Development", "https://localhost:7100", "127.0.0.1", 404)]
    [InlineData(true, "Production", "https://localhost:7100", "127.0.0.1", 404)]
    [InlineData(true, "Development", "http://localhost:7100", "127.0.0.1", 403)]
    [InlineData(true, "Development", "https://localhost:7200", "127.0.0.1", 403)]
    [InlineData(true, "Development", "https://localhost:7100", "192.0.2.1", 403)]
    public async Task FeatureAndTransportBoundary_IsEnforcedBeforeMutation(bool enabled, string environment, string origin, string remote, int status)
    {
        await using var app = Application(enabled, environment, remote);
        using var client = Client(app, origin);
        using var response = await Create(client, Guid.NewGuid());
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(status, (int)(await client.GetAsync($"{Path}/{Guid.NewGuid():D}")).StatusCode);
        Assert.Equal(status, (int)(await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task AuditFailure_RollsBackWholeRequestAndSameKeyCanRecover()
    {
        await using var db = new OrderDbContext(_orders);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.order_audit_entries ADD CONSTRAINT test_failure CHECK (order_version = 0)");
        var key = Guid.NewGuid();
        using var rejected = await Create(_client, key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(0, await db.CommandReceipts.CountAsync());
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.order_audit_entries DROP CONSTRAINT test_failure");
        using var recovered = await Create(_client, key);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
    }

    [Fact]
    public async Task MutationAuthorizationAndEligibility_RowsRemainLockedUntilCommit()
    {
        await using var db = new OrderDbContext(_orders);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var actor = new ManagementActor(ManagementAccessStore.DevelopmentIssuer, _subject, ManagementActorKind.Human, true);
        var scope = new OrderPlanningScope(Tenant, Plant, actor.Issuer, actor.Subject);
        var access = new SessionOrderPlanningAccess(actor, _members, _customers);
        await using var lease = await access.BindTransactionAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), scope, default);
        Assert.True(await access.IsAllowedAsync(scope, OrderPlanningPermissions.Create, default));
        Assert.True(await access.IsCustomerEligibleAsync(scope, Customer, default));
        await using var other = new NpgsqlConnection(_postgres.GetConnectionString());
        await other.OpenAsync();
        foreach (var sql in new[] {
            "UPDATE identity_access.plant_memberships SET is_active = false",
            "DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.create'",
            "UPDATE customers.laundry_customers SET is_active = false",
            "UPDATE customers.plant_services SET is_active = false" })
        {
            await using var command = new NpgsqlCommand("SET lock_timeout = '100ms'; " + sql, other);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        }
        await transaction.CommitAsync();
        await using var released = new NpgsqlCommand("UPDATE customers.plant_services SET is_active = false", other);
        Assert.Equal(1, await released.ExecuteNonQueryAsync());
    }

    private async Task Csrf(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/management/antiforgery");
        client.DefaultRequestHeaders.Add("X-Management-CSRF", csrf.GetProperty("requestToken").GetString());
    }

    private static Task<HttpResponseMessage> Create(HttpClient client, Guid key, string? body = null, string? path = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path ?? Path) { Content = new StringContent(body ?? Body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return client.SendAsync(request);
    }

    private WebApplicationFactory<Program> Application(bool enabled = true, string environment = "Development", string remote = "127.0.0.1") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ManagementAuthentication:Enabled", "true");
            builder.UseSetting("ManagementAuthentication:ClientSecret", "synthetic-test-only");
            builder.UseSetting("ManagementAccess:Enabled", "true");
            builder.UseSetting("Customers:Enabled", "true");
            builder.UseSetting("OrderPlanning:Enabled", enabled.ToString());
            builder.UseSetting("ConnectionStrings:Laundry", _postgres.GetConnectionString());
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = null;
                    options.MetadataAddress = null!;
                    options.Configuration = new OpenIdConnectConfiguration { Issuer = ManagementAccessStore.DevelopmentIssuer };
                    options.Configuration.SigningKeys.Add(GatewayTestKey);
                    options.TokenValidationParameters.ValidIssuer = ManagementAccessStore.DevelopmentIssuer;
                    options.TokenValidationParameters.ValidAudience = "laundry-cloud-api";
                    options.TokenValidationParameters.IssuerSigningKey = GatewayTestKey;
                });
                services.AddSingleton<IStartupFilter>(new RemoteAddress(remote));
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, string origin = "https://localhost:7100") => app.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri(origin), AllowAutoRedirect = false });

    private void Cookie(HttpClient client, WebApplicationFactory<Program> app)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", ManagementAccessStore.DevelopmentIssuer), new Claim("sub", _subject),
            new Claim(ClaimTypes.NameIdentifier, _subject), new Claim(ManagementAuthentication.SessionMarker, "interactive")
        ], ManagementAuthentication.CookieScheme));
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(ManagementAuthentication.CookieScheme);
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
        { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(15) }, ManagementAuthentication.CookieScheme);
        client.DefaultRequestHeaders.Add("Cookie", "__Host-laundry-management=" + options.TicketDataFormat.Protect(ticket));
    }

    private sealed class RemoteAddress(string value) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, continuation) => { http.Connection.RemoteIpAddress = IPAddress.Parse(value); return continuation(http); });
            next(app);
        };
    }
}
