using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Laundry.Api.Customers;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Orders;

public static partial class DevelopmentOrderEndpoints
{
    private static void MapHistory(WebApplication app)
    {
        app.MapGet("/api/v1/plants/{plantId:guid}/orders/{orderId:guid}/history", async (Guid plantId, Guid orderId,
            HttpContext http, ManagementAccessStore memberships, DbContextOptions<OrderDbContext> orderOptions,
            DbContextOptions<ManagementAccessDbContext> membershipOptions, DbContextOptions<CustomerDbContext> customerOptions,
            IDataProtectionProvider protection, TimeProvider clock, CancellationToken ct) =>
        {
            var session = await http.AuthenticateAsync(ManagementAuthentication.CookieScheme);
            if (!ManagementSessionAccess.TryReadActor(session, app.Configuration["ManagementAuthentication:Authority"]!.TrimEnd('/'), out var actor))
                return Error(http, 401, "management_session_required");
            try
            {
                var assignments = (await memberships.ReadAssignmentsAsync(actor!, ct)).Where(x => x.PlantId == plantId).ToArray();
                if (assignments.Length == 0) return Error(http, 403, "access_denied");
                if (assignments.Length != 1) return Error(http, 409, "plant_scope_ambiguous");
                if (!assignments[0].Permissions.Contains(OrderPlanningPermissions.Read, StringComparer.Ordinal))
                    return Error(http, 403, "access_denied");
                var scope = new OrderPlanningScope(assignments[0].TenantId, plantId, actor!.Issuer, actor.Subject);
                var query = http.Request.Query;
                var limit = 25;
                if (query.Keys.Any(x => x is not ("limit" or "cursor")) ||
                    (query.ContainsKey("limit") && (query["limit"].Count != 1 ||
                    !int.TryParse(query["limit"][0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100)))
                    return Error(http, 400, "invalid_order_history_query");
                var protector = protection.CreateProtector("Laundry.Orders.HistoryCursor.v1");
                OrderHistoryCursor? cursor = null;
                if (query.ContainsKey("cursor") && (query["cursor"].Count != 1 ||
                    !OrderHistoryCursor.TryRead(protector, query["cursor"][0] ?? "", scope, orderId, limit, out cursor)))
                    return Error(http, 400, "invalid_order_history_query");
                var access = new SessionOrderPlanningAccess(actor, membershipOptions, customerOptions);
                var rows = await new OrderPlanningStore(orderOptions, access, clock).ReadHistoryAsync(scope, orderId, limit, cursor?.AfterVersion ?? 0, ct);
                var items = rows.Take(limit).ToArray();
                var next = rows.Length > limit
                    ? new OrderHistoryCursor(scope.TenantId, plantId, orderId, limit, items[^1].Version).Encode(protector)
                    : null;
                return Results.Ok(new OrderHistoryPage(items, next));
            }
            catch (OrderPlanningException exception)
            {
                return Error(http, exception.Code == "order_not_found" ? 404 : 403, exception.Code);
            }
            catch (Exception exception) when (exception is DbException or TimeoutException or JsonException ||
                exception is InvalidOperationException { InnerException: DbException })
            {
                return Error(http, 503, "order_history_unavailable");
            }
        }).RequireAuthorization(ManagementAuthentication.Policy).WithName("GetOrderHistory");
    }
}
