using System.Data.Common;
using Laundry.Api.Orders;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Customers;

public static class DevelopmentCustomerEndpoints
{
    private static bool Enabled(IConfiguration configuration, IHostEnvironment environment) =>
        ManagementSessionAccess.Enabled(configuration, environment) && configuration.GetValue<bool>("Customers:Enabled");

    public static void AddDevelopmentCustomers(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        if (!Enabled(configuration, environment)) return;
        services.AddDbContext<CustomerDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString("Laundry") ?? throw new InvalidOperationException("Configure the cloud database."),
            provider => provider.MigrationsHistoryTable("__customer_migrations", "customers")));
        services.AddScoped<CustomerStore>();
        services.AddScoped<ICustomerPlantEligibility>(provider => provider.GetRequiredService<CustomerStore>());
    }

    public static void MapDevelopmentCustomers(this WebApplication app)
    {
        if (!Enabled(app.Configuration, app.Environment)) return;
        app.MapGet("/management/tenants/{tenantId:guid}/plants/{plantId:guid}/customers",
            async (Guid tenantId, Guid plantId, HttpContext http, ManagementAccessStore access, CustomerStore customers, CancellationToken ct) =>
            {
                var session = await http.AuthenticateAsync(ManagementAuthentication.CookieScheme);
                if (!ManagementSessionAccess.TryReadActor(session, app.Configuration["ManagementAuthentication:Authority"]!.TrimEnd('/'), out var actor))
                    return Results.Unauthorized();
                try
                {
                    // Read permission is for choosing a customer on an order, not administering customers.
                    if (!await access.IsAllowedAsync(actor!, tenantId, plantId, OrderPlanningPermissions.Read, ct))
                        return Results.StatusCode(403);
                    var choices = await customers.ReadChoicesAsync(tenantId, plantId, ct);
                    return Results.Ok(new
                    {
                        customers = choices.Take(100),
                        truncated = choices.Length > 100,
                        orderAccessEnabled = DevelopmentOrderEndpoints.Enabled(app.Configuration, app.Environment)
                    });
                }
                catch (Exception exception) when (exception is DbException or TimeoutException ||
                    exception is InvalidOperationException { InnerException: DbException })
                {
                    return Results.Json(new { code = "customer_lookup_unavailable", correlationId = http.TraceIdentifier }, statusCode: 503);
                }
            }).RequireAuthorization(ManagementAuthentication.Policy);
    }
}
