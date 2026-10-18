using Laundry.Edge.Orders;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Laundry.Edge.Tests;

public sealed class DevelopmentOrderAccessCommandTests
{
    private static WebApplicationBuilder Builder(string environment = "Development")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Plant"] = "Host=localhost;Port=15433;Database=laundry_plant",
            ["ScanAcceptance:TenantId"] = "11111111-1111-4111-8111-111111111111",
            ["ScanAcceptance:PlantId"] = "22222222-2222-4222-8222-222222222222",
            ["Forwarding:Enabled"] = "true",
            ["LocalOrderList:Enabled"] = "false"
        });
        return builder;
    }

    [Fact]
    public void AccessCommand_ForcesLocalOnlyProcessOverridesWithoutNeedingIdentitySecrets()
    {
        var builder = Builder(); DevelopmentOrderAccessCommand.Configure(builder);
        Assert.Equal("false", builder.Configuration["Forwarding:Enabled"]);
        Assert.Equal("true", builder.Configuration["LocalOrderList:Enabled"]);
        Assert.Equal("true", builder.Configuration["SourceEnrollment:Enabled"]);
        Assert.Equal("false", builder.Configuration["SyncDiagnostics:Enabled"]);
        Assert.Equal("https://localhost:7200", builder.WebHost.GetSetting("urls"));
    }

    [Theory]
    [InlineData("ConnectionStrings:Plant", "Host=remote.invalid;Port=15433;Database=laundry_plant")]
    [InlineData("ConnectionStrings:Plant", "Host=localhost;Port=15432;Database=laundry_plant")]
    [InlineData("ConnectionStrings:Plant", "Host=localhost;Port=15433;Database=laundry")]
    [InlineData("ScanAcceptance:TenantId", "44444444-4444-4444-8444-444444444444")]
    [InlineData("ScanAcceptance:PlantId", "44444444-4444-4444-8444-444444444444")]
    [InlineData("Kestrel:Endpoints:Other:Url", "http://0.0.0.0:7201")]
    public void AccessCommand_RejectsUnexpectedInstallationBeforeServerOrDatabase(string key, string value)
    {
        var builder = Builder(); builder.Configuration[key] = value;
        Assert.Throws<InvalidOperationException>(() => DevelopmentOrderAccessCommand.Configure(builder));
        Assert.Equal("true", builder.Configuration["Forwarding:Enabled"]);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void AccessCommand_RejectsNonDevelopmentBeforeActivation(string environment) =>
        Assert.Throws<InvalidOperationException>(() => DevelopmentOrderAccessCommand.Configure(Builder(environment)));
}
