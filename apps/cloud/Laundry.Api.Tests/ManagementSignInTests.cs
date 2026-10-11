using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Laundry.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Laundry.Api.Tests;

// Runs the real OIDC and cookie handlers against a synthetic signed identity-provider response.
// No passwords, private environment files, live Keycloak, or databases are used.
public sealed class ManagementSignInTests : IDisposable
{
    private const string Issuer = "https://identity.example.test/realms/management-test";
    private const string ClientId = "management-testing";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly TestClock _clock = new();
    private string _nonce = "";
    private string _failure = "";
    private string? _verifier;
    private string _challenge = "";

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    public async Task DisabledOrNonDevelopment_ManagementRoutesAndCallbacksRemainAbsent(string environment, bool enabled)
    {
        await using var app = Application(environment, enabled);
        using var client = Client(app);
        foreach (var path in new[] { "/management/login", "/management/session", ManagementAuthentication.CallbackPath })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("http://localhost:7100", "127.0.0.1")]
    [InlineData("https://localhost:7200", "127.0.0.1")]
    [InlineData("https://other.example.test:7100", "127.0.0.1")]
    [InlineData("https://localhost:7100", "192.0.2.1")]
    public async Task TransportBoundary_RejectsWrongOriginAndNonLoopbackEvenForCallbacks(string origin, string remoteAddress)
    {
        await using var app = Application(remoteAddress: remoteAddress);
        using var client = Client(app, origin);
        foreach (var path in new[] { "/management/login", ManagementAuthentication.CallbackPath })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task AnonymousSession_Returns401NotLoginHtmlAndValidGatewayBearerIsNotAManagementSession()
    {
        await using var app = Application();
        using var client = Client(app);
        using var anonymous = await client.GetAsync("/management/session");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(anonymous.Headers.Location);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("laundry-cloud-api", ""));
        using var machine = await client.GetAsync("/management/session");
        Assert.Equal(HttpStatusCode.Unauthorized, machine.StatusCode);
    }

    [Fact]
    public async Task Login_UsesCodePkceAndFixedLocalCallbackIgnoringReturnUrl()
    {
        await using var app = Application();
        using var client = Client(app);
        using var login = await client.GetAsync("/management/login?returnUrl=https://attacker.example.test");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var query = QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("form_post", query["response_mode"]);
        Assert.Equal("openid", query["scope"]);
        Assert.Equal("login", query["prompt"]);
        Assert.Equal("https://localhost:7100" + ManagementAuthentication.CallbackPath, query["redirect_uri"]);
        Assert.DoesNotContain("synthetic-client-secret", login.Headers.Location.ToString());
        Assert.All(login.Headers.GetValues("Set-Cookie"), cookie =>
        {
            Assert.Contains("secure", cookie);
            Assert.Contains("httponly", cookie);
        });
    }

    [Fact]
    public async Task RealHandlers_AcceptSignedCallbackStoreNoTokensAndIssueBoundedSecureCookie()
    {
        await using var app = Application();
        using var client = Client(app);
        using var callback = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/management/session", callback.Headers.Location!.OriginalString);
        Assert.False(string.IsNullOrWhiteSpace(_verifier));
        Assert.Equal(_challenge, Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(_verifier!))));
        var cookie = callback.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-laundry-management="));
        Assert.Contains("path=/", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.DoesNotContain("expires=", cookie);
        Assert.DoesNotContain("domain=", cookie);
        using var status = await client.GetAsync("/management/session");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("no-store", status.Headers.CacheControl!.ToString());
        var body = await status.Content.ReadAsStringAsync();
        Assert.Contains("\"authenticated\":true", body);
        Assert.Contains("\"orderAccessEnabled\":false", body);
        Assert.DoesNotContain("synthetic-person", body);
        using var attemptedScan = await client.PostAsJsonAsync("/api/scans", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, attemptedScan.StatusCode);
        var options = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(ManagementAuthentication.OidcScheme);
        Assert.False(options.SaveTokens);
        var cookies = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(ManagementAuthentication.CookieScheme);
        Assert.Equal(ManagementAuthentication.SessionLifetime, cookies.ExpireTimeSpan);
        Assert.False(cookies.SlidingExpiration);
        var encodedTicket = cookie.Split(';')[0].Split('=', 2)[1];
        var ticket = cookies.TicketDataFormat.Unprotect(Uri.UnescapeDataString(encodedTicket));
        Assert.NotNull(ticket);
        Assert.Empty(ticket.Properties.GetTokens());
        Assert.False(ticket.Properties.IsPersistent);
        Assert.DoesNotContain(ticket.Principal.Claims, claim => claim.Type == "laundry_permissions");
        Assert.Equal("interactive", ticket.Principal.FindFirstValue(ManagementAuthentication.SessionMarker));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("nonce")]
    [InlineData("subject")]
    [InlineData("state")]
    public async Task InvalidLoginResponse_IsRejectedWithoutLeakingIdentityDetails(string failure)
    {
        _failure = failure;
        await using var app = Application();
        using var client = Client(app);
        using var callback = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.Equal("{\"code\":\"management_login_failed\"}", await callback.Content.ReadAsStringAsync());
        using var status = await client.GetAsync("/management/session");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task ForgedCookie_CannotAuthenticateTheSession()
    {
        await using var app = Application();
        using var client = Client(app);
        client.DefaultRequestHeaders.Add("Cookie", "__Host-laundry-management=forged-ticket");
        using var response = await client.GetAsync("/management/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void ClientImport_RequiresConfidentialCodePkceAndNoMachineOrPasswordGrant()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Identity", "management-development-client.json")));
        var client = document.RootElement;
        Assert.Equal("management-development", client.GetProperty("clientId").GetString());
        Assert.False(client.TryGetProperty("secret", out _));
        Assert.True(client.GetProperty("standardFlowEnabled").GetBoolean());
        foreach (var property in new[] { "publicClient", "implicitFlowEnabled", "directAccessGrantsEnabled", "serviceAccountsEnabled", "fullScopeAllowed" })
        {
            Assert.False(client.GetProperty(property).GetBoolean());
        }

        Assert.Equal("S256", client.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString());
        var redirect = Assert.Single(client.GetProperty("redirectUris").EnumerateArray());
        Assert.Equal("https://localhost:7100" + ManagementAuthentication.CallbackPath, redirect.GetString());
        Assert.Empty(client.GetProperty("webOrigins").EnumerateArray());
        Assert.Empty(client.GetProperty("defaultClientScopes").EnumerateArray());
    }

    [Fact]
    public async Task UnsolicitedCallback_DoesNotCreateSession()
    {
        await using var app = Application();
        using var client = Client(app);
        using var response = await client.PostAsync(ManagementAuthentication.CallbackPath,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "unsolicited", ["state"] = "forged" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var status = await client.GetAsync("/management/session");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task ExpiredSession_DoesNotSlideOrRemainAuthenticated()
    {
        await using var app = Application();
        using var client = Client(app);
        using var login = await LoginAsync(client);
        _clock.Advance(TimeSpan.FromMinutes(16));
        using var status = await client.GetAsync("/management/session");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task Logout_RequiresCsrfAndClearsLocalSessionWithoutIdentityProviderRequest()
    {
        await using var app = Application();
        using var client = Client(app);
        using var login = await LoginAsync(client);
        using var rejected = await client.PostAsync("/management/logout", null);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        client.DefaultRequestHeaders.Add("X-Management-CSRF", "invalid");
        using var invalidCsrf = await client.PostAsync("/management/logout", null);
        Assert.Equal(HttpStatusCode.BadRequest, invalidCsrf.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Management-CSRF");
        using var stillSignedIn = await client.GetAsync("/management/session");
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
        var csrf = await client.GetFromJsonAsync<JsonElement>("/management/antiforgery");
        client.DefaultRequestHeaders.Add("X-Management-CSRF", csrf.GetProperty("requestToken").GetString());
        using var logout = await client.PostAsync("/management/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Contains("\"identityProviderSessionEnded\":false", await logout.Content.ReadAsStringAsync());
        using var signedOut = await client.GetAsync("/management/session");
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
        using var logoutGet = await client.GetAsync("/management/logout");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, logoutGet.StatusCode);
    }

    [Fact]
    public async Task ProviderUnavailable_LoginReturnsSafe503()
    {
        await using var app = Application(unavailable: true);
        using var client = Client(app);
        using var login = await client.GetAsync("/management/login");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        Assert.Equal("{\"code\":\"management_identity_unavailable\"}", await login.Content.ReadAsStringAsync());
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public void EnabledWithoutPrivateClientSecret_FailsClosed()
    {
        using var app = Application(secret: "");
        var exception = Assert.Throws<InvalidOperationException>(() => Client(app));
        Assert.Contains("private ClientSecret", exception.Message);
    }

    private WebApplicationFactory<Program> Application(string environment = "Development", bool enabled = true,
        string remoteAddress = "127.0.0.1", bool unavailable = false, string secret = "synthetic-client-secret") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ManagementAuthentication:Enabled", enabled.ToString());
            builder.UseSetting("ManagementAuthentication:Authority", Issuer);
            builder.UseSetting("ManagementAuthentication:ClientId", ClientId);
            builder.UseSetting("ManagementAuthentication:ClientSecret", secret);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new RemoteAddress(remoteAddress));
                services.PostConfigure<CookieAuthenticationOptions>(ManagementAuthentication.CookieScheme, options => options.TimeProvider = _clock);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = null;
                    options.MetadataAddress = null!;
                    options.TokenValidationParameters.ValidIssuer = Issuer;
                    options.TokenValidationParameters.ValidAudience = "laundry-cloud-api";
                    options.TokenValidationParameters.IssuerSigningKey = new RsaSecurityKey(_rsa);
                });
                services.PostConfigure<OpenIdConnectOptions>(ManagementAuthentication.OidcScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = Issuer,
                        AuthorizationEndpoint = Issuer + "/authorize",
                        TokenEndpoint = Issuer + "/token"
                    };
                    configuration.SigningKeys.Add(new RsaSecurityKey(_rsa));
                    options.ConfigurationManager = unavailable ? new UnavailableConfiguration() :
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    options.Backchannel = new HttpClient(new TokenEndpoint(this));
                });
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, string origin = "https://localhost:7100") =>
        app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(origin), AllowAutoRedirect = false });

    private async Task<HttpResponseMessage> LoginAsync(HttpClient client)
    {
        using var login = await client.GetAsync("/management/login");
        var query = QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        _nonce = query["nonce"].ToString();
        _challenge = query["code_challenge"].ToString();
        return await client.PostAsync(ManagementAuthentication.CallbackPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = "synthetic-code",
            ["state"] = _failure == "state" ? "forged" : query["state"].ToString()
        }));
    }

    private string Token(string audience, string nonce)
    {
        var claims = new List<Claim>
        {
            new("nonce", _failure == "nonce" ? "wrong" : nonce),
            new("laundry_permissions", "scans.ingest"),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        };
        if (_failure != "subject") claims.Add(new Claim("sub", "synthetic-person"));
        var now = DateTime.UtcNow;
        using var otherKey = RSA.Create(2048);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            _failure == "issuer" ? "https://wrong.example.test" : Issuer,
            _failure == "audience" ? "gateway-development" : audience,
            claims, now.AddMinutes(-10), _failure == "expired" ? now.AddMinutes(-5) : now.AddMinutes(5),
            new SigningCredentials(new RsaSecurityKey(_failure == "signature" ? otherKey : _rsa), SecurityAlgorithms.RsaSha256)));
    }

    private sealed class TokenEndpoint(ManagementSignInTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.EndsWith("/token", request.RequestUri!.AbsolutePath);
            var fields = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("authorization_code", fields["grant_type"]);
            Assert.Equal(ClientId, fields["client_id"]);
            Assert.Equal("synthetic-client-secret", fields["client_secret"]);
            owner._verifier = fields["code_verifier"].ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { access_token = "synthetic-unused-token", token_type = "Bearer", expires_in = 300, id_token = owner.Token(ClientId, owner._nonce) })
            };
        }
    }

    private sealed class RemoteAddress(string value) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, continuation) =>
            {
                http.Connection.RemoteIpAddress = IPAddress.Parse(value);
                return continuation(http);
            });
            next(app);
        };
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class UnavailableConfiguration : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken) =>
            throw new HttpRequestException("Synthetic discovery outage");
        public void RequestRefresh() { }
    }

    public void Dispose() => _rsa.Dispose();
}
