using Laundry.Edge.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Laundry.Edge.Orders;

public static class DevelopmentOrderAccessCommand
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Plant"));
        if (!builder.Environment.IsDevelopment() || connection.Host != "localhost" || connection.Port != 15433 || connection.Database != "laundry_plant" ||
            configuration["ScanAcceptance:TenantId"] != "11111111-1111-4111-8111-111111111111" ||
            configuration["ScanAcceptance:PlantId"] != "22222222-2222-4222-8222-222222222222" ||
            configuration.GetSection("Kestrel:Endpoints").GetChildren().Any()) throw new InvalidOperationException();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forwarding:Enabled"] = "false",
            ["LocalOrderList:Enabled"] = "true",
            ["ScanAcceptance:Enabled"] = "true",
            ["SourceEnrollment:Enabled"] = "true",
            ["SyncDiagnostics:Enabled"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning"
        });
        builder.WebHost.UseUrls("https://localhost:7200");
        builder.Logging.ClearProviders(); // The command emits only its safe summary; database audit evidence remains.
    }

    public static async Task<int> RunAsync(WebApplication app)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var started = false;
        var exitCode = 1;
        try
        {
            await using (var serviceScope = app.Services.CreateAsyncScope())
            {
                var database = serviceScope.ServiceProvider.GetRequiredService<PlantDbContext>();
                if ((await database.Database.GetPendingMigrationsAsync(deadline.Token)).Any()) throw new InvalidOperationException();
            }
            // Binding refuses an occupied port. No attempt is made to stop or adopt an existing gateway.
            await app.StartAsync(deadline.Token); started = true;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, app.Lifetime.ApplicationStopping);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = true })
            { BaseAddress = new Uri("https://localhost:7200"), Timeout = TimeSpan.FromSeconds(10) };
            await using var services = app.Services.CreateAsyncScope();
            var options = services.ServiceProvider.GetRequiredService<DbContextOptions<PlantDbContext>>();
            var scope = new OrderDownloadScope(Guid.Parse(app.Configuration["ScanAcceptance:TenantId"]!), Guid.Parse(app.Configuration["ScanAcceptance:PlantId"]!));
            var result = await new LocalOrderAccessVerification(options, scope, client).RunAsync(cancellation.Token);
            Console.WriteLine($"Local order access verification {result.RunId:D}: passed={result.Passed}, cleanupComplete={result.CleanupComplete}, ordersRead={result.OrdersRead}.");
            if (!result.CleanupComplete)
                Console.Error.WriteLine("Cleanup needs attention or enrollment creation was uncertain. Do not assume revocation. Claimed sources are traceable by the run correlation ID; an unclaimed pending enrollment has no active credential and its code expires normally.");
            exitCode = result.Passed ? 0 : 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Local order access verification could not finish. Check the local target, migration state, port and trusted HTTPS certificate. No exception details or credentials printed.");
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { if (started) await app.StopAsync(stop.Token); }
            catch (Exception) { exitCode = 1; Console.Error.WriteLine("Temporary gateway shutdown needs attention."); }
            try { await app.DisposeAsync(); }
            catch (Exception) { exitCode = 1; Console.Error.WriteLine("Temporary gateway disposal needs attention."); }
        }
        return exitCode;
    }
}
