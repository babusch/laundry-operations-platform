using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Laundry.Api.Security;

public static class ManagementAuthentication
{
    public const string CookieScheme = "ManagementSession";
    public const string OidcScheme = "ManagementOidc";
    public const string Policy = "ManagementHumanSession";
    public const string SessionMarker = "laundry_management_session";
    public const string CallbackPath = "/management/signin-oidc";
    private const string ExpectedOrigin = "https://localhost:7100";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);

    private static bool Enabled(IConfiguration configuration, IHostEnvironment environment) =>
        environment.IsDevelopment() && configuration.GetValue<bool>("ManagementAuthentication:Enabled");

    public static void AddDevelopmentManagementAuthentication(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        if (!Enabled(configuration, environment)) return;
        var section = configuration.GetSection("ManagementAuthentication");
        var authority = section["Authority"];
        var clientId = section["ClientId"];
        var secret = section["ClientSecret"];
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(clientId) ||
            clientId == "gateway-development" || string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("Configure a separate HTTPS management identity client and private ClientSecret before enabling management sign-in.");
        }

        var issuer = uri.AbsoluteUri.TrimEnd('/');
        // Do not change the existing default gateway bearer scheme.
        services.AddAuthentication()
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = "__Host-laundry-management";
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = SessionLifetime;
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = 401;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = 403;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(OidcScheme, options =>
            {
                options.Authority = issuer;
                options.ClientId = clientId;
                options.ClientSecret = secret;
                options.SignInScheme = CookieScheme;
                options.CallbackPath = CallbackPath;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.FormPost;
                options.UsePkce = true;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.UseTokenLifetime = false;
                options.BackchannelTimeout = TimeSpan.FromSeconds(5);
                options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(5);
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = issuer,
                    ValidAudience = clientId,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.RedirectUri = ExpectedOrigin + CallbackPath;
                    // A new local login must not silently reuse a still-active Keycloak SSO session.
                    context.ProtocolMessage.Prompt = "login";
                    return Task.CompletedTask;
                };
                options.Events.OnTokenValidated = context =>
                {
                    var subjects = context.Principal!.FindAll("sub").ToArray();
                    if (subjects.Length != 1 || string.IsNullOrWhiteSpace(subjects[0].Value))
                    {
                        context.Fail("Management identity is incomplete.");
                        return Task.CompletedTask;
                    }

                    // Rebuild with only server-owned provenance and necessary stable identity.
                    // Incoming token role/permission/scope/marker claims do not grant application access.
                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim("iss", issuer), new Claim("sub", subjects[0].Value),
                        new Claim(ClaimTypes.NameIdentifier, subjects[0].Value),
                        new Claim(SessionMarker, "interactive")
                    ], CookieScheme));
                    context.Properties ??= new AuthenticationProperties();
                    context.Properties.IsPersistent = false;
                    context.Properties.AllowRefresh = false;
                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = async context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = 400;
                    context.Response.Headers.CacheControl = "no-store";
                    await context.Response.WriteAsJsonAsync(new { code = "management_login_failed" });
                };
            });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-Management-CSRF";
            options.Cookie.Name = "__Host-laundry-management-csrf";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(CookieScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(SessionMarker, "interactive"));
    }

    public static void UseDevelopmentManagementBoundary(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            // Guard callbacks before authentication middleware as well as mapped endpoints.
            if (http.Request.Path.StartsWithSegments("/management"))
            {
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
                if (!Enabled(app.Configuration, app.Environment))
                {
                    http.Response.StatusCode = 404;
                    return;
                }

                if (!http.Request.IsHttps || http.Request.Host.Value != "localhost:7100" ||
                    http.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
                {
                    http.Response.StatusCode = 403;
                    return;
                }
            }

            await next(http);
        });
    }

    public static void MapDevelopmentManagementSession(this WebApplication app)
    {
        if (!Enabled(app.Configuration, app.Environment)) return;
        app.MapGet("/management/login", async (HttpContext http) =>
        {
            // No user-supplied return URL is accepted.
            try
            {
                await http.ChallengeAsync(OidcScheme, new AuthenticationProperties
                {
                    RedirectUri = "/management/session",
                    IsPersistent = false,
                    AllowRefresh = false
                });
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
            {
                http.Response.Clear();
                http.Response.Headers.CacheControl = "no-store";
                http.Response.StatusCode = 503;
                await http.Response.WriteAsJsonAsync(new { code = "management_identity_unavailable" });
            }
        }).AllowAnonymous();
        app.MapGet("/management/session", () => Results.Ok(new { authenticated = true, orderAccessEnabled = false }))
            .RequireAuthorization(Policy);
        app.MapGet("/management/antiforgery", (HttpContext http, IAntiforgery antiforgery) =>
            Results.Ok(new { requestToken = antiforgery.GetAndStoreTokens(http).RequestToken, headerName = "X-Management-CSRF" }))
            .RequireAuthorization(Policy);
        app.MapPost("/management/logout", async (HttpContext http, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest(new { code = "management_csrf_required" });
            }

            // Local sign-out is available without contacting Keycloak.
            await http.SignOutAsync(CookieScheme);
            return Results.Ok(new { signedOut = true, identityProviderSessionEnded = false });
        }).RequireAuthorization(Policy);
    }
}
