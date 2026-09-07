using Laundry.Edge.Health;
using Laundry.Edge.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PlantDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Plant")));
builder.Services.AddHealthChecks()
    .AddCheck<PlantDatabaseHealthCheck>("plant-postgres", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();

// Separate entry-point type from the cloud API for integration testing.
public partial class Program;
