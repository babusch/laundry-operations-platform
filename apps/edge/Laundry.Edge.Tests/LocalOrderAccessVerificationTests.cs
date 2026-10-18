using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Laundry.Edge.Orders;
using Laundry.Edge.Persistence;
using Laundry.Edge.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Laundry.Edge.Tests;

[Trait("Category", "Database")]
public sealed class LocalOrderAccessVerificationTests(AcceptanceFixture fixture) : IClassFixture<AcceptanceFixture>
{
    private readonly OrderDownloadScope _scope = new(Guid.NewGuid(), Guid.NewGuid());
    private DbContextOptions<PlantDbContext> Options => new DbContextOptionsBuilder<PlantDbContext>().UseNpgsql(fixture.ConnectionString).Options;
    private WebApplicationFactory<Program> App() => fixture.CreateApplication(tenant: _scope.TenantId.ToString(), plant: _scope.PlantId.ToString())
        .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        { ["LocalOrderList:Enabled"] = "true" })));

    private async Task SeedOrders()
    {
        var item = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "order-plan.v1.visible.json")))!.AsObject();
        item["tenantId"] = _scope.TenantId; item["plantId"] = _scope.PlantId; item["orderId"] = Guid.NewGuid();
        var json = new JsonObject { ["items"] = new JsonArray(item), ["nextCursor"] = "synthetic-download", ["hasMore"] = false };
        await new OrderDownloadStore(Options, _scope, TimeProvider.System).ApplyAsync(Guid.NewGuid(), 0, null, json.ToJsonString());
    }

    [Fact]
    public async Task Verification_ProvesAccessThenRevokesOnlyItsOwnSourceWithAuditAndNoBusinessWrites()
    {
        await SeedOrders(); using var app = App();
        using var untouched = app.CreateClient(new() { BaseAddress = new("https://localhost:7200") });
        var enrollment = await (await untouched.PostAsync("/api/development/source-enrollments", null)).Content.ReadFromJsonAsync<JsonObject>();
        var existingId = enrollment!["sourceId"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.NoContent, (await untouched.PostAsJsonAsync("/api/source-enrollment/exchange", new { enrollmentCode = enrollment["enrollmentCode"]!.GetValue<string>() })).StatusCode);
        await using var db = new PlantDbContext(Options);
        var existingBefore = JsonSerializer.Serialize(await db.TrustedSources.AsNoTracking().SingleAsync(x => x.SourceId == existingId));
        var copies = JsonSerializer.Serialize(await db.LocalOrderPlans.AsNoTracking().Where(x => x.TenantId == _scope.TenantId).ToArrayAsync());
        var progress = JsonSerializer.Serialize(await db.OrderDownloadProgress.AsNoTracking().Where(x => x.TenantId == _scope.TenantId).ToArrayAsync());
        var receiptCount = await db.OrderDownloadReceipts.CountAsync();
        var observationCount = await db.Observations.CountAsync(); var outboxCount = await db.Outbox.CountAsync();
        using var client = app.CreateClient(new() { BaseAddress = new("https://localhost:7200") });
        var result = await new LocalOrderAccessVerification(Options, _scope, client).RunAsync();
        Assert.True(result.Passed); Assert.True(result.CleanupComplete); Assert.Equal(1, result.OrdersRead);
        var audit = await db.SourceSecurityAudits.AsNoTracking().Where(x => x.CorrelationId == result.RunId).ToArrayAsync();
        Assert.Contains(audit, x => x.Action == SourceSecurityAction.VerificationStarted);
        Assert.Contains(audit, x => x.Action == SourceSecurityAction.PermissionGranted);
        Assert.Contains(audit, x => x.Action == SourceSecurityAction.PermissionRevoked);
        Assert.Contains(audit, x => x.Action == SourceSecurityAction.CredentialRevoked);
        Assert.Contains(audit, x => x.Action == SourceSecurityAction.SourceRevoked);
        Assert.All(audit, x => { Assert.Equal("development-system", x.ActorType); Assert.EndsWith(result.RunId.ToString("D"), x.ActorId); });
        var ownId = audit.Select(x => x.SourceId).Distinct().Single()!.Value;
        Assert.NotEqual(existingId, ownId);
        Assert.Equal(TrustedSourceStatus.Revoked, (await db.TrustedSources.AsNoTracking().SingleAsync(x => x.SourceId == ownId)).Status);
        Assert.False(await db.SourcePermissions.AnyAsync(x => x.SourceId == ownId && x.Permission == SourcePermissions.ReadOrders));
        Assert.False(await db.SourceCredentials.AnyAsync(x => x.SourceId == ownId && x.Status == SourceCredentialStatus.Active));
        Assert.Equal(existingBefore, JsonSerializer.Serialize(await db.TrustedSources.AsNoTracking().SingleAsync(x => x.SourceId == existingId)));
        Assert.Equal(HttpStatusCode.Forbidden, (await untouched.GetAsync("/api/order-plans")).StatusCode);
        Assert.Equal(copies, JsonSerializer.Serialize(await db.LocalOrderPlans.AsNoTracking().Where(x => x.TenantId == _scope.TenantId).ToArrayAsync()));
        Assert.Equal(progress, JsonSerializer.Serialize(await db.OrderDownloadProgress.AsNoTracking().Where(x => x.TenantId == _scope.TenantId).ToArrayAsync()));
        Assert.Equal(receiptCount, await db.OrderDownloadReceipts.CountAsync());
        Assert.Equal(observationCount, await db.Observations.CountAsync()); Assert.Equal(outboxCount, await db.Outbox.CountAsync());
    }

    [Theory]
    [InlineData("before-enroll")]
    [InlineData("lost-enrollment-ack")]
    [InlineData("exchange")]
    [InlineData("lost-exchange-ack")]
    [InlineData("after-grant")]
    [InlineData("after-revoke")]
    [InlineData("cancel-after-grant")]
    public async Task Verification_FailureAndCancellationCleanOnlyClaimedTemporarySource(string mode)
    {
        using var app = App(); using var cancelled = new CancellationTokenSource();
        using var client = app.CreateDefaultClient(new Uri("https://localhost:7200"), new CookieContainerHandler(new CookieContainer()), new Fault(mode, cancelled));
        var result = await new LocalOrderAccessVerification(Options, _scope, client).RunAsync(cancelled.Token);
        Assert.False(result.Passed); Assert.Equal(mode is not ("before-enroll" or "lost-enrollment-ack"), result.CleanupComplete);
        await using var db = new PlantDbContext(Options);
        var sourceIds = await db.SourceSecurityAudits.Where(x => x.CorrelationId == result.RunId).Select(x => x.SourceId).Distinct().ToArrayAsync();
        foreach (var id in sourceIds)
        {
            Assert.Equal(TrustedSourceStatus.Revoked, (await db.TrustedSources.AsNoTracking().SingleAsync(x => x.SourceId == id)).Status);
            Assert.False(await db.SourceCredentials.AnyAsync(x => x.SourceId == id && x.Status == SourceCredentialStatus.Active));
            Assert.False(await db.SourcePermissions.AnyAsync(x => x.SourceId == id && x.Permission == SourcePermissions.ReadOrders));
        }
    }

    [Fact]
    public async Task Verification_CleanupDatabaseFailureIsExplicitNotSuccessfulCleanup()
    {
        using var app = App(); using var cancelled = new CancellationTokenSource();
        var fail = new CleanupFailure();
        using var client = app.CreateDefaultClient(new Uri("https://localhost:7200"), new CookieContainerHandler(new CookieContainer()), new Fault("after-grant", cancelled, () => fail.Enabled = true));
        var options = new DbContextOptionsBuilder<PlantDbContext>(Options).AddInterceptors(fail).Options;
        var result = await new LocalOrderAccessVerification(options, _scope, client).RunAsync();
        Assert.False(result.Passed); Assert.False(result.CleanupComplete);
        // This intentionally stranded synthetic source belongs only to a disposable test database.
        await using var db = new PlantDbContext(Options);
        Assert.True(await db.SourceSecurityAudits.AnyAsync(x => x.CorrelationId == result.RunId && x.Action == SourceSecurityAction.PermissionGranted));
    }

    [Fact]
    public async Task Verification_LostGrantCommitAcknowledgementStillRevokesGrantAndCredential()
    {
        using var app = App(); using var client = app.CreateClient(new() { BaseAddress = new("https://localhost:7200") });
        var options = new DbContextOptionsBuilder<PlantDbContext>(Options).AddInterceptors(new LoseGrantCommit()).Options;
        var result = await new LocalOrderAccessVerification(options, _scope, client).RunAsync();
        Assert.False(result.Passed); Assert.True(result.CleanupComplete);
        await using var db = new PlantDbContext(Options);
        var audit = await db.SourceSecurityAudits.Where(x => x.CorrelationId == result.RunId).ToArrayAsync();
        Assert.Contains(audit, x => x.Action == SourceSecurityAction.PermissionGranted);
        Assert.Contains(audit, x => x.Action == SourceSecurityAction.PermissionRevoked);
        var ownId = audit.Select(x => x.SourceId).Distinct().Single();
        Assert.False(await db.SourcePermissions.AnyAsync(x => x.SourceId == ownId && x.Permission == SourcePermissions.ReadOrders));
        Assert.False(await db.SourceCredentials.AnyAsync(x => x.SourceId == ownId && x.Status == SourceCredentialStatus.Active));
    }

    [Fact]
    public async Task Verification_RepeatInvocationUsesANewStationAndRetainsPriorAuditEvidence()
    {
        using var app = App();
        using var firstClient = app.CreateClient(new() { BaseAddress = new("https://localhost:7200") });
        var first = await new LocalOrderAccessVerification(Options, _scope, firstClient).RunAsync();
        Assert.True(first.Passed);
        await using var db = new PlantDbContext(Options);
        var originalAudit = JsonSerializer.Serialize(await db.SourceSecurityAudits.AsNoTracking().Where(x => x.CorrelationId == first.RunId).OrderBy(x => x.AuditId).ToArrayAsync());
        using var secondClient = app.CreateClient(new() { BaseAddress = new("https://localhost:7200") });
        var second = await new LocalOrderAccessVerification(Options, _scope, secondClient).RunAsync();
        Assert.True(second.Passed); Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(originalAudit, JsonSerializer.Serialize(await db.SourceSecurityAudits.AsNoTracking().Where(x => x.CorrelationId == first.RunId).OrderBy(x => x.AuditId).ToArrayAsync()));
        Assert.Equal(2, await db.TrustedSources.CountAsync(x => x.TenantId == _scope.TenantId && x.Status == TrustedSourceStatus.Revoked));
    }

    [Fact]
    public async Task Verification_CleanupWaitsForUncertainInFlightExchangeBeforeRevokingSource()
    {
        var race = new ExchangeRace();
        using var app = App().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<PlantDbContext>(options => options.AddInterceptors(race))));
        var fault = new LostInFlightExchange(race);
        using var client = app.CreateDefaultClient(new Uri("https://localhost:7200"), new CookieContainerHandler(new CookieContainer()), fault);
        var options = new DbContextOptionsBuilder<PlantDbContext>(Options).AddInterceptors(race).Options;
        var run = new LocalOrderAccessVerification(options, _scope, client).RunAsync();
        try
        {
            await race.CleanupLock.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted); // Exchange still holds its enrollment lock.
        }
        finally { race.Release.TrySetResult(); }
        var result = await run;
        Assert.False(result.Passed); Assert.True(result.CleanupComplete);
        if (fault.InFlight is not null) (await fault.InFlight).Dispose();
        await using var db = new PlantDbContext(Options);
        var ownId = await db.SourceSecurityAudits.Where(x => x.CorrelationId == result.RunId).Select(x => x.SourceId).FirstAsync();
        Assert.Equal(TrustedSourceStatus.Revoked, (await db.TrustedSources.SingleAsync(x => x.SourceId == ownId)).Status);
        Assert.False(await db.SourceCredentials.AnyAsync(x => x.SourceId == ownId && x.Status == SourceCredentialStatus.Active));
    }

    private sealed class LoseGrantCommit : DbTransactionInterceptor
    {
        private int _commits;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (++_commits == 2) throw new TimeoutException("synthetic lost grant acknowledgement");
            return Task.CompletedTask;
        }
    }
    private sealed class ExchangeRace : DbCommandInterceptor
    {
        public TaskCompletionSource Consumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupLock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE plant.source_enrollment_codes", StringComparison.Ordinal))
            {
                Consumed.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT enrollment_code_id", StringComparison.Ordinal)) CleanupLock.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
    private sealed class LostInFlightExchange(ExchangeRace race) : DelegatingHandler
    {
        public Task<HttpResponseMessage>? InFlight;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/source-enrollment/exchange")
            {
                InFlight = base.SendAsync(request, CancellationToken.None);
                await race.Consumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                throw new HttpRequestException("synthetic uncertain exchange");
            }
            return await base.SendAsync(request, ct);
        }
    }

    private sealed class Fault(string mode, CancellationTokenSource cancelled, Action? beforeFailure = null) : DelegatingHandler
    {
        private int _reads;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/development/source-enrollments" && mode == "before-enroll") return new(HttpStatusCode.ServiceUnavailable);
            if (path == "/api/development/source-enrollments" && mode == "lost-enrollment-ack")
            {
                (await base.SendAsync(request, ct)).Dispose();
                throw new HttpRequestException("synthetic lost enrollment creation acknowledgement");
            }
            if (path == "/api/source-enrollment/exchange")
            {
                if (mode == "exchange") return new(HttpStatusCode.ServiceUnavailable);
                if (mode == "lost-exchange-ack")
                {
                    (await base.SendAsync(request, ct)).Dispose();
                    throw new HttpRequestException("synthetic lost exchange acknowledgement");
                }
            }
            if (path == "/api/order-plans")
            {
                _reads++;
                if ((_reads == 3 && mode is "after-grant" or "cancel-after-grant") || (_reads == 4 && mode == "after-revoke"))
                {
                    beforeFailure?.Invoke();
                    if (mode == "cancel-after-grant") { cancelled.Cancel(); throw new OperationCanceledException(ct); }
                    return new(HttpStatusCode.ServiceUnavailable);
                }
            }
            return await base.SendAsync(request, ct);
        }
    }
    private sealed class CleanupFailure : DbCommandInterceptor
    {
        public bool Enabled;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Enabled ? throw new TimeoutException("synthetic cleanup database outage") : base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}
