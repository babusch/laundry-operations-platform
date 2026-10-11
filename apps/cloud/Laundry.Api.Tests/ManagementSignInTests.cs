using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Laundry.Api.Customers;
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
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
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
    private string _parState = "";

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
        Assert.Equal(Issuer, ticket.Principal.FindFirstValue("iss"));
        Assert.True(ManagementSessionAccess.TryReadActor(AuthenticateResult.Success(ticket), Issuer, out var actor));
        Assert.Equal("synthetic-person", actor!.Subject);
        Assert.DoesNotContain(ticket.Principal.Claims, claim => claim.Type is "name" or "email");
        Assert.Equal(4, ticket.Principal.Claims.Count());
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
    [Trait("Category", "Database")]
    public async Task RealLoginCookie_CanReadItsAssignmentAndEligibleCustomers()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<ManagementAccessDbContext>().UseNpgsql(postgres.GetConnectionString(),
            provider => provider.MigrationsHistoryTable("__management_access_migrations", "identity_access")).Options;
        await using var database = new ManagementAccessDbContext(options);
        await database.Database.MigrateAsync();
        var member = new PlantMembership
        {
            Id = Guid.NewGuid(),
            ActorIssuer = Issuer,
            ActorSubject = "synthetic-person",
            TenantId = ManagementAccessStore.DevelopmentTenant,
            PlantId = ManagementAccessStore.DevelopmentPlant,
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        member.Permissions = Laundry.Api.Orders.OrderPlanningPermissions.Management.Select(permission =>
            new PlantMembershipPermission { MembershipId = member.Id, Permission = permission }).ToList();
        database.Memberships.Add(member);
        database.AuditEntries.Add(new MembershipAuditEntry
        {
            Id = Guid.NewGuid(),
            MembershipId = member.Id,
            MembershipVersion = 1,
            Action = "synthetic_assignment_created",
            RecordedAtUtc = member.CreatedAtUtc,
            RecordedBy = "synthetic-test-setup",
            AfterState = "{}"
        });
        await database.SaveChangesAsync();
        var customerOptions = new DbContextOptionsBuilder<CustomerDbContext>().UseNpgsql(postgres.GetConnectionString(),
            provider => provider.MigrationsHistoryTable("__customer_migrations", "customers")).Options;
        await using var customerDatabase = new CustomerDbContext(customerOptions);
        await customerDatabase.Database.MigrateAsync();
        await new CustomerStore(customerDatabase).InitializeDevelopmentAsync(member.TenantId, member.PlantId);
        var orderOptions = new DbContextOptionsBuilder<Laundry.Api.Orders.OrderDbContext>().UseNpgsql(postgres.GetConnectionString(),
            provider => provider.MigrationsHistoryTable("__order_migrations_history", "orders")).Options;
        await using var orderDatabase = new Laundry.Api.Orders.OrderDbContext(orderOptions);
        await orderDatabase.Database.MigrateAsync();
        await using var app = Application(accessConnection: postgres.GetConnectionString(), customersEnabled: true, ordersEnabled: true);
        using var client = Client(app);
        using var callback = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        using var response = await client.GetAsync("/management/access");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var assignment = Assert.Single(json.RootElement.GetProperty("assignments").EnumerateArray());
        Assert.Equal(ManagementAccessStore.DevelopmentPlant, assignment.GetProperty("plantId").GetGuid());
        Assert.Equal(3, assignment.GetProperty("permissions").GetArrayLength());
        Assert.True(json.RootElement.GetProperty("orderAccessEnabled").GetBoolean());
        var customerPath = $"/management/tenants/{member.TenantId:D}/plants/{member.PlantId:D}/customers";
        using var choices = await client.GetAsync(customerPath);
        Assert.Equal(HttpStatusCode.OK, choices.StatusCode);
        using var choiceJson = JsonDocument.Parse(await choices.Content.ReadAsStringAsync());
        var choice = Assert.Single(choiceJson.RootElement.GetProperty("customers").EnumerateArray());
        Assert.Equal(CustomerStore.DevelopmentCustomer, choice.GetProperty("id").GetGuid());
        Assert.Equal("Demo laundry customer", choice.GetProperty("displayName").GetString());
        Assert.False(choiceJson.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(choiceJson.RootElement.GetProperty("orderAccessEnabled").GetBoolean());
        var csrf = await client.GetFromJsonAsync<JsonElement>("/management/antiforgery");
        client.DefaultRequestHeaders.Add("X-Management-CSRF", csrf.GetProperty("requestToken").GetString());
        using var orderRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/plants/{member.PlantId:D}/orders")
        {
            Content = JsonContent.Create(new
            {
                customerId = CustomerStore.DevelopmentCustomer,
                plannedReceivingDate = "2030-01-01",
                plannedDeliveryDate = "2030-01-03"
            })
        };
        orderRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var createdOrder = await client.SendAsync(orderRequest);
        Assert.Equal(HttpStatusCode.Created, createdOrder.StatusCode);
        using var savedOrder = await client.GetAsync(createdOrder.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, savedOrder.StatusCode);
        Assert.Equal(createdOrder.Headers.ETag, savedOrder.Headers.ETag);
        Assert.Equal("synthetic-person", (await orderDatabase.Orders.SingleAsync()).CreatedBySubject);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/management/tenants/{Guid.NewGuid():D}/plants/{member.PlantId:D}/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/management/tenants/{member.TenantId:D}/plants/{Guid.NewGuid():D}/customers")).StatusCode);
        await database.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.read'");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(customerPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(createdOrder.Headers.Location)).StatusCode);
        using var anonymous = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(customerPath)).StatusCode);
        await database.Database.ExecuteSqlRawAsync("INSERT INTO identity_access.plant_membership_permissions (membership_id, permission) SELECT id, 'orders.read' FROM identity_access.plant_memberships");
        await customerDatabase.Database.ExecuteSqlRawAsync("ALTER TABLE customers.laundry_customers RENAME TO temporarily_unavailable_customers");
        try
        {
            using var unavailable = await client.GetAsync(customerPath);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Contains("customer_lookup_unavailable", await unavailable.Content.ReadAsStringAsync());
        }
        finally
        {
            await customerDatabase.Database.ExecuteSqlRawAsync("ALTER TABLE customers.temporarily_unavailable_customers RENAME TO laundry_customers");
        }
        await using var disabled = Application(accessConnection: postgres.GetConnectionString());
        using var disabledClient = Client(disabled);
        Assert.Equal(HttpStatusCode.NotFound, (await disabledClient.GetAsync(customerPath)).StatusCode);
        await using var production = Application(environment: "Production", accessConnection: postgres.GetConnectionString(), customersEnabled: true);
        using var productionClient = Client(production);
        Assert.Equal(HttpStatusCode.NotFound, (await productionClient.GetAsync(customerPath)).StatusCode);
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
    public void StartupImportDirectory_ContainsOnlyRealmRepresentations()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Identity", "StartupImport"), "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            Assert.True(document.RootElement.TryGetProperty("realm", out _), "Automatic startup imports must be realm representations, not manual client imports.");
            Assert.False(document.RootElement.TryGetProperty("clientId", out _));
        }
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
    public async Task PushedAuthorization_AcceptsConfidentialPkceLoginWithoutChangingTheFlow()
    {
        await using var app = Application(usePar: true);
        using var client = Client(app);
        using var callback = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(_parState));
        Assert.Equal(_challenge, Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(_verifier!))));
        using var session = await client.GetAsync("/management/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task PushedAuthorization_ClientRejectionReturnsSafeErrorWithoutFallbackOrExceptionPage()
    {
        await using var app = Application(usePar: true, secret: "synthetic-wrong-client-secret");
        using var client = Client(app);
        using var login = await client.GetAsync("/management/login");
        Assert.Equal(HttpStatusCode.BadGateway, login.StatusCode);
        Assert.Null(login.Headers.Location);
        Assert.Equal("{\"code\":\"management_identity_rejected\"}", await login.Content.ReadAsStringAsync());
        Assert.Equal("no-store", login.Headers.CacheControl!.ToString());
        using var session = await client.GetAsync("/management/session");
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
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
        string remoteAddress = "127.0.0.1", bool unavailable = false, string secret = "synthetic-client-secret", bool usePar = false,
        string? accessConnection = null, bool customersEnabled = false, bool ordersEnabled = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ManagementAuthentication:Enabled", enabled.ToString());
            builder.UseSetting("ManagementAuthentication:Authority", Issuer);
            builder.UseSetting("ManagementAuthentication:ClientId", ClientId);
            builder.UseSetting("ManagementAuthentication:ClientSecret", secret);
            builder.UseSetting("ManagementAccess:Enabled", (accessConnection is not null).ToString());
            builder.UseSetting("Customers:Enabled", customersEnabled.ToString());
            builder.UseSetting("OrderPlanning:Enabled", ordersEnabled.ToString());
            if (accessConnection is not null) builder.UseSetting("ConnectionStrings:Laundry", accessConnection);
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
                        PushedAuthorizationRequestEndpoint = usePar ? Issuer + "/par" : null,
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
        var state = query.TryGetValue("state", out var stateValue) ? stateValue.ToString() : "";
        if (query.ContainsKey("request_uri"))
        {
            Assert.Equal("urn:ietf:params:oauth:request_uri:synthetic-request", query["request_uri"]);
            state = _parState;
        }
        else
        {
            _nonce = query["nonce"].ToString();
            _challenge = query["code_challenge"].ToString();
        }
        return await client.PostAsync(ManagementAuthentication.CallbackPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = "synthetic-code",
            ["state"] = _failure == "state" ? "forged" : state
        }));
    }

    private string Token(string audience, string nonce)
    {
        var claims = new List<Claim>
        {
            new("nonce", _failure == "nonce" ? "wrong" : nonce),
            new("laundry_permissions", "scans.ingest"),
            new("name", "Synthetic profile name"),
            new("email", "synthetic@example.test"),
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
            var fields = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (request.RequestUri!.AbsolutePath.EndsWith("/par", StringComparison.Ordinal))
            {
                Assert.Equal(ClientId, fields["client_id"]);
                if (fields["client_secret"] != "synthetic-client-secret")
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = JsonContent.Create(new { error = "invalid_request", error_description = "Authentication failed." })
                    };
                }

                Assert.Equal("code", fields["response_type"]);
                Assert.Equal("S256", fields["code_challenge_method"]);
                owner._parState = fields["state"].ToString();
                owner._nonce = fields["nonce"].ToString();
                owner._challenge = fields["code_challenge"].ToString();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { request_uri = "urn:ietf:params:oauth:request_uri:synthetic-request", expires_in = 90 })
                };
            }

            Assert.EndsWith("/token", request.RequestUri.AbsolutePath);
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
