using System.Data.Common;
using System.Globalization;
using System.Net;
using Laundry.Api.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Orders;

public static class DevelopmentOrderPlanDownload
{
    public const string Path = "/api/v1/gateway/order-plans";
    public const string CursorPurpose = "Laundry.Orders.DownloadCursor.v1";
    private static bool Enabled(WebApplication app) => app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("OrderPlanDownload:Enabled");

    public static void UseOrderPlanDownloadBoundary(this WebApplication app) => app.Use(async (http, next) =>
    {
        if (http.Request.Path.StartsWithSegments(Path))
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Enabled(app)) { http.Response.StatusCode = 404; return; }
            if (!http.Request.IsHttps || http.Request.Host.Value != "localhost:7100" ||
                http.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
            { http.Response.StatusCode = 403; return; }
        }
        await next(http);
    });

    public static void MapOrderPlanDownload(this WebApplication app)
    {
        if (!Enabled(app)) return;
        app.MapGet(Path, async (HttpContext http, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            if (!GatewayScope.TryRead(http.User, out var scope))
                return Results.Json(new { code = "gateway_scope_required" }, statusCode: 403);
            var query = http.Request.Query;
            var limit = 100;
            if (query.Keys.Any(x => x is not ("cursor" or "limit")) ||
                (query.ContainsKey("limit") && (query["limit"].Count != 1 ||
                    !int.TryParse(query["limit"][0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100)) ||
                (query.ContainsKey("cursor") && query["cursor"].Count != 1))
                return Results.Json(new { code = "invalid_order_download_query" }, statusCode: 400);
            try
            {
                var options = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(app.Configuration.GetConnectionString("Laundry"),
                    p => p.MigrationsHistoryTable("__order_migrations_history", "orders")).Options;
                var store = new OrderPlanDownloadStore(options, protection.CreateProtector(CursorPurpose));
                return Results.Ok(await store.ReadAsync(scope!, query.ContainsKey("cursor") ? query["cursor"].ToString() : null, limit, ct));
            }
            catch (InvalidOrderDownloadCursorException)
            { return Results.Json(new { code = "invalid_order_download_cursor" }, statusCode: 400); }
            catch (Exception ex) when (ex is DbException or TimeoutException || ex is InvalidOperationException { InnerException: DbException })
            { return Results.Json(new { code = "order_download_unavailable" }, statusCode: 503); }
        }).RequireAuthorization(GatewayAuthorization.OrderDownloadPolicy).WithName("DownloadOrderPlans");
    }
}
