using System.Net;

namespace Laundry.Edge.Synchronization;

public sealed record ForwardingSettings(Uri Endpoint, Guid TenantId, Guid PlantId)
{
    public static ForwardingSettings Read(IConfiguration configuration)
    {
        if (!Uri.TryCreate(configuration["Forwarding:Endpoint"], UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "http" || !IPAddress.TryParse(endpoint.Host, out var ip) || !IPAddress.IsLoopback(ip) ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath != "/api/scans")
            throw new InvalidOperationException("Forwarding requires an HTTP loopback IP endpoint ending in /api/scans for local development.");
        var tenant = configuration.GetValue<Guid>("ScanAcceptance:TenantId");
        var plant = configuration.GetValue<Guid>("ScanAcceptance:PlantId");
        if (tenant == Guid.Empty || plant == Guid.Empty)
            throw new InvalidOperationException("Configure forwarding tenant and plant scope.");
        return new(endpoint, tenant, plant);
    }
}
