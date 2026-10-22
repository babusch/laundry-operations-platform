using System.Data.Common;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Laundry.Edge.Security;

public static class OperatorAuthentication
{
    public const string CookieScheme = "OperatorIdentityProof";
    public const string OidcScheme = "OperatorIdentityOidc";
    public const string CookieName = "__Host-laundry-operator-proof";
    public const string Callback = "/operator/signin-oidc";
    public const string Authority = "https://localhost:8443/realms/laundry-development";
    public const string ClientId = "operator-development";
    public const string Origin = "https://localhost:7200";
    public const string Marker = "laundry_operator_identity_proof";
    public static readonly TimeSpan ProofLifetime = TimeSpan.FromMinutes(15);
    private static readonly string[] ScopeClaims = ["operator_tenant", "operator_plant", "operator_station", "operator_source"];

    public static bool Enabled(IConfiguration config, IHostEnvironment environment) => environment.IsDevelopment() &&
        config.GetValue<bool>("OperatorAuthentication:Enabled") && config.GetValue<bool>("ScanAcceptance:Enabled");

    private static string[] Binding(SourceIdentity source) => [source.TenantId.ToString("D"), source.PlantId.ToString("D"), source.StationId.ToString("D"), source.SourceId.ToString("D")];
    private static async Task<SourceIdentity?> Source(HttpContext http) =>
        await http.RequestServices.GetRequiredService<BrowserCookieSourceAuthenticator>().AuthenticateAsync(http, http.RequestAborted) is { PrincipalType: TrustedSourceKind.BrowserStation } source ? source : null;
    private static bool Matches(ClaimsPrincipal principal, SourceIdentity source) => ScopeClaims.Zip(Binding(source))
        .All(pair => principal.FindAll(pair.First).Select(claim => claim.Value).SequenceEqual([pair.Second]));
    private static string? SafeName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 200 && name == name.Trim() && !name.Any(char.IsControl) ? name : null;

    public static void AddDevelopmentOperatorIdentity(this IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        if (!Enabled(config, environment)) return;
        var secret = config["OperatorAuthentication:ClientSecret"];
        if (string.IsNullOrWhiteSpace(secret) || secret != secret.Trim())
            throw new InvalidOperationException("Configure the private operator-development client secret before enabling the operator identity proof.");
        services.AddSingleton<OperatorTicketStore>();
        services.AddAuthentication()
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = ProofLifetime;
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
                options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
                options.Events.OnValidatePrincipal = async context =>
                {
                    var source = await Source(context.HttpContext);
                    var principal = context.Principal!;
                    if (source is null || !Matches(principal, source) || !principal.HasClaim(Marker, "interactive") ||
                        principal.FindFirstValue("iss") != Authority || principal.FindAll("sub").Count() != 1)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieScheme);
                    }
                };
            })
            .AddOpenIdConnect(OidcScheme, options =>
            {
                options.Authority = Authority;
                options.ClientId = ClientId;
                options.ClientSecret = secret;
                options.SignInScheme = CookieScheme;
                options.CallbackPath = Callback;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.FormPost;
                options.UsePkce = true;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.ClaimActions.Clear();
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.UseTokenLifetime = false;
                options.BackchannelTimeout = TimeSpan.FromSeconds(5);
                options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(5);
                options.Scope.Clear(); options.Scope.Add("openid"); options.Scope.Add("profile");
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Authority,
                    ValidAudience = ClientId,
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
                    context.ProtocolMessage.RedirectUri = Origin + Callback;
                    context.ProtocolMessage.Prompt = "login";
                    return Task.CompletedTask;
                };
                options.Events.OnTokenValidated = async context =>
                {
                    var source = await Source(context.HttpContext);
                    var subjects = context.Principal!.FindAll("sub").ToArray();
                    if (source is null || subjects.Length != 1 || string.IsNullOrWhiteSpace(subjects[0].Value) || subjects[0].Value.Length > 200 || subjects[0].Value.Any(char.IsControl) ||
                        context.Properties is null || !ScopeClaims.Zip(Binding(source)).All(pair => context.Properties.Items.TryGetValue(pair.First, out var value) && value == pair.Second))
                    { context.Fail("Operator identity or source binding rejected."); return; }
                    var names = context.Principal.FindAll("preferred_username").ToArray();
                    var name = names.Length == 1 && names[0].ValueType == ClaimValueTypes.String ? SafeName(names[0].Value) : null;
                    var claims = new List<Claim> { new("iss", Authority), new("sub", subjects[0].Value), new(ClaimTypes.NameIdentifier, subjects[0].Value), new(Marker, "interactive") };
                    claims.AddRange(ScopeClaims.Zip(Binding(source)).Select(pair => new Claim(pair.First, pair.Second)));
                    if (name is not null) claims.Add(new("operator_display_name", name));
                    context.Principal = new(new ClaimsIdentity(claims, CookieScheme));
                    foreach (var key in ScopeClaims) context.Properties.Items.Remove(key);
                    var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                    context.Properties.IssuedUtc = now; context.Properties.ExpiresUtc = now.Add(ProofLifetime);
                    context.Properties.IsPersistent = false; context.Properties.AllowRefresh = false;
                };
                options.Events.OnRemoteFailure = async context =>
                {
                    context.HandleResponse(); context.Response.StatusCode = 400;
                    context.Response.Headers.CacheControl = "no-store";
                    await context.Response.WriteAsJsonAsync(new { code = "operator_login_failed" });
                };
            });
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme).Configure<OperatorTicketStore, TimeProvider>((options, store, clock) =>
        { options.SessionStore = store; options.TimeProvider = clock; });
    }

    public static void UseDevelopmentOperatorBoundary(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            if (!http.Request.Path.StartsWithSegments("/operator")) { await next(http); return; }
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Vary = "Cookie";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (!Enabled(app.Configuration, app.Environment)) { http.Response.StatusCode = 404; return; }
            // Keycloak's no-referrer policy makes a browser's form_post callback use Origin: null.
            // Only this exact form callback may carry the opaque/provider origin. It still must
            // pass the framework's correlation/state, nonce, code/PKCE and token checks, then source binding.
            // This is not a CORS allowlist or an exception for logout/ordinary cookie-backed requests.
            var formCallback = http.Request.Path.Value == Callback && HttpMethods.IsPost(http.Request.Method) &&
                string.Equals(http.Request.ContentType?.Split(';', 2)[0].Trim(), "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
            var originAllowed = !http.Request.Headers.TryGetValue("Origin", out var origin) ||
                (origin.Count == 1 && (origin[0] == Origin || (formCallback && origin[0] is "null" or "https://localhost:8443")));
            if (!http.Request.IsHttps || http.Request.Host != new HostString("localhost", 7200) ||
                http.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
                !originAllowed)
            { http.Response.StatusCode = 403; return; }
            // Do not populate a default human principal on existing source/scan routes.
            try { await next(http); }
            catch (Exception exception) when (exception is DbException or TimeoutException || exception.InnerException is DbException)
            {
                if (http.Response.HasStarted) throw;
                http.Response.StatusCode = 503;
                await http.Response.WriteAsJsonAsync(new { code = "operator_source_storage_unavailable" });
            }
        });
    }

    public static void MapDevelopmentOperatorIdentity(this WebApplication app)
    {
        if (!Enabled(app.Configuration, app.Environment)) return;
        app.MapGet("/operator/login", async (HttpContext http) =>
        {
            if (http.Request.Query.Count != 0) return Results.BadRequest(new { code = "operator_invalid_request" });
            var source = await Source(http);
            if (source is null) return Results.Unauthorized();
            var properties = new AuthenticationProperties { RedirectUri = "/operator/session", IsPersistent = false, AllowRefresh = false };
            foreach (var pair in ScopeClaims.Zip(Binding(source))) properties.Items[pair.First] = pair.Second;
            try { await http.ChallengeAsync(OidcScheme, properties); return Results.Empty; }
            catch (OpenIdConnectProtocolException) { return Results.Json(new { code = "operator_identity_rejected" }, statusCode: 502); }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
            { return Results.Json(new { code = "operator_identity_unavailable" }, statusCode: 503); }
        });
        app.MapGet("/operator/session", async (HttpContext http) =>
        {
            var result = await http.AuthenticateAsync(CookieScheme);
            return !result.Succeeded ? Results.Unauthorized() : Results.Ok(new
            {
                authenticated = true,
                displayName = result.Principal!.FindFirstValue("operator_display_name"),
                expiresAtUtc = result.Properties!.ExpiresUtc,
                operationalAccessEnabled = false
            });
        });
        app.MapGet("/operator/antiforgery", async (HttpContext http, IAntiforgery antiforgery) =>
        {
            var result = await http.AuthenticateAsync(CookieScheme);
            if (!result.Succeeded) return Results.Unauthorized();
            http.User = result.Principal!;
            return Results.Ok(new { headerName = SourceEnrollmentEndpoints.AntiforgeryHeaderName, requestToken = antiforgery.GetAndStoreTokens(http).RequestToken });
        });
        app.MapPost("/operator/logout", async (HttpContext http, IAntiforgery antiforgery) =>
        {
            var result = await http.AuthenticateAsync(CookieScheme);
            if (!result.Succeeded) return Results.Unauthorized();
            http.User = result.Principal!;
            try { await antiforgery.ValidateRequestAsync(http); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(new { code = "operator_antiforgery_required" }); }
            await http.SignOutAsync(CookieScheme);
            return Results.NoContent();
        });
    }
}
