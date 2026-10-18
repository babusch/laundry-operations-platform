using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
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
public sealed class DevelopmentItemLookupTests(AcceptanceFixture fixture) : IClassFixture<AcceptanceFixture>
{
    private const string Route = "/api/development/item-lookup";
    private const string Towel = "SIMULATED-RFID-TOWEL-001";
    private WebApplicationFactory<Program> App(bool enabled = true, string environment = "Development", string address = "127.0.0.1",
        string? tenant = null, string? plant = null, bool scans = true) =>
        fixture.CreateApplication(environment, address, enabled: scans, tenant: tenant, plant: plant)
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["DevelopmentItemLookup:Enabled"] = enabled.ToString(), ["AllowedHosts"] = "*" })));
    private static HttpClient Client(WebApplicationFactory<Program> app, string origin = "https://localhost:7200") =>
        app.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
    private static async Task<(HttpClient Client, Guid Source)> Enroll(WebApplicationFactory<Program> app)
    {
        var client = Client(app);
        var created = await client.PostAsync("/api/development/source-enrollments", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var enrollment = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/source-enrollment/exchange",
            new { enrollmentCode = enrollment["enrollmentCode"]!.GetValue<string>() })).StatusCode);
        var session = (await client.GetFromJsonAsync<JsonObject>("/api/source-session"))!;
        client.DefaultRequestHeaders.Add(session["antiforgeryHeaderName"]!.GetValue<string>(), session["antiforgeryToken"]!.GetValue<string>());
        return (client, enrollment["sourceId"]!.GetValue<Guid>());
    }
    private static Task<HttpResponseMessage> Lookup(HttpClient client, string value = Towel) =>
        client.PostAsJsonAsync(Route, new { identifier = new { technology = "rfid", value } });
    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Contains("Cookie", response.Headers.Vary);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData(Towel, "towel", "DEMO-001")]
    [InlineData("SIMULATED-RFID-SHEET-001", "sheet", "DEMO-001")]
    [InlineData("SIMULATED-RFID-MOP-001", "mop", "SYNTHETIC-TEAM")]
    public async Task KnownFixturesReturnOnlySyntheticItemAndCustomer(string tag, string article, string customer)
    {
        using var app = App(); var session = await Enroll(app); using var client = session.Client;
        using var response = await Lookup(client, tag);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore); Assert.Contains("Cookie", response.Headers.Vary);
        var json = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(new[] { "isSynthetic", "item", "status" }, json.Select(x => x.Key).Order().ToArray());
        Assert.True(json["isSynthetic"]!.GetValue<bool>()); Assert.Equal("recognized", json["status"]!.GetValue<string>());
        var item = json["item"]!.AsObject();
        Assert.Equal(new[] { "articleType", "customer", "displayName", "id" }, item.Select(x => x.Key).Order().ToArray());
        Assert.NotEqual(Guid.Empty, item["id"]!.GetValue<Guid>());
        Assert.Equal(article, item["articleType"]!.GetValue<string>());
        Assert.Equal(customer, item["customer"]!["code"]!.GetValue<string>());
        Assert.Equal(new[] { "code", "displayName", "id" }, item["customer"]!.AsObject().Select(x => x.Key).Order().ToArray());
        Assert.DoesNotContain(tag, json.ToJsonString());
    }

    [Theory]
    [InlineData("SIMULATED-RFID-UNKNOWN")]
    [InlineData("simulated-rfid-towel-001")]
    [InlineData("unrecognized-input-do-not-echo")]
    public async Task UnknownDoesNotGuessCustomerOrEchoInput(string tag)
    {
        using var app = App(); var session = await Enroll(app); using var client = session.Client;
        using var response = await Lookup(client, tag);
        var result = await response.Content.ReadFromJsonAsync<DevelopmentItemLookupResult>();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new(true, "notRecognized", null), result);
        Assert.DoesNotContain(tag, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{\"identifier\":{\"technology\":\"barcode\",\"value\":\"SIMULATED-RFID-TOWEL-001\"}}")]
    [InlineData("{\"identifier\":{\"technology\":\"rfid\",\"value\":\"\"}}")]
    [InlineData("{\"identifier\":{\"technology\":\"rfid\",\"value\":\" tag \"}}")]
    [InlineData("{\"identifier\":{\"technology\":\"rfid\",\"value\":\"tag\\n\"}}")]
    [InlineData("{\"identifier\":{\"technology\":\"rfid\",\"value\":42}}")]
    [InlineData("{\"identifier\":{\"technology\":\"rfid\",\"value\":\"tag\",\"customerId\":\"other\"}}")]
    [InlineData("{\"identifier\":{\"technology\":\"rfid\",\"value\":\"tag\"},\"plantId\":\"other\"}")]
    [InlineData("{\"identifier\":{\"technology\":\"rfid\",\"value\":\"tag\",\"value\":\"other\"}}")]
    public async Task MalformedAndCallerAssertedScopeAreRejected(string body)
    {
        using var app = App(); var session = await Enroll(app); using var client = session.Client;
        await Error(await client.PostAsync(Route, new StringContent(body, Encoding.UTF8, "application/json")),
            HttpStatusCode.BadRequest, "invalid_development_item_request");
    }

    [Fact]
    public async Task MediaQueryLengthAndAntiforgeryAreBounded()
    {
        using var app = App(); var session = await Enroll(app); using var client = session.Client;
        await Error(await Lookup(client, new string('x', 257)), HttpStatusCode.BadRequest, "invalid_development_item_request");
        await Error(await client.PostAsync(Route + "?tenantId=other", new StringContent("{}", Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest, "invalid_development_item_request");
        await Error(await client.PostAsync(Route, new StringContent("{}")), HttpStatusCode.UnsupportedMediaType, "development_item_json_required");
        await Error(await client.PostAsync(Route, new StringContent(new string('x', 1025), Encoding.UTF8, "application/json")), HttpStatusCode.RequestEntityTooLarge, "development_item_request_too_large");
        await Error(await client.PostAsync(Route, new UnknownLengthBody()), HttpStatusCode.RequestEntityTooLarge, "development_item_request_too_large");
        client.DefaultRequestHeaders.Remove(SourceEnrollmentEndpoints.AntiforgeryHeaderName);
        await Error(await Lookup(client), HttpStatusCode.BadRequest, "development_item_antiforgery_required");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("disabled")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-plant")]
    [InlineData("adapter")]
    [InlineData("permission")]
    public async Task SourceAndPermissionBoundariesAreEnforced(string mode)
    {
        using var app = App(); var session = await Enroll(app); using var client = session.Client;
        if (mode == "missing")
        {
            using var anonymous = Client(app);
            await Error(await Lookup(anonymous), HttpStatusCode.Unauthorized, "development_item_source_required"); return;
        }
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        var source = await db.TrustedSources.SingleAsync(x => x.SourceId == session.Source);
        var credential = await db.SourceCredentials.SingleAsync(x => x.SourceId == session.Source);
        switch (mode)
        {
            case "revoked": credential.Status = SourceCredentialStatus.Revoked; break;
            case "expired":
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plant.source_credentials SET expires_at_utc={DateTimeOffset.UtcNow.AddHours(-1)} WHERE source_id={session.Source}"); break;
            case "disabled": source.Status = TrustedSourceStatus.Disabled; break;
            case "wrong-tenant":
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plant.trusted_sources SET tenant_id={Guid.NewGuid()} WHERE source_id={session.Source}"); break;
            case "wrong-plant":
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plant.trusted_sources SET plant_id={Guid.NewGuid()} WHERE source_id={session.Source}"); break;
            case "adapter":
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plant.trusted_sources SET source_kind='RemoteAdapter' WHERE source_id={session.Source}"); break;
            case "permission": db.SourcePermissions.Remove(await db.SourcePermissions.SingleAsync(x => x.SourceId == session.Source)); break;
        }
        await db.SaveChangesAsync();
        var forbidden = mode is "adapter" or "permission";
        await Error(await Lookup(client), forbidden ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized,
            forbidden ? "development_item_permission_required" : "development_item_source_required");
    }

    [Theory]
    [InlineData(false, "Development", true)]
    [InlineData(true, "Production", true)]
    [InlineData(true, "Development", false)]
    public async Task RouteIsNotAvailableWithoutExplicitDevelopmentActivation(bool enabled, string environment, bool scans)
    {
        using var app = App(enabled, environment, scans: scans); using var client = Client(app);
        Assert.Equal(HttpStatusCode.NotFound, (await Lookup(client)).StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:7200", "127.0.0.1", "", "")]
    [InlineData("https://localhost:7201", "127.0.0.1", "", "")]
    [InlineData("https://other.example:7200", "127.0.0.1", "", "")]
    [InlineData("https://localhost:7200", "192.0.2.10", "", "")]
    [InlineData("https://localhost:7200", "127.0.0.1", "https://other.example", "")]
    [InlineData("https://localhost:7200", "127.0.0.1", "", "cross-site")]
    public async Task TransportAndOriginFailBeforeLookup(string origin, string address, string header, string site)
    {
        using var app = App(address: address); using var client = Client(app, origin);
        if (header.Length > 0) client.DefaultRequestHeaders.Add("Origin", header);
        if (site.Length > 0) client.DefaultRequestHeaders.Add("Sec-Fetch-Site", site);
        await Error(await Lookup(client), HttpStatusCode.Forbidden, "development_item_origin_forbidden");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnotherInstallationCannotResolveDemoFixtures(bool foreignTenant)
    {
        using var app = App(tenant: foreignTenant ? Guid.NewGuid().ToString() : null, plant: foreignTenant ? null : Guid.NewGuid().ToString());
        var session = await Enroll(app); using var client = session.Client;
        await Error(await Lookup(client), HttpStatusCode.Forbidden, "development_item_fixture_scope_required");
    }

    [Fact]
    public async Task RetriesConcurrentLookupsAndOutOfOrderTagsNeverWriteOperationalState()
    {
        using var app = App(); var session = await Enroll(app); using var client = session.Client;
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        async Task<long[]> Snapshot() => new[] { await db.Observations.LongCountAsync(), await db.Outbox.LongCountAsync(),
            await db.LocalOrderPlans.LongCountAsync(), await db.OrderDownloadProgress.LongCountAsync(), await db.OrderDownloadReceipts.LongCountAsync(),
            await db.SourceSecurityAudits.LongCountAsync(), await db.SourceCredentials.LongCountAsync(),
            (await db.TrustedSources.AsNoTracking().SingleAsync(x => x.SourceId == session.Source)).ConfigurationVersion };
        var before = await Snapshot();
        var first = await client.PostAsJsonAsync(Route, new { identifier = new { technology = "rfid", value = Towel } });
        var expected = await first.Content.ReadAsStringAsync();
        foreach (var response in await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Lookup(client))))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(expected, await response.Content.ReadAsStringAsync()); response.Dispose();
        }
        foreach (var tag in new[] { "SIMULATED-RFID-MOP-001", "SIMULATED-RFID-UNKNOWN", "SIMULATED-RFID-SHEET-001", Towel })
            Assert.Equal(HttpStatusCode.OK, (await Lookup(client, tag)).StatusCode);
        Assert.Equal(before, await Snapshot());
        // Catalog itself has no HTTP/database dependencies; stable lookups require no cloud availability.
        Assert.Throws<ArgumentException>(() => DevelopmentItemCatalog.Lookup(new(Guid.NewGuid(), DevelopmentItemCatalog.PlantId), Towel));
    }

    [Fact]
    public async Task IdentityStorageOutageIs503NotUnknownAndRecoveryWorks()
    {
        using var app = App(); var session = await Enroll(app); using var client = session.Client;
        var config = app.Services.GetRequiredService<IConfiguration>();
        var original = config["ConnectionStrings:Plant"];
        try
        {
            config["ConnectionStrings:Plant"] = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Port = 1 }.ConnectionString;
            await Error(await Lookup(client), HttpStatusCode.ServiceUnavailable, "development_item_storage_unavailable");
        }
        finally { config["ConnectionStrings:Plant"] = original; }
        Assert.Equal(HttpStatusCode.OK, (await Lookup(client)).StatusCode);
    }

    private sealed class UnknownLengthBody : HttpContent
    {
        public UnknownLengthBody() => Headers.ContentType = new("application/json");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[1025]).AsTask();
    }
}
