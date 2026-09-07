using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Laundry.Edge.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Liveness_DoesNotRequireDatabaseConfigurationOrCloud()
    {
        using var application = CreateApplication(null);
        using var client = application.CreateClient();
        await AssertHealth(client, "/health", HttpStatusCode.OK, "Healthy");
    }

    [Fact]
    public async Task MissingDatabaseConfiguration_ReadinessFailsWithoutLeakingDetails()
    {
        using var application = CreateApplication(null);
        using var client = application.CreateClient();
        await AssertHealth(client, "/health/ready", HttpStatusCode.ServiceUnavailable, "Unhealthy");
    }

    [Fact]
    public async Task ScanEndpoint_IsNotImplemented()
    {
        using var application = CreateApplication(null);
        using var client = application.CreateClient();
        using var response = await client.PostAsync("/api/scans", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Database")]
    public async Task PlantDatabase_OutageAndRecovery_ChangesReadinessButNotLiveness()
    {
        await using var database = new PostgreSqlBuilder("postgres:18.6-alpine3.23").Build();
        await database.StartAsync();
        using var application = CreateApplication(database.GetConnectionString());
        using var client = application.CreateClient();

        // No cloud application or cloud database is started or configured in this test.
        await AssertHealth(client, "/health/ready", HttpStatusCode.OK, "Healthy");
        await database.StopAsync();
        await AssertHealth(client, "/health", HttpStatusCode.OK, "Healthy");
        await AssertHealth(client, "/health/ready", HttpStatusCode.ServiceUnavailable, "Unhealthy");

        await database.StartAsync();
        // Docker can assign a new random host port when a test container restarts.
        application.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Plant"] =
            WithTimeouts(database.GetConnectionString());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            using var response = await client.GetAsync("/health/ready", deadline.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(deadline.Token));
                break;
            }
            await Task.Delay(250, deadline.Token);
        }
        await AssertHealth(client, "/health", HttpStatusCode.OK, "Healthy");
    }

    private static WebApplicationFactory<Program> CreateApplication(string? connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Plant"] = connectionString is null ? null : WithTimeouts(connectionString)
                }));
        });

    private static string WithTimeouts(string connectionString) =>
        new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 2,
            CommandTimeout = 2
        }.ConnectionString;

    private static async Task AssertHealth(HttpClient client, string path, HttpStatusCode status, string body)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }
}
