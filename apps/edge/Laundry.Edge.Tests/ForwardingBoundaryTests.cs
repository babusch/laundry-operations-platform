using Laundry.Edge.Synchronization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laundry.Edge.Tests;

public sealed class ForwardingBoundaryTests
{
    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("Development", false)]
    public async Task DisabledWorkerDoesNotResolveStorageOrSend(string environment, bool enabled)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forwarding:Enabled"] = enabled.ToString(),
            ["Forwarding:Endpoint"] = "deliberately invalid: must not be used"
        }).Build();
        using var worker = new OutboxWorker(new ForbiddenScopes(), config, new TestEnvironment(environment),
            NullLogger<OutboxWorker>.Instance);
        await worker.StartAsync(default);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
    }

    private sealed class ForbiddenScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new Xunit.Sdk.XunitException("Disabled forwarding must not resolve work.");
    }
    private sealed class TestEnvironment(string environment) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "ForwardingTest";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
