using System.Data.Common;
using System.Globalization;
using System.Net;
using Laundry.Edge.Security;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Edge.Orders;

public static class LocalOrderListEndpoint
{
    public static void MapLocalOrderList(this WebApplication app) =>
        app.MapGet("/api/order-plans", ReadAsync).WithName("GetLocalOrderPlans").Produces<LocalOrderPage>();

    private static async Task<IResult> ReadAsync(HttpContext http, BrowserCookieSourceAuthenticator authenticator,
        LocalOrderList orders, ILoggerFactory logs, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Vary = "Cookie";
        IResult Error(int status, string code) => Results.Json(new { code }, statusCode: status);
        if (!http.Request.IsHttps || http.Request.Host != new HostString("localhost", 7200) ||
            http.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
            (http.Request.Headers.TryGetValue("Origin", out var origin) && origin.ToString() != "https://localhost:7200") ||
            (http.Request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site.ToString() is not ("same-origin" or "none")))
            return Error(403, "local_order_origin_forbidden");
        try
        {
            var source = await authenticator.AuthenticateAsync(http, ct);
            if (source is null) return Error(401, "local_order_source_required");
            if (source.PrincipalType != TrustedSourceKind.BrowserStation || !source.Permissions.Contains(SourcePermissions.ReadOrders))
                return Error(403, "local_order_permission_required");
            var query = http.Request.Query;
            if (query.Any(x => x.Key is not ("limit" or "cursor") || x.Value.Count != 1))
                return Error(400, "invalid_local_order_query");
            var limit = 50;
            if (query.TryGetValue("limit", out var size) &&
                (!int.TryParse(size.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 50))
                return Error(400, "invalid_local_order_query");
            return Results.Ok(await orders.ReadAsync(new(source.TenantId, source.PlantId), limit,
                query.TryGetValue("cursor", out var cursor) ? cursor.ToString() : null, ct));
        }
        catch (LocalOrderListException exception)
        {
            return Error(exception.Code == "local_order_list_changed" ? 409 : exception.Code == "local_order_copy_invalid" ? 503 : 400, exception.Code);
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or TimeoutException ||
            (exception is InvalidOperationException && exception.InnerException is DbException))
        {
            // Never log exceptions, query strings, cookies, order text or cursors.
            logs.CreateLogger("LocalOrderList").LogWarning("Local order storage unavailable. Correlation {CorrelationId}", http.TraceIdentifier);
            http.Response.Headers.RetryAfter = "5";
            return Error(503, "local_order_storage_unavailable");
        }
    }
}
