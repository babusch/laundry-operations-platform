using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Laundry.Api.Security;

public sealed record GatewayScope(Guid TenantId, Guid PlantId)
{
    public static bool TryRead(ClaimsPrincipal principal, out GatewayScope? scope)
    {
        scope = null;
        var tenant = principal.FindFirstValue(GatewayAuthorization.TenantClaim);
        var plant = principal.FindFirstValue(GatewayAuthorization.PlantClaim);
        if (!Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty ||
            !Guid.TryParse(plant, out var plantId) || plantId == Guid.Empty)
        {
            return false;
        }

        scope = new GatewayScope(tenantId, plantId);
        return true;
    }
}

public static class GatewayAuthorization
{
    public const string Policy = "GatewayScanIngest";
    public const string PermissionClaim = "laundry_permissions";
    public const string IngestPermission = "scans.ingest";
    public const string TenantClaim = "tenant_id";
    public const string PlantClaim = "plant_id";

    public static IServiceCollection AddGatewayAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetRequiredSection("GatewayAuthentication");
        var authority = section["Authority"];
        var audience = section["Audience"];
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) ||
            authorityUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(authorityUri.Query) || !string.IsNullOrEmpty(authorityUri.Fragment))
        {
            throw new InvalidOperationException(
                "GatewayAuthentication:Authority must be an absolute HTTPS URL without query or fragment.");
        }
        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException("Configure GatewayAuthentication:Audience.");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authorityUri.AbsoluteUri.TrimEnd('/');
                options.Audience = audience;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.BackchannelTimeout = TimeSpan.FromSeconds(5);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateAudience = true,
                    ValidateIssuer = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true
                };
            });
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(PermissionClaim, IngestPermission));
        return services;
    }
}
