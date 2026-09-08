using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Laundry.Edge.Persistence;
using Laundry.Edge.Scans;
using Laundry.Edge.Synchronization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Laundry.Edge.Tests;

[Trait("Category", "Database")]
public sealed class SyncDiagnosticsTests(AcceptanceFixture fixture) : IClassFixture<AcceptanceFixture>
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _plant = Guid.NewGuid();

    [Fact]
    public async Task SummaryIsScopedAndListingIsBoundedWithoutPayloads()
    {
        var pending = await Seed("pending", age: TimeSpan.FromMinutes(10));
        await Seed("synchronized");
        await Seed("needsAttention");
        await Seed("pending", otherTenant: true);
        await Seed("pending", otherPlant: true);
        using var app = App();
        using var client = app.CreateClient();
        var summary = await client.GetFromJsonAsync<QueueSummary>("/api/sync/summary");
        Assert.NotNull(summary);
        Assert.Equal(1, summary.Pending);
        Assert.Equal(1, summary.Synchronized);
        Assert.Equal(1, summary.NeedsAttention);
        Assert.InRange(summary.OldestPendingAgeSeconds!.Value, 599, 650);
        Assert.False(summary.ForwardingEnabled);
        using var response = await client.GetAsync("/api/sync/events?status=pending&limit=1");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("SIMULATED", body);
        Assert.DoesNotContain("submissionJson", body);
        Assert.Contains(pending.EventId.ToString(), body);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var page = JsonNode.Parse(await client.GetStringAsync("/api/sync/events?limit=1"))!;
        Assert.Single(page["items"]!.AsArray());
        Assert.Equal(1, page["nextOffset"]!.GetValue<int>());
        Assert.Equal(2, JsonNode.Parse(await client.GetStringAsync("/api/sync/events?offset=1"))!["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task EmptySummaryDoesNotInventOldestAgeOrReceipt()
    {
        using var app = App();
        using var client = app.CreateClient();
        var summary = (await client.GetFromJsonAsync<QueueSummary>("/api/sync/summary"))!;
        Assert.Equal(0, summary.Pending);
        Assert.Null(summary.OldestPendingAgeSeconds);
        Assert.Null(summary.LastCloudReceiptAtUtc);
    }

    [Fact]
    public async Task ReplayIsAuditedAtomicIdempotentAndDoesNotChangeEvidence()
    {
        var scan = await Seed("needsAttention");
        using var app = App();
        using var client = app.CreateClient();
        var request = Request();
        using var first = await client.PostAsJsonAsync(Path(scan), request);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var receipt = (await first.Content.ReadFromJsonAsync<ReplayReceipt>())!;
        Assert.Equal("replayRequested", receipt.Status);
        using var retry = await client.PostAsJsonAsync(Path(scan), request);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(receipt with { Status = "replayAlreadyRequested" }, await retry.Content.ReadFromJsonAsync<ReplayReceipt>());
        using var scope = fixture.Application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        Assert.Equal(scan, await db.Observations.AsNoTracking().SingleAsync(x => x.EventId == scan.EventId));
        var entry = await db.Outbox.AsNoTracking().SingleAsync(x => x.EventId == scan.EventId);
        Assert.Equal("pending", entry.Status);
        Assert.Equal(3, entry.Attempts);
        Assert.NotNull(entry.NextAttemptAtUtc);
        var audit = await db.ReplayAudits.SingleAsync(x => x.EventId == scan.EventId);
        Assert.Equal("http_409", audit.PreviousError);
        Assert.Equal("local-development-unattributed", audit.Actor);
        Assert.Equal(request.RequestId, audit.RequestId);
        using var restarted = App();
        using var restartClient = restarted.CreateClient();
        using var replayAgain = await restartClient.PostAsJsonAsync(Path(scan), request);
        Assert.Equal(HttpStatusCode.OK, replayAgain.StatusCode);
        var history = await restartClient.GetStringAsync($"/api/sync/events/{scan.EventId}/replays");
        Assert.Contains(request.RequestId.ToString(), history);
        Assert.DoesNotContain("SIMULATED", history);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReplayRequestsDoNotDoubleQueue(bool sameRequest)
    {
        var scan = await Seed("needsAttention");
        using var app = App();
        using var client = app.CreateClient();
        var request = Request();
        var results = await Task.WhenAll(client.PostAsJsonAsync(Path(scan), request),
            client.PostAsJsonAsync(Path(scan), sameRequest ? request : Request()));
        try
        {
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.Accepted);
            Assert.Single(results, x => x.StatusCode == (sameRequest ? HttpStatusCode.OK : HttpStatusCode.Conflict));
        }
        finally { foreach (var result in results) result.Dispose(); }
        using var scope = fixture.Application.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PlantDbContext>().ReplayAudits.CountAsync(x => x.EventId == scan.EventId));
    }

    [Theory]
    [InlineData("pending", 3)]
    [InlineData("synchronized", 3)]
    [InlineData("needsAttention", 2)]
    public async Task OnlyReviewedNeedsAttentionStateCanBeReplayed(string status, int expected)
    {
        var scan = await Seed(status);
        using var app = App();
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync(Path(scan), Request() with { ExpectedAttempts = expected });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RequestIdCannotBeReusedWithChangedReasonOrDifferentEvent()
    {
        var first = await Seed("needsAttention");
        var second = await Seed("needsAttention");
        using var app = App();
        using var client = app.CreateClient();
        var request = Request();
        using var accepted = await client.PostAsJsonAsync(Path(first), request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var changed = await client.PostAsJsonAsync(Path(first), request with { ReasonCode = "configurationCorrected" });
        using var collision = await client.PostAsJsonAsync(Path(second), request);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OtherScopeIsInvisibleAndCannotBeReplayed(bool tenant)
    {
        var scan = await Seed("needsAttention", otherTenant: tenant, otherPlant: !tenant);
        using var app = App();
        using var client = app.CreateClient();
        using var detail = await client.GetAsync($"/api/sync/events/{scan.EventId}");
        using var history = await client.GetAsync($"/api/sync/events/{scan.EventId}/replays");
        using var replay = await client.PostAsJsonAsync(Path(scan), Request());
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
    }

    [Theory]
    [InlineData("Production", "127.0.0.1", true, HttpStatusCode.NotFound)]
    [InlineData("Staging", "127.0.0.1", true, HttpStatusCode.NotFound)]
    [InlineData("Development", "192.0.2.1", true, HttpStatusCode.Forbidden)]
    [InlineData("Development", "127.0.0.1", false, HttpStatusCode.NotFound)]
    public async Task DevelopmentLoopbackAndFeatureBoundary(string environment, string address, bool enabled, HttpStatusCode expected)
    {
        using var app = fixture.CreateApplication(environment, address, diagnostics: enabled);
        using var client = app.CreateClient();
        using var summary = await client.GetAsync("/api/sync/summary");
        using var replay = await client.PostAsJsonAsync($"/api/sync/events/{Guid.NewGuid()}/replay", Request());
        Assert.Equal(expected, summary.StatusCode);
        Assert.Equal(expected, replay.StatusCode);
    }

    [Theory]
    [InlineData("status=unknown")]
    [InlineData("limit=101")]
    [InlineData("offset=-1")]
    public async Task InvalidPaginationIsRejected(string query)
    {
        using var app = App();
        using var client = app.CreateClient();
        using var response = await client.GetAsync($"/api/sync/events?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AuditFailureRollsBackRequeue()
    {
        var scan = await Seed("needsAttention");
        using var scope = fixture.Application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE plant.replay_audit ADD CONSTRAINT test_fail_audit CHECK (false) NOT VALID");
        using var app = App();
        using var client = app.CreateClient();
        try
        {
            using var response = await client.PostAsJsonAsync(Path(scan), Request());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("needsAttention", (await db.Outbox.AsNoTracking().SingleAsync(x => x.EventId == scan.EventId)).Status);
            Assert.False(await db.ReplayAudits.AnyAsync(x => x.EventId == scan.EventId));
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE plant.replay_audit DROP CONSTRAINT test_fail_audit"); }
    }

    [Fact]
    public async Task InvalidReplayBodiesAndOversizedPayloadsAreRejected()
    {
        using var app = App();
        using var client = app.CreateClient();
        foreach (var raw in new[] { "{", "null", "{}", "{\"requestId\":7}",
            "{\"requestId\":\"00000000-0000-0000-0000-000000000000\",\"expectedAttempts\":3,\"reasonCode\":\"reviewedForRetry\"}" })
        {
            using var content = new StringContent(raw, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"/api/sync/events/{Guid.NewGuid()}/replay", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var oversized = new StringContent(new string(' ', 2049), Encoding.UTF8, "application/json");
        using var large = await client.PostAsync($"/api/sync/events/{Guid.NewGuid()}/replay", oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
    }

    [Fact]
    public async Task OutageDoesNotReportAnEmptyQueueOrAcceptReplayAndRecoveryPreservesAudit()
    {
        var scan = await Seed("needsAttention");
        using var app = App();
        using var client = app.CreateClient();
        var request = Request();
        await fixture.Postgres.StopAsync();
        try
        {
            using var summary = await client.GetAsync("/api/sync/summary");
            using var replay = await client.PostAsJsonAsync(Path(scan), request);
            Assert.True(summary.StatusCode == HttpStatusCode.ServiceUnavailable, await summary.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, replay.StatusCode);
            Assert.DoesNotContain("SIMULATED", await summary.Content.ReadAsStringAsync());
        }
        finally
        {
            await fixture.Postgres.StartAsync();
            fixture.Application.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Plant"] = fixture.ConnectionString;
            app.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Plant"] = fixture.ConnectionString;
        }
        using var retry = await client.PostAsJsonAsync(Path(scan), request);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Contains(request.RequestId.ToString(), await client.GetStringAsync($"/api/sync/events/{scan.EventId}/replays"));
    }

    [Fact]
    public async Task LiveLeaseCannotBeReplayed()
    {
        var scan = await Seed("needsAttention");
        using var scope = fixture.Application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plant.outbox SET lease_until_utc = {DateTimeOffset.UtcNow.AddMinutes(1)} WHERE event_id = {scan.EventId}");
        using var app = App();
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync(Path(scan), Request());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(await db.ReplayAudits.AnyAsync(x => x.EventId == scan.EventId));
    }

    [Fact]
    public async Task RequeueFailureRollsBackInsertedAudit()
    {
        var scan = await Seed("needsAttention");
        using var scope = fixture.Application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE plant.outbox ADD CONSTRAINT test_fail_requeue CHECK (status <> 'pending') NOT VALID");
        using var app = App();
        using var client = app.CreateClient();
        try
        {
            using var response = await client.PostAsJsonAsync(Path(scan), Request());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("needsAttention", (await db.Outbox.AsNoTracking().SingleAsync(x => x.EventId == scan.EventId)).Status);
            Assert.False(await db.ReplayAudits.AnyAsync(x => x.EventId == scan.EventId));
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE plant.outbox DROP CONSTRAINT test_fail_requeue"); }
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App() =>
        fixture.CreateApplication(tenant: _tenant.ToString(), plant: _plant.ToString());
    private static string Path(LocalObservation scan) => $"/api/sync/events/{scan.EventId}/replay";
    private static ReplayRequest Request() => new(Guid.NewGuid(), 3, "reviewedForRetry");
    private async Task<LocalObservation> Seed(string status, TimeSpan? age = null, bool otherTenant = false, bool otherPlant = false)
    {
        var scan = new LocalObservation
        {
            EventId = Guid.NewGuid(), TenantId = otherTenant ? Guid.NewGuid() : _tenant,
            PlantId = otherPlant ? Guid.NewGuid() : _plant, AcceptedAtUtc = DateTimeOffset.UtcNow - (age ?? TimeSpan.Zero),
            SubmissionJson = "{\"value\":\"SIMULATED-PRIVATE\"}", EventJson = "{\"value\":\"SIMULATED-PRIVATE\"}"
        };
        // Store database precision so equality checks test evidence rather than submicrosecond truncation.
        scan = scan with { AcceptedAtUtc = new DateTimeOffset(scan.AcceptedAtUtc.Ticks - scan.AcceptedAtUtc.Ticks % 10, TimeSpan.Zero) };
        using var scope = fixture.Application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        db.Observations.Add(scan);
        db.Outbox.Add(new OutboxEntry { EventId = scan.EventId, Status = status, Attempts = 3, LastError = "http_409" });
        await db.SaveChangesAsync();
        return scan;
    }
}
