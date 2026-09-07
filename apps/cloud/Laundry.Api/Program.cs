using Laundry.Api.Integrations.Scans;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ScanContractValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<ScanDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Laundry")
        ?? throw new InvalidOperationException("Configure ConnectionStrings:Laundry before using the database.")));
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();

if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("ScanIngestion:Enabled"))
{
    var tenantId = app.Configuration.GetValue<Guid>("ScanIngestion:TenantId");
    var plantId = app.Configuration.GetValue<Guid>("ScanIngestion:PlantId");
    if (tenantId == Guid.Empty || plantId == Guid.Empty)
    {
        throw new InvalidOperationException("Configure the development scan tenant and plant.");
    }

    app.MapScanIngestion(new DevelopmentScanScope(tenantId, plantId));
}

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();

// WebApplicationFactory uses this entry point in integration tests.
public partial class Program;
