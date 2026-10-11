using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Laundry.Api.Orders;

namespace Laundry.Api.Security.ManagementAccess;

public static class ManagementSessionAccess
{
    public static bool TryReadActor(AuthenticateResult result, string expectedIssuer, out ManagementActor? actor)
    {
        actor = null;
        if (!result.Succeeded || result.Ticket?.AuthenticationScheme != ManagementAuthentication.CookieScheme ||
            result.Principal?.Identities.Count() != 1) return false;
        var identity = result.Principal.Identities.Single();
        if (!identity.IsAuthenticated || identity.AuthenticationType != ManagementAuthentication.CookieScheme) return false;
        var issuers = identity.FindAll("iss").ToArray();
        var subjects = identity.FindAll("sub").ToArray();
        var markers = identity.FindAll(ManagementAuthentication.SessionMarker).ToArray();
        if (issuers.Length != 1 || issuers[0].Value != expectedIssuer || subjects.Length != 1 ||
            string.IsNullOrWhiteSpace(subjects[0].Value) || markers.Length != 1 || markers[0].Value != "interactive") return false;
        actor = new ManagementActor(issuers[0].Value, subjects[0].Value, ManagementActorKind.Human, true);
        return true;
    }

    public static bool Enabled(IConfiguration configuration, IHostEnvironment environment) => environment.IsDevelopment() &&
        configuration.GetValue<bool>("ManagementAuthentication:Enabled") && configuration.GetValue<bool>("ManagementAccess:Enabled");

    public static void AddDevelopmentManagementAccess(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        if (!Enabled(configuration, environment)) return;
        services.AddDbContext<ManagementAccessDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString("Laundry") ?? throw new InvalidOperationException("Configure the cloud database connection."),
            provider => provider.MigrationsHistoryTable("__management_access_migrations", "identity_access")));
        services.AddScoped<ManagementAccessStore>();
    }

    public static void MapDevelopmentManagementAccess(this WebApplication app)
    {
        if (!Enabled(app.Configuration, app.Environment)) return;
        app.MapGet("/management/access", async (HttpContext http, ManagementAccessStore store, CancellationToken ct) =>
        {
            var session = await http.AuthenticateAsync(ManagementAuthentication.CookieScheme);
            var issuer = app.Configuration["ManagementAuthentication:Authority"]!.TrimEnd('/');
            if (!TryReadActor(session, issuer, out var actor)) return Results.Unauthorized();
            try
            {
                var assignments = await store.ReadAssignmentsAsync(actor!, ct);
                return Results.Ok(new
                {
                    assignments,
                    orderAccessEnabled = DevelopmentOrderEndpoints.Enabled(app.Configuration, app.Environment)
                });
            }
            catch (Exception exception) when (exception is DbException or TimeoutException ||
                exception is InvalidOperationException { InnerException: DbException })
            {
                return Results.Json(new { code = "management_access_unavailable", correlationId = http.TraceIdentifier }, statusCode: 503);
            }
        }).RequireAuthorization(ManagementAuthentication.Policy);
    }
}
