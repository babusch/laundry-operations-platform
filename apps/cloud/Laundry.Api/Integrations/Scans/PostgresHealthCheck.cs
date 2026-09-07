using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Laundry.Api.Integrations.Scans;

public sealed class PostgresHealthCheck(ScanDbContext database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return await database.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database unavailable.");
    }
}
