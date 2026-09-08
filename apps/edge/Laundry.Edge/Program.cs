using Laundry.Edge.Health;
using Laundry.Edge.Persistence;
using Laundry.Edge.Scans;
using Laundry.Edge.Synchronization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SubmissionValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<WorkerDiagnostics>();
builder.Services.AddHttpClient<CloudDelivery>().ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxWorker>();

builder.Services.AddDbContext<PlantDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Plant")));
builder.Services.AddHealthChecks()
    .AddCheck<PlantDatabaseHealthCheck>("plant-postgres", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();

if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("ScanAcceptance:Enabled"))
{
    var source = app.Configuration.GetSection("ScanAcceptance").Get<DevelopmentSource>()
        ?? throw new InvalidOperationException("Configure the development scan source.");
    if (new[] { source.TenantId, source.PlantId, source.StationId, source.DeviceId }.Contains(Guid.Empty))
        throw new InvalidOperationException("Configure nonempty development tenant, plant, station, and device IDs.");
    app.MapScanAcceptance(source);
    if (app.Configuration.GetValue<bool>("SyncDiagnostics:Enabled")) app.MapSyncDiagnostics(source);
}

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();

// Separate entry-point type from the cloud API for integration testing.
public partial class Program;
