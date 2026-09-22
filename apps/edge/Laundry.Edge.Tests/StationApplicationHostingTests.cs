using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Laundry.Edge.Tests;

public sealed class StationApplicationHostingTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/receiving")]
    public async Task StationRoute_ReturnsApplicationShellWithoutCaching(string path)
    {
        using var application = CreateApplication();
        using var client = application.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Contains("Station application fixture", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FingerprintedAsset_IsLongLivedAndImmutable()
    {
        using var application = CreateApplication();
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/assets/app-test.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("stationFixtureLoaded", await response.Content.ReadAsStringAsync());
        Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl?.MaxAge);
        Assert.Contains(response.Headers.CacheControl!.Extensions,
            extension => string.Equals(extension.Name, "immutable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/api/not-a-real-endpoint")]
    [InlineData("/health/not-a-real-endpoint")]
    public async Task UnknownGatewayRoute_DoesNotReturnApplicationShell(string path)
    {
        using var application = CreateApplication();
        using var client = application.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Station application fixture", await response.Content.ReadAsStringAsync());
    }

    private static WebApplicationFactory<Program> CreateApplication()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "StationAppFixture");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseWebRoot(webRoot);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Plant"] = null
                }));
        });
    }
}
