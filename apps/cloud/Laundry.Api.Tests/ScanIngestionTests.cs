using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Laundry.Api.Integrations.Scans;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Laundry.Api.Tests;

public sealed class ScanIngestionFixture : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } =
        new PostgreSqlBuilder("postgres:18.6-alpine3.23").Build();
    public WebApplicationFactory<Program> Application { get; private set; } = null!;

    public string ConnectionString => new NpgsqlConnectionStringBuilder(Postgres.GetConnectionString())
    {
        Timeout = 2, CommandTimeout = 2
    }.ConnectionString;

    public WebApplicationFactory<Program> CreateApplication(string environment = "Development",
        string remoteAddress = "127.0.0.1", string? tenant = null, string? plant = null,
        bool enabled = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Laundry", ConnectionString);
            builder.UseSetting("ScanIngestion:Enabled", enabled.ToString());
            builder.UseSetting("ScanIngestion:TenantId", tenant ?? "11111111-1111-4111-8111-111111111111");
            builder.UseSetting("ScanIngestion:PlantId", plant ?? "22222222-2222-4222-8222-222222222222");
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new TestRemoteAddress(remoteAddress)));
        });

    public async Task InitializeAsync()
    {
        await Postgres.StartAsync();
        Application = CreateApplication();
        using var scope = Application.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ScanDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Application is not null) await Application.DisposeAsync();
        await Postgres.DisposeAsync();
    }

    private sealed class TestRemoteAddress(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(address);
                return continuation(context);
            });
            next(app);
        };
    }
}

[Trait("Category", "Database")]
public sealed class ScanIngestionTests(ScanIngestionFixture fixture) : IClassFixture<ScanIngestionFixture>
{
    [Theory]
    [InlineData("barcode")]
    [InlineData("rfid")]
    public async Task ValidScanIsPersistedBeforeAcknowledgement_AndReplayReturnsSameReceipt(string technology)
    {
        var json = ScanContractTests.Example(technology);
        json["observedAtUtc"] = "2026-09-07T12:00:00.1234567Z";
        using var client = fixture.Application.CreateClient();
        using var first = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var receipt = (await first.Content.ReadFromJsonAsync<ScanReceipt>())!;
        Assert.Equal("accepted", receipt.Status);

        var stored = await StoredAsync(receipt.EventId);
        Assert.NotNull(stored);
        Assert.Equal(receipt.CloudReceivedAtUtc, stored.CloudReceivedAtUtc);
        Assert.NotNull(stored.PayloadJson);

        // Model a lost acknowledgement: the sender repeats the unchanged event.
        using var retry = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(receipt with { Status = "alreadyProcessed" },
            await retry.Content.ReadFromJsonAsync<ScanReceipt>());
        Assert.Equal(stored, await StoredAsync(receipt.EventId));
    }

    [Fact]
    public async Task ConcurrentRetriesStoreOneRow()
    {
        var json = ScanContractTests.Example();
        using var client = fixture.Application.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.PostAsJsonAsync("/api/scans", json)));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
            Assert.Equal(3, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
        Assert.NotNull(await StoredAsync(json["eventId"]!.GetValue<Guid>()));
    }

    [Fact]
    public async Task ChangedPayloadConflicts_WithoutOverwritingOriginal()
    {
        var json = ScanContractTests.Example();
        using var client = fixture.Application.CreateClient();
        using var first = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var eventId = json["eventId"]!.GetValue<Guid>();
        var stored = await StoredAsync(eventId);
        json["identifier"]!["value"] = "SIMULATED-CHANGED";
        using var conflict = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(stored, await StoredAsync(eventId));
    }

    [Fact]
    public async Task JsonWhitespaceAndPropertyOrderDoNotChangeIdentity()
    {
        var json = ScanContractTests.Example();
        using var client = fixture.Application.CreateClient();
        using var first = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var reversed = new JsonObject(json.Reverse().Select(x =>
            KeyValuePair.Create(x.Key, x.Value?.DeepClone())));
        using var content = new StringContent(reversed.ToJsonString(new JsonSerializerOptions
            { WriteIndented = true }), Encoding.UTF8, "application/json");
        using var second = await client.PostAsync("/api/scans", content);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task ConflictingConcurrentRequestsHaveOneWinner()
    {
        var original = ScanContractTests.Example();
        var changed = original.DeepClone();
        changed["identifier"]!["value"] = "SIMULATED-ALTERNATIVE";
        using var client = fixture.Application.CreateClient();
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/scans", original),
            client.PostAsJsonAsync("/api/scans", changed));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            var winner = responses[0].StatusCode == HttpStatusCode.Created ? original : changed;
            var stored = await StoredAsync(original["eventId"]!.GetValue<Guid>());
            Assert.Equal(winner["identifier"]!["value"]!.GetValue<string>(), stored!.IdentifierValue);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task DelayedScanAndIncorrectClockRemainAcceptedObservations()
    {
        var first = ScanContractTests.Example();
        first["observedAtUtc"] = "2027-01-01T12:00:00Z";
        var delayed = ScanContractTests.Example();
        delayed["observedAtUtc"] = "2026-01-01T12:00:00Z";
        using var client = fixture.Application.CreateClient();
        using var firstResponse = await client.PostAsJsonAsync("/api/scans", first);
        using var delayedResponse = await client.PostAsJsonAsync("/api/scans", delayed);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, delayedResponse.StatusCode);
        Assert.Equal(DateTimeOffset.Parse("2027-01-01T12:00:00Z"),
            (await StoredAsync(first["eventId"]!.GetValue<Guid>()))!.ObservedAtUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T12:00:00Z"),
            (await StoredAsync(delayed["eventId"]!.GetValue<Guid>()))!.ObservedAtUtc);
    }

    [Theory]
    [InlineData("tenantId")]
    [InlineData("plantId")]
    public async Task BodyCannotSelectAnotherTenantOrPlant(string property)
    {
        var json = ScanContractTests.Example();
        json[property] = Guid.NewGuid();
        using var client = fixture.Application.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await StoredAsync(json["eventId"]!.GetValue<Guid>()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EventIdCollisionAcrossScopesDoesNotRevealOrModifyOriginal(bool differentTenant)
    {
        var json = ScanContractTests.Example();
        using var client = fixture.Application.CreateClient();
        using var first = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var stored = await StoredAsync(json["eventId"]!.GetValue<Guid>());
        var otherId = Guid.NewGuid().ToString();
        json[differentTenant ? "tenantId" : "plantId"] = otherId;
        await using var otherApp = fixture.CreateApplication(
            tenant: differentTenant ? otherId : null, plant: differentTenant ? null : otherId);
        using var otherClient = otherApp.CreateClient();
        using var collision = await otherClient.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        Assert.DoesNotContain("SIMULATED", await collision.Content.ReadAsStringAsync());
        Assert.Equal(stored, await StoredAsync(json["eventId"]!.GetValue<Guid>()));
    }

    [Theory]
    [InlineData("Production", "127.0.0.1", true, HttpStatusCode.NotFound)]
    [InlineData("Staging", "127.0.0.1", true, HttpStatusCode.NotFound)]
    [InlineData("Development", "192.0.2.10", true, HttpStatusCode.Forbidden)]
    [InlineData("Development", "127.0.0.1", false, HttpStatusCode.NotFound)]
    public async Task DevelopmentBoundaryIsEnforced(string environment, string address, bool enabled,
        HttpStatusCode expected)
    {
        await using var app = fixture.CreateApplication(environment, address, enabled: enabled);
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/scans", ScanContractTests.Example());
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("{", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("null", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{}", "text/plain", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("{\"eventId\":\"a\",\"eventId\":\"b\"}", "application/json", HttpStatusCode.BadRequest)]
    public async Task InvalidRequestsAreRejected(string json, string mediaType, HttpStatusCode expected)
    {
        using var client = fixture.Application.CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, mediaType);
        using var response = await client.PostAsync("/api/scans", content);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task OversizedChunkedBodyIsRejected()
    {
        using var client = fixture.Application.CreateClient();
        using var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 16385))));
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scans") { Content = content };
        request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task OutageReturnsRetryableResponse_ThenSameEventCanBeSaved()
    {
        var json = ScanContractTests.Example();
        using var client = fixture.Application.CreateClient();
        await fixture.Postgres.StopAsync();
        try
        {
            using var unavailable = await client.PostAsJsonAsync("/api/scans", json);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.NotNull(unavailable.Headers.RetryAfter);
        }
        finally
        {
            await fixture.Postgres.StartAsync();
            fixture.Application.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Laundry"] =
                fixture.ConnectionString;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            using var retry = await client.PostAsJsonAsync("/api/scans", json, timeout.Token);
            if (retry.StatusCode == HttpStatusCode.Created) break;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
            await Task.Delay(250, timeout.Token);
        }
        Assert.NotNull(await StoredAsync(json["eventId"]!.GetValue<Guid>()));
    }

    private async Task<ScanObservation?> StoredAsync(Guid eventId)
    {
        using var scope = fixture.Application.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ScanDbContext>()
            .Observations.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == eventId);
    }
}
