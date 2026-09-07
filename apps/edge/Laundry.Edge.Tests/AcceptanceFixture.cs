using System.Net;
using Laundry.Edge.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Laundry.Edge.Tests;

public sealed class AcceptanceFixture : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:18.6-alpine3.23").Build();
    public string ConnectionString => new Npgsql.NpgsqlConnectionStringBuilder(Postgres.GetConnectionString())
        { Timeout = 2, CommandTimeout = 2 }.ConnectionString;
    public WebApplicationFactory<Program> Application { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Postgres.StartAsync();
        Application = CreateApplication();
        using var scope = Application.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PlantDbContext>().Database.MigrateAsync();
    }

    public WebApplicationFactory<Program> CreateApplication(string environment = "Development",
        string address = "127.0.0.1", bool enabled = true, string? tenant = null, string? plant = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Plant"] = ConnectionString,
                ["ScanAcceptance:Enabled"] = enabled.ToString(),
                ["ScanAcceptance:TenantId"] = tenant ?? "11111111-1111-4111-8111-111111111111",
                ["ScanAcceptance:PlantId"] = plant ?? "22222222-2222-4222-8222-222222222222",
                ["ScanAcceptance:StationId"] = "33333333-3333-4333-8333-333333333333",
                ["ScanAcceptance:DeviceId"] = "44444444-4444-4444-8444-444444444444"
            }));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddress(address)));
        });

    public async Task DisposeAsync()
    {
        await Application.DisposeAsync();
        await Postgres.DisposeAsync();
    }

    private sealed class RemoteAddress(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(address);
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
