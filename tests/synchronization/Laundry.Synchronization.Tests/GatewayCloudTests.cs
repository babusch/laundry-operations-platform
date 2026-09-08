extern alias cloud;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Laundry.Edge.Persistence;
using Laundry.Edge.Synchronization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using CloudProgram = cloud::Program;
using CloudDbContext = cloud::Laundry.Api.Integrations.Scans.ScanDbContext;

namespace Laundry.Synchronization.Tests;

public sealed class GatewayCloudTests
{
    [Fact]
    [Trait("Category", "Database")]
    public async Task CloudOutageLostReceiptAndGatewayRestartConvergeWithoutDuplicateObservations()
    {
        await using var plant = new PostgreSqlBuilder("postgres:18.6-alpine3.23").Build();
        await using var cloudDb = new PostgreSqlBuilder("postgres:18.6-alpine3.23").Build();
        await Task.WhenAll(plant.StartAsync(), cloudDb.StartAsync());
        using var cloudApp = new WebApplicationFactory<CloudProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Laundry"] = cloudDb.GetConnectionString() }));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, Loopback>());
        });
        using (var scope = cloudApp.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CloudDbContext>().Database.MigrateAsync();

        var network = new NetworkState();
        WebApplicationFactory<Program> Edge(bool enabled) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Plant"] = plant.GetConnectionString(),
                ["Forwarding:Enabled"] = enabled.ToString()
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter, Loopback>();
                services.AddHttpClient<CloudDelivery>().ConfigurePrimaryHttpMessageHandler(() =>
                    new UnreliableNetwork(network) { InnerHandler = cloudApp.Server.CreateHandler() });
            });
        });
        using (var setup = Edge(false))
        using (var scope = setup.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PlantDbContext>().Database.MigrateAsync();

        var submission = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "submission.json")))!;
        var firstId = Guid.NewGuid();
        submission["eventId"] = firstId.ToString();
        using (var edge = Edge(true))
        using (var client = edge.CreateClient())
        {
            using var response = await client.PostAsJsonAsync("/api/scans", submission);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            await WaitFor(async () =>
            {
                using var scope = edge.Services.CreateScope();
                var entry = await scope.ServiceProvider.GetRequiredService<PlantDbContext>().Outbox.AsNoTracking().SingleAsync();
                return entry.Attempts >= 1 && entry.LastError == "http_503";
            });
            using var healthy = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
            // A delayed scan is still accepted while cloud delivery is failing.
            submission["eventId"] = Guid.NewGuid().ToString();
            submission["observedAtUtc"] = "2020-01-01T00:00:00Z";
            using var second = await client.PostAsJsonAsync("/api/scans", submission);
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        }

        network.Reachable = true;
        using var restarted = Edge(true);
        using var restartedClient = restarted.CreateClient();
        await WaitFor(async () =>
        {
            using var scope = restarted.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
            return await database.Outbox.CountAsync(x => x.Status == "synchronized") == 2;
        });
        Assert.Equal(1, network.LostReceipts);
        using var edgeScope = restarted.Services.CreateScope();
        using var cloudScope = cloudApp.Services.CreateScope();
        var local = edgeScope.ServiceProvider.GetRequiredService<PlantDbContext>();
        var remote = cloudScope.ServiceProvider.GetRequiredService<CloudDbContext>();
        Assert.Equal(2, await remote.Observations.CountAsync());
        foreach (var scan in await local.Observations.AsNoTracking().ToListAsync())
        {
            var stored = await remote.Observations.SingleAsync(x => x.EventId == scan.EventId);
            Assert.Equal(scan.EventJson, stored.PayloadJson);
            var delivered = await local.Outbox.SingleAsync(x => x.EventId == scan.EventId);
            Assert.Equal(stored.CloudReceivedAtUtc, delivered.CloudReceivedAtUtc);
        }
        using var retry = await restartedClient.PostAsJsonAsync("/api/scans", submission);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Contains("synchronized", await retry.Content.ReadAsStringAsync());

        // Simulate an access/configuration rejection, then explicitly replay after correction.
        network.Reject = true;
        var replayEventId = Guid.NewGuid();
        submission["eventId"] = replayEventId.ToString();
        using var rejectedSubmission = await restartedClient.PostAsJsonAsync("/api/scans", submission);
        Assert.Equal(HttpStatusCode.Created, rejectedSubmission.StatusCode);
        QueueItem? diagnostic = null;
        await WaitFor(async () =>
        {
            diagnostic = await restartedClient.GetFromJsonAsync<QueueItem>($"/api/sync/events/{replayEventId}");
            return diagnostic?.Status == "needsAttention";
        });
        var original = await local.Observations.AsNoTracking().SingleAsync(x => x.EventId == replayEventId);
        network.Reject = false;
        var replayRequest = new ReplayRequest(Guid.NewGuid(), diagnostic!.Attempts, "configurationCorrected");
        using var replay = await restartedClient.PostAsJsonAsync($"/api/sync/events/{replayEventId}/replay", replayRequest);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        await WaitFor(async () =>
            (await restartedClient.GetFromJsonAsync<QueueItem>($"/api/sync/events/{replayEventId}"))?.Status == "synchronized");
        Assert.Equal(original.EventJson, (await remote.Observations.SingleAsync(x => x.EventId == replayEventId)).PayloadJson);
        Assert.Equal(1, await remote.Observations.CountAsync(x => x.EventId == replayEventId));
        Assert.Equal(1, await local.ReplayAudits.CountAsync(x => x.EventId == replayEventId));
        using var replayRetry = await restartedClient.PostAsJsonAsync($"/api/sync/events/{replayEventId}/replay", replayRequest);
        Assert.Equal(HttpStatusCode.OK, replayRetry.StatusCode);
        Assert.Equal("synchronized", (await local.Outbox.AsNoTracking().SingleAsync(x => x.EventId == replayEventId)).Status);
    }

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        while (!await condition()) await Task.Delay(200, deadline.Token);
    }
    private sealed class NetworkState
    {
        public volatile bool Reachable;
        public volatile bool Reject;
        public int LostReceipts;
    }
    private sealed class UnreliableNetwork(NetworkState state) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!state.Reachable) return new(HttpStatusCode.ServiceUnavailable);
            if (state.Reject) return new(HttpStatusCode.Forbidden);
            var response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Created && Interlocked.CompareExchange(ref state.LostReceipts, 1, 0) == 0)
            {
                response.Dispose();
                throw new HttpRequestException("Simulated loss after the real cloud committed.");
            }
            return response;
        }
    }
    private sealed class Loopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return continuation(context);
            });
            next(app);
        };
    }
}
