using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Laundry.Edge.Persistence;
using Laundry.Edge.Scans;
using Laundry.Edge.Synchronization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laundry.Edge.Tests;

public sealed class CloudDeliveryTests
{
    [Theory]
    [InlineData(201, "accepted", "synchronized")]
    [InlineData(200, "alreadyProcessed", "synchronized")]
    [InlineData(200, "accepted", "pending")]
    [InlineData(201, "queued", "pending")]
    [InlineData(400, "", "needsAttention")]
    [InlineData(401, "", "needsAttention")]
    [InlineData(403, "", "needsAttention")]
    [InlineData(404, "", "needsAttention")]
    [InlineData(409, "", "needsAttention")]
    [InlineData(302, "", "needsAttention")]
    [InlineData(408, "", "pending")]
    [InlineData(429, "", "pending")]
    [InlineData(503, "", "pending")]
    public async Task ReceiptAndFailureClassification(int code, string status, string expected)
    {
        var id = Guid.NewGuid();
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(Receipt(id, code, status))));
        var result = await new CloudDelivery(client).SendAsync(new("http://127.0.0.1/api/scans"), id, "{}", default);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task MismatchedMalformedAndOversizedReceiptsNeverAcknowledge()
    {
        using var mismatched = Receipt(Guid.NewGuid(), 201, "accepted");
        var mismatchedJson = await mismatched.Content.ReadAsStringAsync();
        foreach (var content in new[] { "{", new string(' ', 4097), "null", "{\"eventId\":7}",
            mismatchedJson })
        {
            using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                { Content = new StringContent(content) })));
            var result = await new CloudDelivery(client).SendAsync(new("http://127.0.0.1/api/scans"), Guid.NewGuid(), "{}", default);
            Assert.Equal("pending", result.Status);
            Assert.Equal("invalid_receipt", result.Error);
        }
    }

    [Fact]
    public async Task RetryHintsAndConnectionLossAreRetained()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(90));
            return Task.FromResult(response);
        }));
        var result = await new CloudDelivery(client).SendAsync(new("http://127.0.0.1/api/scans"), Guid.NewGuid(), "{}", default);
        Assert.Equal(TimeSpan.FromSeconds(90), result.RetryAfter);
        Assert.True(OutboxDispatcher.RetryDelay(1, result.RetryAfter) >= TimeSpan.FromSeconds(90));
        Assert.InRange(OutboxDispatcher.RetryDelay(1000, null).TotalSeconds, 240, 300);
        using var lost = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("simulated loss")));
        Assert.Equal("pending", (await new CloudDelivery(lost).SendAsync(new("http://127.0.0.1/api/scans"), Guid.NewGuid(), "{}", default)).Status);
    }

    [Theory]
    [InlineData("http://example.com/api/scans")]
    [InlineData("http://127.0.0.1/api/scans?target=remote")]
    [InlineData("https://127.0.0.1/api/scans")]
    [InlineData("http://user:password@127.0.0.1/api/scans")]
    public void UnsafeEndpointConfigurationIsRejected(string endpoint)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Forwarding:Endpoint"] = endpoint }).Build();
        Assert.Throws<InvalidOperationException>(() => ForwardingSettings.Read(configuration));
    }

    internal static HttpResponseMessage Receipt(Guid id, int code = 201, string status = "accepted") => new((HttpStatusCode)code)
    {
        Content = JsonContent.Create(new { eventId = id, status, cloudReceivedAtUtc = DateTimeOffset.UtcNow })
    };
}

internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

[Trait("Category", "Database")]
public sealed class OutboxDispatchTests(AcceptanceFixture fixture) : IClassFixture<AcceptanceFixture>
{
    private readonly TestClock _clock = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _plant = Guid.NewGuid();
    private ForwardingSettings Settings => new(new("http://127.0.0.1/api/scans"), _tenant, _plant);

    [Fact]
    public async Task LostAcknowledgementAndRestartReplayExactPayloadAndPersistReceipt()
    {
        var scan = await Seed();
        var bodies = new List<string>();
        using var client = new HttpClient(new StubHandler(async (request, token) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (bodies.Count == 1) throw new HttpRequestException("cloud committed; receipt lost");
            return CloudDeliveryTests.Receipt(scan.EventId, 200, "alreadyProcessed");
        }));
        Assert.True(await Dispatch(client));
        var pending = await Entry(scan.EventId);
        Assert.Equal("pending", pending.Status);
        Assert.Equal(1, pending.Attempts);
        Assert.NotNull(pending.NextAttemptAtUtc);
        Assert.Null(pending.CloudReceivedAtUtc);
        Assert.False(await Dispatch(client));
        _clock.Advance(TimeSpan.FromMinutes(1));
        // Each dispatch uses a fresh context; no in-memory delivery state survives.
        Assert.True(await Dispatch(client));
        var delivered = await Entry(scan.EventId);
        Assert.Equal("synchronized", delivered.Status);
        Assert.Equal(2, delivered.Attempts);
        Assert.NotNull(delivered.CloudReceivedAtUtc);
        Assert.Null(delivered.LeaseId);
        Assert.All(bodies, body => Assert.Equal(scan.EventJson, body));
        Assert.False(await Dispatch(client));
    }

    [Fact]
    public async Task PermanentConflictDoesNotBlockFollowingEventsOrRetryForever()
    {
        var conflict = await Seed();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var good = await Seed();
        using var client = new HttpClient(new StubHandler(async (request, token) =>
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var id = Guid.Parse(json["eventId"]!.GetValue<string>());
            return id == conflict.EventId ? new(HttpStatusCode.Conflict) : CloudDeliveryTests.Receipt(id);
        }));
        Assert.True(await Dispatch(client));
        Assert.True(await Dispatch(client));
        Assert.Equal("needsAttention", (await Entry(conflict.EventId)).Status);
        Assert.Equal("http_409", (await Entry(conflict.EventId)).LastError);
        Assert.Equal("synchronized", (await Entry(good.EventId)).Status);
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.False(await Dispatch(client));
    }

    [Fact]
    public async Task LiveLeasePreventsConcurrentSend_ExpiredLeaseIsRecoveredAndFencesStaleResult()
    {
        var scan = await Seed();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slow = new HttpClient(new StubHandler(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return new(HttpStatusCode.Conflict);
        }));
        using var fast = new HttpClient(new StubHandler((_, _) => Task.FromResult(CloudDeliveryTests.Receipt(scan.EventId))));
        var first = Dispatch(slow);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(await Dispatch(fast));
            _clock.Advance(TimeSpan.FromSeconds(31));
            Assert.True(await Dispatch(fast));
        }
        finally { release.TrySetResult(); }
        await first;
        Assert.Equal("synchronized", (await Entry(scan.EventId)).Status);
        Assert.Equal(2, (await Entry(scan.EventId)).Attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatcherDoesNotSendAnotherTenantOrPlant(bool tenant)
    {
        var scan = await Seed(tenant ? Guid.NewGuid() : _tenant, tenant ? _plant : Guid.NewGuid());
        using var client = new HttpClient(new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Must not send")));
        Assert.False(await Dispatch(client));
        Assert.Equal(0, (await Entry(scan.EventId)).Attempts);
    }

    [Fact]
    public async Task FailedReceiptBookkeepingLeavesLeaseForSafeReplay()
    {
        var scan = await Seed();
        using var scope = fixture.Application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        await database.Database.ExecuteSqlRawAsync("ALTER TABLE plant.outbox ADD CONSTRAINT test_fail_delivery CHECK (status <> 'synchronized') NOT VALID");
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(CloudDeliveryTests.Receipt(scan.EventId))));
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Dispatch(client));
            Assert.Equal("pending", (await Entry(scan.EventId)).Status);
            Assert.NotNull((await Entry(scan.EventId)).LeaseId);
        }
        finally { await database.Database.ExecuteSqlRawAsync("ALTER TABLE plant.outbox DROP CONSTRAINT test_fail_delivery"); }
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(await Dispatch(client));
        Assert.Equal("synchronized", (await Entry(scan.EventId)).Status);
    }

    private async Task<LocalObservation> Seed(Guid? tenant = null, Guid? plant = null)
    {
        var json = SubmissionContractTests.Example();
        json["tenantId"] = (tenant ?? _tenant).ToString();
        json["plantId"] = (plant ?? _plant).ToString();
        var submission = json.ToJsonString();
        json["gatewayAcceptedAtUtc"] = _clock.GetUtcNow().UtcDateTime.ToString("O");
        var observation = new LocalObservation
        {
            EventId = Guid.Parse(json["eventId"]!.GetValue<string>()), TenantId = tenant ?? _tenant, PlantId = plant ?? _plant,
            AcceptedAtUtc = _clock.GetUtcNow(), SubmissionJson = submission, EventJson = json.ToJsonString()
        };
        using var scope = fixture.Application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        database.Observations.Add(observation);
        database.Outbox.Add(new OutboxEntry { EventId = observation.EventId, Status = "pending" });
        await database.SaveChangesAsync();
        return observation;
    }
    private async Task<bool> Dispatch(HttpClient client)
    {
        using var scope = fixture.Application.Services.CreateScope();
        var dispatcher = new OutboxDispatcher(scope.ServiceProvider.GetRequiredService<PlantDbContext>(),
            new CloudDelivery(client), _clock, NullLogger<OutboxDispatcher>.Instance);
        return await dispatcher.DispatchOneAsync(Settings, default);
    }
    private async Task<OutboxEntry> Entry(Guid id)
    {
        using var scope = fixture.Application.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlantDbContext>().Outbox.AsNoTracking().SingleAsync(x => x.EventId == id);
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
}
