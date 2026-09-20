using System.Text.Json;

namespace Laundry.Edge.Tests;

public sealed class DevelopmentTransportConfigurationTests
{
    [Fact]
    public void LaunchProfileMakesTrustedHttpsPrimaryAndKeepsHttpOnLoopback()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "launchSettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var profile = document.RootElement
            .GetProperty("profiles")
            .GetProperty("https");

        var addresses = profile
            .GetProperty("applicationUrl")
            .GetString()!
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal("https://localhost:7200", addresses[0]);
        Assert.Contains("http://localhost:5200", addresses);
        Assert.All(addresses, address => Assert.Equal("localhost", new Uri(address).Host));
    }
}
