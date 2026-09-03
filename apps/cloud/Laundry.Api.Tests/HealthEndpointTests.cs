using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Laundry.Api.Tests;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> application)
    {
        _client = application.CreateClient();
    }

    [Fact]
    public async Task GetHealth_WhenApiStarts_ReturnsHealthy()
    {
        using var response = await _client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
