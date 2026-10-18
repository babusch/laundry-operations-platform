using Laundry.Edge.Persistence;
using Laundry.Edge.Synchronization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Laundry.Edge.Orders;

public static class DevelopmentOrderDownloadCommand
{
    public static async Task<int> RunAsync(IHostEnvironment environment, IConfiguration configuration)
    {
        var runId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            // Installation-bound Development tool, not a general remote importer or production entry point.
            if (!environment.IsDevelopment()) throw new InvalidOperationException();
            var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Plant"));
            var scope = new OrderDownloadScope(configuration.GetValue<Guid>("ScanAcceptance:TenantId"),
                configuration.GetValue<Guid>("ScanAcceptance:PlantId"));
            var identity = GatewayIdentitySettings.Read(configuration);
            if (connection.Host != "localhost" || connection.Port != 15433 || connection.Database != "laundry_plant" ||
                scope.TenantId != Guid.Parse("11111111-1111-4111-8111-111111111111") ||
                scope.PlantId != Guid.Parse("22222222-2222-4222-8222-222222222222") ||
                identity.ClientId != "gateway-development" ||
                identity.TokenEndpoint.AbsoluteUri != "https://localhost:8443/realms/laundry-development/protocol/openid-connect/token")
                throw new InvalidOperationException();
            var options = new DbContextOptionsBuilder<PlantDbContext>().UseNpgsql(connection.ConnectionString).Options;
            await using (var db = new PlantDbContext(options))
            {
                if ((await db.Database.GetPendingMigrationsAsync(cancellation.Token)).Any())
                    throw new OrderDownloadException("order_download_migration_required");
            }
            using var identityClient = CreateClient();
            using var provider = new GatewayTokenProvider(identityClient, configuration, TimeProvider.System);
            using var downloadClient = CreateClient();
            var store = new OrderDownloadStore(options, scope, TimeProvider.System);
            var result = await new OrderDownloadOnce(downloadClient, provider, store, scope,
                new Uri("https://localhost:7100/api/v1/gateway/order-plans")).RunAsync(ct: cancellation.Token);
            Console.WriteLine($"Order download {runId:D}: {result.Status}; committed pages={result.Pages}, added/updated={result.Applied}, unchanged={result.Duplicates}, older ignored={result.Older}; code={result.Error ?? "none"}.");
            Console.WriteLine("Counts cover acknowledged pages in this invocation only. Local copies are retained; no scan forwarding or web server was started.");
            return result.Status == "stopped" ? 1 : result.Status == "more_available" ? 2 : 0;
        }
        catch (Exception exception)
        {
            var code = exception is OrderDownloadException known ? known.Code :
                exception is OperationCanceledException ? "cancelled" : "order_download_setup_or_storage_failed";
            Console.Error.WriteLine($"Order download {runId:D} stopped: {code}. No credentials, response body or bookmark printed. Rerun after resolving the cause; saved progress is retained.");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false
    }); // Normal certificate/hostname validation remains enabled.
}
