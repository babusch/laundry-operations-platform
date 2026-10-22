using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Laundry.Edge.Persistence;
using Laundry.Edge.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Laundry.Edge.Tests;

// Real OIDC/cookie handlers, disposable plant DB and a synthetic signed provider; no live credentials.
[Trait("Category", "Database")]
public sealed class OperatorIdentityTests(AcceptanceFixture fixture) : IClassFixture<AcceptanceFixture>, IDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    private readonly Clock clock = new();
    private string nonce = "", challenge = "", parState = "", failure = "";
    private string? name = "synthetic-operator";
    private bool unavailable;
    private int providerCalls;
    private WebApplicationFactory<Program> App(string environment = "Development", bool enabled = true, string address = "127.0.0.1", bool par = false, string secret = "synthetic-client-secret", bool scans = true) =>
        fixture.CreateApplication(environment, address, enabled: scans).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OperatorAuthentication:Enabled", enabled.ToString());
            builder.UseSetting("OperatorAuthentication:ClientSecret", secret);
            builder.UseSetting("ScanAcceptance:Enabled", scans.ToString());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OperatorAuthentication:Enabled"] = enabled.ToString(),
                ["OperatorAuthentication:ClientSecret"] = secret,
                ["AllowedHosts"] = "*"
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.PostConfigure<OpenIdConnectOptions>(OperatorAuthentication.OidcScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = OperatorAuthentication.Authority,
                        AuthorizationEndpoint = OperatorAuthentication.Authority + "/authorize",
                        TokenEndpoint = OperatorAuthentication.Authority + "/token",
                        PushedAuthorizationRequestEndpoint = par ? OperatorAuthentication.Authority + "/par" : null
                    };
                    configuration.SigningKeys.Add(new RsaSecurityKey(rsa));
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    options.Backchannel = new HttpClient(new Provider(this));
                });
            });
        });
    private static HttpClient Client(WebApplicationFactory<Program> app, string origin = OperatorAuthentication.Origin) =>
        app.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
    private static async Task<Guid> Enroll(HttpClient client)
    {
        using var created = await client.PostAsync("/api/development/source-enrollments", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var code = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        using var exchange = await client.PostAsJsonAsync("/api/source-enrollment/exchange", new { enrollmentCode = code["enrollmentCode"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.NoContent, exchange.StatusCode);
        return code["sourceId"]!.GetValue<Guid>();
    }
    private async Task<string> Begin(HttpClient client)
    {
        using var login = await client.GetAsync("/operator/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var query = QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        if (query.ContainsKey("request_uri")) return parState;
        Assert.Equal("code", query["response_type"]); Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("form_post", query["response_mode"]); Assert.Equal("login", query["prompt"]);
        Assert.Equal(OperatorAuthentication.Origin + OperatorAuthentication.Callback, query["redirect_uri"]);
        Assert.Equal("openid profile", query["scope"]);
        nonce = query["nonce"].ToString(); challenge = query["code_challenge"].ToString();
        return query["state"].ToString();
    }
    private async Task<HttpResponseMessage> Complete(HttpClient client, string state, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OperatorAuthentication.Callback)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "synthetic-code", ["state"] = failure == "state" ? "forged" : state })
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }
    private async Task<HttpResponseMessage> Login(HttpClient client) => await Complete(client, await Begin(client));

    [Theory]
    [InlineData("null")]
    [InlineData("https://localhost:8443")]
    public async Task ValidFormCallbackSupportsProviderAndOpaqueBrowserOrigins(string origin)
    {
        using var app = App(); using var client = Client(app); await Enroll(client);
        using var response = await Complete(client, await Begin(client), origin);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/operator/session", response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/operator/session")).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/operator/signin-oidc", "null", "application/x-www-form-urlencoded")]
    [InlineData("POST", "/operator/signin-oidc", "null", "application/json")]
    [InlineData("POST", "/operator/signin-oidc", "https://untrusted.test", "application/x-www-form-urlencoded")]
    [InlineData("GET", "/operator/session", "null", "application/x-www-form-urlencoded")]
    [InlineData("POST", "/operator/logout", "null", "application/x-www-form-urlencoded")]
    [InlineData("POST", "/operator/logout", "https://localhost:8443", "application/x-www-form-urlencoded")]
    public async Task CallbackOriginExceptionDoesNotApplyToOtherRequests(string method, string path, string origin, string contentType)
    {
        using var app = App(); using var client = Client(app);
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent("{}") };
        request.Content.Headers.ContentType = new(contentType);
        request.Headers.Add("Origin", origin);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnsolicitedOpaqueOriginCallbackStillNeedsProtocolProof()
    {
        using var app = App(); using var client = Client(app);
        using var response = await Complete(client, "forged", "null");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"code\":\"operator_login_failed\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/operator/session")).StatusCode);
    }

    [Theory]
    [InlineData("Development", false, true)]
    [InlineData("Production", true, true)]
    [InlineData("Staging", true, true)]
    [InlineData("Development", true, false)]
    public async Task FeatureIsAbsentWithoutEveryGate(string environment, bool enabled, bool scans)
    {
        using var app = App(environment, enabled, scans: scans); using var client = Client(app);
        foreach (var path in new[] { "/operator/login", "/operator/session", "/operator/antiforgery", OperatorAuthentication.Callback })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/operator/logout", null)).StatusCode);
    }
    [Theory]
    [InlineData("http://localhost:7200", "127.0.0.1")]
    [InlineData("https://localhost:7100", "127.0.0.1")]
    [InlineData("https://elsewhere.test:7200", "127.0.0.1")]
    [InlineData(OperatorAuthentication.Origin, "192.0.2.4")]
    public async Task BoundaryProtectsEndpointsAndCallback(string origin, string address)
    {
        using var app = App(address: address); using var client = Client(app, origin);
        foreach (var path in new[] { "/operator/login", "/operator/session", OperatorAuthentication.Callback })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
    }
    [Fact]
    public async Task AnonymousForgedManagementAndBearerCredentialsAreNotAnOperatorProof()
    {
        using var app = App(); using var client = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/operator/login")).StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", "__Host-laundry-management=forged; __Host-laundry-operator-proof=forged");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-human-session");
        using var response = await client.GetAsync("/operator/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Null(response.Headers.Location);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedCallbackProvesIdentityWithoutGrantingOperationsAndLogoutRevokesTicket(bool par)
    {
        using var app = App(par: par); using var client = Client(app); await Enroll(client);
        using var callback = await Login(client);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode); Assert.Equal("/operator/session", callback.Headers.Location!.OriginalString);
        var cookie = callback.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(OperatorAuthentication.CookieName + "="));
        foreach (var flag in new[] { "path=/", "secure", "httponly", "samesite=lax" }) Assert.Contains(flag, cookie);
        Assert.DoesNotContain("expires=", cookie); Assert.DoesNotContain("domain=", cookie);
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(OperatorAuthentication.CookieScheme);
        var envelope = options.TicketDataFormat.Unprotect(Uri.UnescapeDataString(cookie.Split(';')[0].Split('=', 2)[1]));
        Assert.NotNull(envelope);
        var stored = await app.Services.GetRequiredService<OperatorTicketStore>().RetrieveAsync(Assert.Single(envelope.Principal.Claims).Value);
        Assert.NotNull(stored); Assert.Empty(stored.Properties.GetTokens());
        Assert.DoesNotContain(stored.Principal.Claims, claim => claim.Type is "laundry_permissions" or "role" or "email" or "access_token");
        Assert.True(stored.Principal.HasClaim(OperatorAuthentication.Marker, "interactive"));
        using var session = await client.GetAsync("/operator/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode); Assert.Contains("Cookie", session.Headers.Vary);
        var json = (await session.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(new[] { "authenticated", "displayName", "expiresAtUtc", "operationalAccessEnabled" }, json.Select(pair => pair.Key).Order().ToArray());
        Assert.Equal("synthetic-operator", json["displayName"]!.GetValue<string>()); Assert.False(json["operationalAccessEnabled"]!.GetValue<bool>());
        Assert.Equal(clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds(), DateTimeOffset.Parse(json["expiresAtUtc"]!.GetValue<string>()).ToUnixTimeSeconds());
        var calls = providerCalls; unavailable = true; // Local status and logout must not require the provider.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/operator/session")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/operator/logout", null)).StatusCode);
        var csrf = (await client.GetFromJsonAsync<JsonObject>("/operator/antiforgery"))!;
        client.DefaultRequestHeaders.Add(csrf["headerName"]!.GetValue<string>(), csrf["requestToken"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/operator/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/operator/session")).StatusCode);
        // Even a captured cookie cannot recover the removed server ticket.
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/operator/session")).StatusCode);
        Assert.Equal(calls, providerCalls);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/source-session")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/operator/logout")).StatusCode);
    }
    [Theory]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("nonce")]
    [InlineData("subject")]
    [InlineData("state")]
    public async Task InvalidCallbackDoesNotCreateAProof(string problem)
    {
        failure = problem; using var app = App(); using var client = Client(app); await Enroll(client);
        using var response = await Complete(client, await Begin(client), "null"); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"code\":\"operator_login_failed\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/operator/session")).StatusCode);
    }
    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("bad\nname")]
    [InlineData("long")]
    public async Task MissingOrUnsafeDisplayNameIsNotInvented(string? displayName)
    {
        name = displayName == "long" ? new string('x', 201) : displayName; using var app = App(); using var client = Client(app); await Enroll(client);
        using var callback = await Login(client); Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var json = (await client.GetFromJsonAsync<JsonObject>("/operator/session"))!; Assert.Null(json["displayName"]);
    }
    [Fact]
    public async Task SourceRevocationInvalidatesProofAndChangedSourceRejectsCallback()
    {
        using var app = App(); using var client = Client(app); var firstSource = await Enroll(client);
        var state = await Begin(client); await Enroll(client); // Replace the source during pending login.
        using var mismatch = await Complete(client, state); Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        using var callback = await Login(client); Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var sourceSession = (await client.GetFromJsonAsync<JsonObject>("/api/source-session"))!;
        using var scope = app.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        var sourceId = sourceSession["sourceId"]!.GetValue<Guid>(); Assert.NotEqual(firstSource, sourceId);
        await database.TrustedSources.Where(source => source.SourceId == sourceId).ExecuteUpdateAsync(setters => setters.SetProperty(source => source.Status, TrustedSourceStatus.Revoked));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/operator/session")).StatusCode);
    }
    [Fact]
    public async Task ExpiryIsFixedAndIndependentOfProviderOrStatusReads()
    {
        using var app = App(); using var client = Client(app); await Enroll(client); using var callback = await Login(client);
        clock.Now = clock.Now.AddMinutes(14); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/operator/session")).StatusCode);
        clock.Now = clock.Now.AddMinutes(1); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/operator/session")).StatusCode);
    }
    [Fact]
    public async Task CallbackReplayBadOriginMissingCsrfAndUserReturnUrlsAreRejected()
    {
        using var app = App(); using var client = Client(app); await Enroll(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/operator/login?returnUrl=https://attacker.test")).StatusCode);
        var state = await Begin(client); using var callback = await Complete(client, state); Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        using var replay = await Complete(client, state); Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://attacker.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/operator/logout", null)).StatusCode);
    }
    [Fact]
    public async Task ParOutageOrClientRejectionCannotBypassAuthentication()
    {
        using var app = App(par: true); using var client = Client(app); await Enroll(client); unavailable = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/operator/login")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/operator/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/source-session")).StatusCode);
        unavailable = false; failure = "client";
        Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync("/operator/login")).StatusCode);
    }
    [Fact]
    public void MissingSecretFailsClosed()
    {
        using var app = App(secret: ""); Assert.Throws<InvalidOperationException>(() => Client(app));
    }
    [Fact]
    public void ClientImportHasNoSecretOrPasswordGrantAndOnlyTheExactCallback()
    {
        var value = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Identity/operator-development-client.json")))!;
        Assert.Equal(OperatorAuthentication.ClientId, value["clientId"]!.GetValue<string>());
        Assert.Null(value["secret"]); Assert.True(value["standardFlowEnabled"]!.GetValue<bool>());
        foreach (var key in new[] { "publicClient", "implicitFlowEnabled", "directAccessGrantsEnabled", "serviceAccountsEnabled", "fullScopeAllowed" }) Assert.False(value[key]!.GetValue<bool>());
        Assert.Equal(OperatorAuthentication.Origin + OperatorAuthentication.Callback, Assert.Single(value["redirectUris"]!.AsArray())!.GetValue<string>());
        Assert.Empty(value["webOrigins"]!.AsArray()); Assert.Empty(value["defaultClientScopes"]!.AsArray());
        Assert.Equal("S256", value["attributes"]!["pkce.code.challenge.method"]!.GetValue<string>());
    }
    [Fact]
    public async Task TicketStoreExpiryRemovalCapacityAndRestartAreFailClosed()
    {
        var store = new OperatorTicketStore(clock);
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(new(new ClaimsIdentity([new("sub", "synthetic")], "test")),
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties { ExpiresUtc = clock.Now.AddMinutes(15) }, OperatorAuthentication.CookieScheme);
        var key = await store.StoreAsync(ticket); Assert.Same(ticket, await store.RetrieveAsync(key));
        Assert.Null(await new OperatorTicketStore(clock).RetrieveAsync(key));
        await store.RemoveAsync(key); Assert.Null(await store.RetrieveAsync(key));
        for (var i = 0; i < 256; i++) await store.StoreAsync(ticket);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync(ticket));
        clock.Now = clock.Now.AddMinutes(15); Assert.Null(await store.RetrieveAsync(key));
        var fresh = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(ticket.Principal,
            new() { ExpiresUtc = clock.Now.AddMinutes(15) }, OperatorAuthentication.CookieScheme);
        Assert.NotEmpty(await store.StoreAsync(fresh));
    }

    private string Token()
    {
        var claims = new List<Claim> { new("nonce", failure == "nonce" ? "wrong" : nonce), new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64), new("laundry_permissions", "orders.create"), new(OperatorAuthentication.Marker, "forged") };
        if (failure != "subject") claims.Add(new("sub", "synthetic-subject"));
        if (name is not null) claims.Add(new("preferred_username", name));
        using var other = RSA.Create(2048); var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(failure == "issuer" ? "https://wrong.test" : OperatorAuthentication.Authority,
            failure == "audience" ? "management-development" : OperatorAuthentication.ClientId, claims, now.AddMinutes(-10), failure == "expired" ? now.AddMinutes(-5) : now.AddMinutes(5),
            new SigningCredentials(new RsaSecurityKey(failure == "signature" ? other : rsa), SecurityAlgorithms.RsaSha256)));
    }
    private sealed class Provider(OperatorIdentityTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.providerCalls++; if (owner.unavailable) throw new HttpRequestException("Synthetic provider unavailable");
            var fields = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(OperatorAuthentication.ClientId, fields["client_id"]); Assert.Equal("synthetic-client-secret", fields["client_secret"]);
            if (request.RequestUri!.AbsolutePath.EndsWith("/par"))
            {
                if (owner.failure == "client") return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_request" }) };
                owner.parState = fields["state"].ToString(); owner.nonce = fields["nonce"].ToString(); owner.challenge = fields["code_challenge"].ToString();
                return new(HttpStatusCode.Created) { Content = JsonContent.Create(new { request_uri = "urn:ietf:params:oauth:request_uri:synthetic", expires_in = 90 }) };
            }
            Assert.Equal("authorization_code", fields["grant_type"]);
            Assert.Equal(owner.challenge, Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(fields["code_verifier"].ToString()))));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "synthetic-unused", token_type = "Bearer", expires_in = 300, id_token = owner.Token() }) };
        }
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    public void Dispose() => rsa.Dispose();
}
