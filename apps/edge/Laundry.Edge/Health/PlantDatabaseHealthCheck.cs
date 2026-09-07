using Laundry.Edge.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Laundry.Edge.Health;

public sealed class PlantDatabaseHealthCheck(PlantDbContext database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return await database.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Plant database unavailable.");
    }
}
