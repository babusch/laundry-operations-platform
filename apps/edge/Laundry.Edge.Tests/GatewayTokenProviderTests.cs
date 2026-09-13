using System.Net;
using System.Net.Http.Json;
using Laundry.Edge.Synchronization;
using Microsoft.Extensions.Configuration;

namespace Laundry.Edge.Tests;

public sealed class GatewayTokenProviderTests
{
    [Fact]
    public async Task TokenIsCachedUntilRefreshWindow_ThenReplaced()
    {
        var requests = 0;
        using var client = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            requests++;
            Assert.Equal("https://localhost:8443/realms/laundry-development/protocol/openid-connect/token",
                request.RequestUri!.AbsoluteUri);
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("grant_type=client_credentials", form);
            Assert.Contains("client_id=gateway-development", form);
            Assert.Contains("client_secret=private-test-value", form);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    access_token = $"access-token-{requests}",
                    token_type = "Bearer",
                    expires_in = 300
                })
            };
        }));
        var clock = new TokenClock();
        using var provider = new GatewayTokenProvider(client, Configuration(), clock);

        Assert.Equal("access-token-1", (await provider.GetAsync(default)).AccessToken);
        Assert.Equal("access-token-1", (await provider.GetAsync(default)).AccessToken);
        Assert.Equal(1, requests);
        clock.Advance(TimeSpan.FromSeconds(271));
        Assert.Equal("access-token-2", (await provider.GetAsync(default)).AccessToken);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneTokenRequest()
    {
        var requests = 0;
        using var client = new HttpClient(new StubHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref requests);
            await Task.Delay(50, cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    access_token = "shared-token",
                    token_type = "Bearer",
                    expires_in = 300
                })
            };
        }));
        using var provider = new GatewayTokenProvider(client, Configuration(), TimeProvider.System);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => provider.GetAsync(default)));
        Assert.All(results, result => Assert.Equal("shared-token", result.AccessToken));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(400, "identity_credentials_rejected")]
    [InlineData(401, "identity_credentials_rejected")]
    [InlineData(429, "identity_rate_limited")]
    [InlineData(503, "identity_unavailable")]
    public async Task TokenEndpointFailuresReturnSafeCodes(int status, string expected)
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        using var provider = new GatewayTokenProvider(client, Configuration(), TimeProvider.System);
        var result = await provider.GetAsync(default);
        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Error);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"access_token\":\"token\",\"token_type\":\"NotBearer\",\"expires_in\":300}")]
    [InlineData("{\"access_token\":\"token\",\"token_type\":\"Bearer\",\"expires_in\":0}")]
    public async Task InvalidTokenResponsesAreRejected(string json)
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) })));
        using var provider = new GatewayTokenProvider(client, Configuration(), TimeProvider.System);
        var result = await provider.GetAsync(default);
        Assert.False(result.Succeeded);
        Assert.Equal("identity_response_invalid", result.Error);
    }

    [Theory]
    [InlineData("GatewayIdentity:TokenEndpoint", "http://localhost/realms/test/protocol/openid-connect/token")]
    [InlineData("GatewayIdentity:TokenEndpoint", "https://example.com/realms/test/protocol/openid-connect/token")]
    [InlineData("GatewayIdentity:TokenEndpoint", "https://localhost/other/token")]
    [InlineData("GatewayIdentity:ClientId", "")]
    [InlineData("GatewayIdentity:ClientSecret", "")]
    public void UnsafeOrIncompleteConfigurationIsRejected(string key, string value)
    {
        var values = Values();
        values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Throws<InvalidOperationException>(() => GatewayIdentitySettings.Read(configuration));
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(Values()).Build();

    private static Dictionary<string, string?> Values() => new()
    {
        ["GatewayIdentity:TokenEndpoint"] =
            "https://localhost:8443/realms/laundry-development/protocol/openid-connect/token",
        ["GatewayIdentity:ClientId"] = "gateway-development",
        ["GatewayIdentity:ClientSecret"] = "private-test-value"
    };

    private sealed class TokenClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
