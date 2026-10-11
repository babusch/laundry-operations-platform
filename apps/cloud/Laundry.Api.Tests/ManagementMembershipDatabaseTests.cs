using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Laundry.Api.Orders;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Laundry.Api.Tests;

[Trait("Category", "Database")]
public sealed class ManagementMembershipDatabaseTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23").Build();
    private readonly string _subject = Guid.NewGuid().ToString();
    private DbContextOptions<ManagementAccessDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _options = new DbContextOptionsBuilder<ManagementAccessDbContext>().UseNpgsql(_postgres.GetConnectionString(),
            provider => provider.MigrationsHistoryTable("__management_access_migrations", "identity_access")).Options;
        await using var db = new ManagementAccessDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();
    private ManagementActor Actor() => new(ManagementAccessStore.DevelopmentIssuer, _subject, ManagementActorKind.Human, true);

    [Fact]
    public async Task ExplicitBootstrap_PersistsExactlyApprovedScopedPermissionsAndAudit()
    {
        await using var db = new ManagementAccessDbContext(_options);
        var store = new ManagementAccessStore(db);
        Assert.True(await store.InitializeDevelopmentAsync(_subject));
        Assert.False(await store.InitializeDevelopmentAsync(_subject));
        var membership = await db.Memberships.AsNoTracking().Include(x => x.Permissions).SingleAsync();
        Assert.Equal(ManagementAccessStore.DevelopmentTenant, membership.TenantId);
        Assert.Equal(ManagementAccessStore.DevelopmentPlant, membership.PlantId);
        Assert.Equal(OrderPlanningPermissions.Management.Order(), membership.Permissions.Select(x => x.Permission).Order());
        Assert.Single(await db.AuditEntries.ToListAsync());
        Assert.Equal(TimeSpan.Zero, membership.CreatedAtUtc.Offset);
        foreach (var permission in OrderPlanningPermissions.Management)
            Assert.True(await store.IsAllowedAsync(Actor(), membership.TenantId, membership.PlantId, permission));
    }

    [Fact]
    public async Task ConcurrentBootstrap_ConvergesOnOneAssignmentAndAudit()
    {
        async Task<bool> Provision()
        {
            await using var db = new ManagementAccessDbContext(_options);
            return await new ManagementAccessStore(db).InitializeDevelopmentAsync(_subject);
        }
        var results = await Task.WhenAll(Provision(), Provision(), Provision());
        Assert.Single(results, x => x);
        await using var read = new ManagementAccessDbContext(_options);
        Assert.Equal(1, await read.Memberships.CountAsync());
        Assert.Equal(1, await read.AuditEntries.CountAsync());
        Assert.Equal(3, await read.Permissions.CountAsync());
    }

    [Fact]
    public async Task NoAssignment_MachineOtherIdentityAndWrongScope_AreDenied()
    {
        await using var db = new ManagementAccessDbContext(_options);
        var store = new ManagementAccessStore(db);
        Assert.Empty(await store.ReadAssignmentsAsync(Actor()));
        await store.InitializeDevelopmentAsync(_subject);
        Assert.False(await store.IsAllowedAsync(Actor(), Guid.NewGuid(), ManagementAccessStore.DevelopmentPlant, OrderPlanningPermissions.Read));
        Assert.False(await store.IsAllowedAsync(Actor(), ManagementAccessStore.DevelopmentTenant, Guid.NewGuid(), OrderPlanningPermissions.Read));
        Assert.False(await store.IsAllowedAsync(Actor() with { Kind = ManagementActorKind.Gateway }, ManagementAccessStore.DevelopmentTenant, ManagementAccessStore.DevelopmentPlant, OrderPlanningPermissions.Read));
        Assert.Empty(await store.ReadAssignmentsAsync(Actor() with { Subject = Guid.NewGuid().ToString() }));
        Assert.Empty(await store.ReadAssignmentsAsync(Actor() with { Issuer = "https://other.example.test" }));
        Assert.False(await store.IsAllowedAsync(Actor(), ManagementAccessStore.DevelopmentTenant, ManagementAccessStore.DevelopmentPlant, "orders.complete"));
    }

    [Fact]
    public async Task PermissionRemovalAndDeactivation_AffectNextReadAndBootstrapDoesNotUndoThem()
    {
        await using var db = new ManagementAccessDbContext(_options);
        var store = new ManagementAccessStore(db);
        await store.InitializeDevelopmentAsync(_subject);
        // Simulate a future privileged administration path; no such HTTP mutation is implemented.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.create'");
        Assert.False(await store.IsAllowedAsync(Actor(), ManagementAccessStore.DevelopmentTenant, ManagementAccessStore.DevelopmentPlant, OrderPlanningPermissions.Create));
        Assert.True(await store.IsAllowedAsync(Actor(), ManagementAccessStore.DevelopmentTenant, ManagementAccessStore.DevelopmentPlant, OrderPlanningPermissions.Read));
        await db.Database.ExecuteSqlRawAsync("UPDATE identity_access.plant_memberships SET is_active = false, version = version + 1");
        Assert.Empty(await store.ReadAssignmentsAsync(Actor()));
        Assert.False(await store.InitializeDevelopmentAsync(_subject));
        Assert.False(await store.IsAllowedAsync(Actor(), ManagementAccessStore.DevelopmentTenant, ManagementAccessStore.DevelopmentPlant, OrderPlanningPermissions.Read));
    }

    [Fact]
    public async Task AuditFailure_RollsBackMembershipAndPermissionsAndRetryCanRecover()
    {
        await using var db = new ManagementAccessDbContext(_options);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE identity_access.membership_audit_entries ADD CONSTRAINT test_failure CHECK (membership_version = 0)");
        await Assert.ThrowsAsync<DbUpdateException>(() => new ManagementAccessStore(db).InitializeDevelopmentAsync(_subject));
        await using var check = new ManagementAccessDbContext(_options);
        Assert.Equal(0, await check.Memberships.CountAsync());
        Assert.Equal(0, await check.Permissions.CountAsync());
        Assert.Equal(0, await check.AuditEntries.CountAsync());
        await check.Database.ExecuteSqlRawAsync("ALTER TABLE identity_access.membership_audit_entries DROP CONSTRAINT test_failure");
        Assert.True(await new ManagementAccessStore(check).InitializeDevelopmentAsync(_subject));
    }

    [Fact]
    public async Task AuditIsAppendOnlyAndModelMigrationsAreCurrent()
    {
        await using var db = new ManagementAccessDbContext(_options);
        await new ManagementAccessStore(db).InitializeDevelopmentAsync(_subject);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.membership_audit_entries"));
        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.MigrateAsync();
        Assert.Equal(1, await db.Memberships.CountAsync());
    }

    [Fact]
    public async Task InvalidPermissionsAndOrphanGrants_FailDatabaseConstraints()
    {
        await using var db = new ManagementAccessDbContext(_options);
        await new ManagementAccessStore(db).InitializeDevelopmentAsync(_subject);
        var member = await db.Memberships.SingleAsync();
        db.Permissions.Add(new PlantMembershipPermission { MembershipId = member.Id, Permission = "*" });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        db.ChangeTracker.Clear();
        db.Permissions.Add(new PlantMembershipPermission { MembershipId = Guid.NewGuid(), Permission = OrderPlanningPermissions.Read });
        var orphan = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(orphan.InnerException).SqlState);
    }

    [Fact]
    public async Task AccessEndpoint_UsesProtectedCurrentHumanAndExposesOnlyOwnAssignments()
    {
        await using var db = new ManagementAccessDbContext(_options);
        await new ManagementAccessStore(db).InitializeDevelopmentAsync(_subject);
        await using var app = Application();
        using var anonymous = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/management/access")).StatusCode);
        using var signedIn = Client(app);
        Cookie(signedIn, app, _subject);
        using var response = await signedIn.GetAsync("/management/access");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Single(json.RootElement.GetProperty("assignments").EnumerateArray());
        Assert.False(json.RootElement.GetProperty("orderAccessEnabled").GetBoolean());
        Assert.DoesNotContain(_subject, body);
        Assert.DoesNotContain(ManagementAccessStore.DevelopmentIssuer, body);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Cookie(signedIn, app, Guid.NewGuid().ToString());
        var other = await signedIn.GetFromJsonAsync<JsonElement>("/management/access");
        Assert.Empty(other.GetProperty("assignments").EnumerateArray());
    }

    [Fact]
    public async Task AccessEndpoint_DisabledReturns404AndDatabaseFailureNeverGrantsAccess()
    {
        await using var disabled = Application(enabled: false);
        using var off = Client(disabled);
        Cookie(off, disabled, _subject);
        Assert.Equal(HttpStatusCode.NotFound, (await off.GetAsync("/management/access")).StatusCode);
        await using var broken = Application(connection: "Host=localhost;Port=1;Database=unreachable;Username=synthetic;Timeout=1;Pooling=false");
        using var client = Client(broken);
        Cookie(client, broken, _subject);
        using var response = await client.GetAsync("/management/access");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("management_access_unavailable", await response.Content.ReadAsStringAsync());
    }

    private WebApplicationFactory<Program> Application(bool enabled = true, string? connection = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ManagementAuthentication:Enabled", "true");
            builder.UseSetting("ManagementAuthentication:ClientSecret", "synthetic-test-only");
            builder.UseSetting("ManagementAccess:Enabled", enabled.ToString());
            builder.UseSetting("ConnectionStrings:Laundry", connection ?? _postgres.GetConnectionString());
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddSingleton<IStartupFilter>(new LoopbackAddress());
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> app) => app.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:7100"), AllowAutoRedirect = false });

    private static void Cookie(HttpClient client, WebApplicationFactory<Program> app, string subject)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", ManagementAccessStore.DevelopmentIssuer), new Claim("sub", subject),
            new Claim(ManagementAuthentication.SessionMarker, "interactive")
        ], ManagementAuthentication.CookieScheme));
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(ManagementAuthentication.CookieScheme);
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
        }, ManagementAuthentication.CookieScheme);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", "__Host-laundry-management=" + options.TicketDataFormat.Protect(ticket));
    }

    private sealed class LoopbackAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, continuation) =>
            {
                http.Connection.RemoteIpAddress = IPAddress.Loopback;
                return continuation(http);
            });
            next(app);
        };
    }
}
