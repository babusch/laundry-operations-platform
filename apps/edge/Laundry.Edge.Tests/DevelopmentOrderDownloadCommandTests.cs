using Laundry.Edge.Orders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Laundry.Edge.Tests;

public sealed class DevelopmentOrderDownloadCommandTests
{
    [Theory]
    [InlineData("environment", "Production")]
    [InlineData("ConnectionStrings:Plant", "Host=remote.invalid;Port=15433;Database=laundry_plant")]
    [InlineData("ConnectionStrings:Plant", "Host=localhost;Port=15432;Database=laundry_plant")]
    [InlineData("ConnectionStrings:Plant", "Host=localhost;Port=15433;Database=laundry")]
    [InlineData("ScanAcceptance:TenantId", "44444444-4444-4444-8444-444444444444")]
    [InlineData("ScanAcceptance:PlantId", "44444444-4444-4444-8444-444444444444")]
    [InlineData("GatewayIdentity:ClientId", "another-client")]
    [InlineData("GatewayIdentity:ClientSecret", "")]
    [InlineData("GatewayIdentity:TokenEndpoint", "http://localhost:8443/realms/laundry-development/protocol/openid-connect/token")]
    [InlineData("GatewayIdentity:TokenEndpoint", "https://localhost:8443/realms/other/protocol/openid-connect/token")]
    public async Task DownloadCommand_RejectsWrongInstallationBeforeNetworkOrDatabase(string key, string value)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Plant"] = "Host=localhost;Port=15433;Database=laundry_plant",
            ["ScanAcceptance:TenantId"] = "11111111-1111-4111-8111-111111111111",
            ["ScanAcceptance:PlantId"] = "22222222-2222-4222-8222-222222222222",
            ["GatewayIdentity:ClientId"] = "gateway-development",
            ["GatewayIdentity:ClientSecret"] = "synthetic-only",
            ["GatewayIdentity:TokenEndpoint"] = "https://localhost:8443/realms/laundry-development/protocol/openid-connect/token"
        };
        values[key] = value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var environment = new TestEnvironment { EnvironmentName = key == "environment" ? value : "Development" };
        Assert.Equal(1, await DevelopmentOrderDownloadCommand.RunAsync(environment, config));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SyntheticCommandTest";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
