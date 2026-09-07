using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Laundry.Edge.Persistence;
using Laundry.Edge.Scans;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Laundry.Edge.Tests;

[Trait("Category", "Database")]
public sealed class ScanAcceptanceTests(AcceptanceFixture fixture) : IClassFixture<AcceptanceFixture>
{
    [Theory]
    [InlineData("barcode")]
    [InlineData("rfid")]
    public async Task AcceptanceStoresCompatibleEventAndPendingOutbox_RetryReturnsOriginalReceipt(string technology)
    {
        var json = SubmissionContractTests.Example(technology);
        json["observedAtUtc"] = "2035-01-01T12:00:00.1234567Z";
        using var client = fixture.Application.CreateClient();
        using var first = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var receipt = (await first.Content.ReadFromJsonAsync<LocalReceipt>())!;
        Assert.Equal("acceptedLocally", receipt.Status);
        Assert.Equal("pending", receipt.DeliveryStatus);
        var stored = await Stored(json);
        Assert.NotNull(stored);
        Assert.Equal(receipt.GatewayAcceptedAtUtc, stored.AcceptedAtUtc);
        using var payload = JsonDocument.Parse(stored.EventJson);
        Assert.Equal(json["observedAtUtc"]!.GetValue<string>(), payload.RootElement.GetProperty("observedAtUtc").GetString());
        Assert.Equal(receipt.GatewayAcceptedAtUtc, payload.RootElement.GetProperty("gatewayAcceptedAtUtc").GetDateTimeOffset());
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "scan-observed.v1.schema.json")));
        Assert.True(JsonSchema.Build(schema.RootElement.Clone(), new BuildOptions { SchemaRegistry = new() })
            .Evaluate(payload.RootElement, new EvaluationOptions { RequireFormatValidation = true }).IsValid);
        await AssertPair(json);

        using var retry = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(receipt with { Status = "alreadyAcceptedLocally" }, await retry.Content.ReadFromJsonAsync<LocalReceipt>());
        Assert.Equal(stored, await Stored(json));
        await AssertPair(json);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRetriesOrConflictsCreateExactlyOnePair(bool conflict)
    {
        var json = SubmissionContractTests.Example();
        var other = json.DeepClone().AsObject();
        if (conflict) other["identifier"]!["value"] = "SIMULATED-OTHER";
        using var client = fixture.Application.CreateClient();
        var responses = await Task.WhenAll(client.PostAsJsonAsync("/api/scans", json), client.PostAsJsonAsync("/api/scans", other));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, x => x.StatusCode == (conflict ? HttpStatusCode.Conflict : HttpStatusCode.OK));
            var stored = (await Stored(json))!;
            var winner = responses[0].StatusCode == HttpStatusCode.Created ? json : other;
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(winner), JsonDocument.Parse(stored.SubmissionJson).RootElement));
            await AssertPair(json);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task EquivalentJsonRetryAndGatewayRestartPreserveEvidence()
    {
        var json = SubmissionContractTests.Example();
        using (var app = fixture.CreateApplication())
        using (var client = app.CreateClient())
        using (var response = await client.PostAsJsonAsync("/api/scans", json))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await Stored(json);
        using var restarted = fixture.CreateApplication();
        using var retryClient = restarted.CreateClient();
        var reordered = new JsonObject(json.Reverse().Select(x => new KeyValuePair<string, JsonNode?>(x.Key, x.Value?.DeepClone())));
        using var content = new StringContent(reordered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8, "application/json");
        using var retry = await retryClient.PostAsync("/api/scans", content);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(stored, await Stored(json));
        using var scope = restarted.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        await database.Database.MigrateAsync();
        Assert.False(database.Database.HasPendingModelChanges());
        await AssertPair(json);
    }

    [Theory]
    [InlineData("tenantId")]
    [InlineData("plantId")]
    [InlineData("stationId")]
    [InlineData("deviceId")]
    public async Task UntrustedSourceIsRejected(string field)
    {
        var json = SubmissionContractTests.Example();
        json[field] = Guid.NewGuid().ToString();
        using var client = fixture.Application.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await Stored(json));
    }

    [Theory]
    [InlineData("tenantId")]
    [InlineData("plantId")]
    public async Task CrossScopeCollisionDoesNotRevealOrModifyOriginal(string field)
    {
        var json = SubmissionContractTests.Example();
        using var client = fixture.Application.CreateClient();
        using var first = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await Stored(json);
        var otherId = Guid.NewGuid().ToString();
        json[field] = otherId;
        using var app = fixture.CreateApplication(tenant: field == "tenantId" ? otherId : null,
            plant: field == "plantId" ? otherId : null);
        using var otherClient = app.CreateClient();
        using var response = await otherClient.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("SIMULATED", await response.Content.ReadAsStringAsync());
        Assert.Equal(original, await Stored(json));
        await AssertPair(json);
    }

    [Theory]
    [InlineData("Production", "127.0.0.1", true, HttpStatusCode.NotFound)]
    [InlineData("Staging", "127.0.0.1", true, HttpStatusCode.NotFound)]
    [InlineData("Development", "192.0.2.1", true, HttpStatusCode.Forbidden)]
    [InlineData("Development", "127.0.0.1", false, HttpStatusCode.NotFound)]
    public async Task LocalDevelopmentBoundary(string environment, string address, bool enabled, HttpStatusCode expected)
    {
        using var app = fixture.CreateApplication(environment, address, enabled);
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/scans", SubmissionContractTests.Example());
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("{", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("null", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{}", "text/plain", HttpStatusCode.UnsupportedMediaType)]
    public async Task InvalidHttpBody(string json, string mediaType, HttpStatusCode expected)
    {
        using var client = fixture.Application.CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, mediaType);
        using var response = await client.PostAsync("/api/scans", content);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task CallerCannotSupplyAcceptanceTime_AndChunkedBodyIsBounded()
    {
        using var client = fixture.Application.CreateClient();
        var json = SubmissionContractTests.Example();
        json["gatewayAcceptedAtUtc"] = "2026-09-07T12:00:00Z";
        using var invalid = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Null(await Stored(json));
        using var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 16385))));
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scans") { Content = content };
        request.Headers.TransferEncodingChunked = true;
        using var oversized = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
    }

    [Fact]
    public async Task FailedOutboxInsertRollsBackObservation_ThenRetrySucceeds()
    {
        var json = SubmissionContractTests.Example();
        using var scope = fixture.Application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        // Test-only constraint injects a failure after the observation insert.
        await database.Database.ExecuteSqlRawAsync("ALTER TABLE plant.outbox ADD CONSTRAINT test_fail_outbox CHECK (false) NOT VALID");
        using var client = fixture.Application.CreateClient();
        try
        {
            using var response = await client.PostAsJsonAsync("/api/scans", json);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.NotNull(response.Headers.RetryAfter);
            Assert.Null(await Stored(json));
            Assert.False(await database.Outbox.AnyAsync(x => x.EventId == Id(json)));
        }
        finally
        {
            await database.Database.ExecuteSqlRawAsync("ALTER TABLE plant.outbox DROP CONSTRAINT test_fail_outbox");
        }
        using var retry = await client.PostAsJsonAsync("/api/scans", json);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        await AssertPair(json);
    }

    [Fact]
    public async Task DatabaseOutageAndRestartPreservePendingScansAndPermitDelayedSubmission()
    {
        var saved = SubmissionContractTests.Example();
        var delayed = SubmissionContractTests.Example();
        delayed["observedAtUtc"] = "2020-01-01T00:00:00Z";
        using var client = fixture.Application.CreateClient();
        using var first = await client.PostAsJsonAsync("/api/scans", saved);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await Stored(saved);
        await fixture.Postgres.StopAsync();
        try
        {
            using var failure = await client.PostAsJsonAsync("/api/scans", delayed);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        }
        finally
        {
            await fixture.Postgres.StartAsync();
            fixture.Application.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Plant"] = fixture.ConnectionString;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            using var retry = await client.PostAsJsonAsync("/api/scans", delayed, deadline.Token);
            if (retry.StatusCode == HttpStatusCode.Created) break;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
            await Task.Delay(250, deadline.Token);
        }
        Assert.Equal(original, await Stored(saved));
        await AssertPair(saved);
        await AssertPair(delayed);
    }

    private static Guid Id(JsonObject json) => Guid.Parse(json["eventId"]!.GetValue<string>());
    private async Task<LocalObservation?> Stored(JsonObject json)
    {
        using var scope = fixture.Application.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlantDbContext>().Observations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EventId == Id(json));
    }
    private async Task AssertPair(JsonObject json)
    {
        using var scope = fixture.Application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        Assert.Equal(1, await database.Observations.CountAsync(x => x.EventId == Id(json)));
        var entry = await database.Outbox.SingleAsync(x => x.EventId == Id(json));
        Assert.Equal("pending", entry.Status);
    }
}
