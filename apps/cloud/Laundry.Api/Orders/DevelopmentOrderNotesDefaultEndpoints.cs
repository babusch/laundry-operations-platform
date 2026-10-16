using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Laundry.Api.Customers;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Orders;

public static partial class DevelopmentOrderEndpoints
{
    private static void MapNotesDefault(WebApplication app)
    {
        const string path = "/api/v1/plants/{plantId:guid}/orders/settings/notes";
        app.MapGet(path, (Guid plantId, HttpContext http) => NotesDefault(app, plantId, http, false))
            .RequireAuthorization(ManagementAuthentication.Policy).WithName("GetOrderNotesDefault");
        app.MapPut(path, (Guid plantId, HttpContext http) => NotesDefault(app, plantId, http, true))
            .RequireAuthorization(ManagementAuthentication.Policy).WithName("UpdateOrderNotesDefault");
    }

    private static async Task<IResult> NotesDefault(WebApplication app, Guid plantId, HttpContext http, bool write)
    {
        var services = http.RequestServices;
        var ct = http.RequestAborted;
        var session = await http.AuthenticateAsync(ManagementAuthentication.CookieScheme);
        if (!ManagementSessionAccess.TryReadActor(session, app.Configuration["ManagementAuthentication:Authority"]!.TrimEnd('/'), out var actor))
            return Error(http, 401, "management_session_required");
        try
        {
            if (http.Request.Query.Count != 0) return Error(http, 400, "invalid_order_notes_default_query");
            var assignments = (await services.GetRequiredService<ManagementAccessStore>().ReadAssignmentsAsync(actor!, ct))
                .Where(x => x.PlantId == plantId).ToArray();
            if (assignments.Length == 0) return Error(http, 403, "access_denied");
            if (assignments.Length != 1) return Error(http, 409, "plant_scope_ambiguous");
            var permission = write ? OrderPlanningPermissions.EditNotesDefault : OrderPlanningPermissions.Read;
            if (!assignments[0].Permissions.Contains(permission, StringComparer.Ordinal)) return Error(http, 403, "access_denied");
            var scope = new OrderPlanningScope(assignments[0].TenantId, plantId, actor!.Issuer, actor.Subject);
            var access = new SessionOrderPlanningAccess(actor, services.GetRequiredService<DbContextOptions<ManagementAccessDbContext>>(),
                services.GetRequiredService<DbContextOptions<CustomerDbContext>>());
            var store = new OrderPlanningStore(services.GetRequiredService<DbContextOptions<OrderDbContext>>(), access,
                services.GetRequiredService<TimeProvider>());
            OrderNotesDefaultDetails result;
            if (!write) result = await store.ReadNotesDefaultAsync(scope, ct);
            else
            {
                try { await services.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Error(http, 400, "management_csrf_required"); }
                if (!http.Request.HasJsonContentType()) return Error(http, 415, "order_json_required");
                var keyHeader = http.Request.Headers["Idempotency-Key"];
                if (keyHeader.Count != 1 || !Guid.TryParseExact(keyHeader[0], "D", out var key) || key == Guid.Empty)
                    return Error(http, 400, "order_idempotency_key_required");
                var condition = http.Request.Headers.IfMatch;
                if (condition.Count == 0) return Error(http, 428, "order_notes_default_precondition_required");
                if (condition.Count != 1 || !TryReadNotesDefaultTag(condition[0]!, scope, out var version))
                    return Error(http, 400, "invalid_order_notes_default_precondition");
                const int max = 1024;
                if (http.Request.ContentLength > max) return Error(http, 413, "order_request_too_large");
                var buffer = new byte[max + 1]; var count = 0;
                while (count < buffer.Length)
                {
                    var read = await http.Request.Body.ReadAsync(buffer.AsMemory(count), ct);
                    if (read == 0) break;
                    count += read;
                }
                if (count > max) return Error(http, 413, "order_request_too_large");
                using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
                if (document.RootElement.ValueKind != JsonValueKind.Object) return Error(http, 400, "invalid_order_notes_default_request");
                var properties = document.RootElement.EnumerateObject().ToArray();
                if (properties.Length != 1 || properties[0].Name != "notesVisibleToOperators" ||
                    properties[0].Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Error(http, 400, "invalid_order_notes_default_request");
                result = await store.ChangeNotesDefaultAsync(scope, key, version, properties[0].Value.GetBoolean(), ct);
            }
            http.Response.Headers.ETag = NotesDefaultTag(scope, result.Version);
            return Results.Ok(result);
        }
        catch (JsonException) { return Error(http, 400, "invalid_order_notes_default_request"); }
        catch (OrderPlanningException exception)
        {
            return Error(http, exception.Code switch
            {
                "access_denied" => 403,
                "idempotency_conflict" => 409,
                "version_conflict" => 412,
                _ => 400
            }, exception.Code);
        }
        catch (Exception exception) when (exception is DbException or TimeoutException ||
            exception is InvalidOperationException { InnerException: DbException } || exception is DbUpdateException { InnerException: DbException })
        { return Error(http, 503, "order_notes_default_unavailable"); }
    }

    private static string NotesDefaultTag(OrderPlanningScope scope, long version)
    {
        var scopeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"order-notes-default:v1:{scope.TenantId:D}:{scope.PlantId:D}")));
        return $"\"notes-default-{scopeHash}:{version.ToString(CultureInfo.InvariantCulture)}\"";
    }

    private static bool TryReadNotesDefaultTag(string value, OrderPlanningScope scope, out long version)
    {
        version = 0;
        var prefix = NotesDefaultTag(scope, 0)[..^2];
        return value.Length > prefix.Length + 1 && value.StartsWith(prefix, StringComparison.Ordinal) && value[^1] == '"' &&
            long.TryParse(value.AsSpan(prefix.Length, value.Length - prefix.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out version) &&
            version >= 0 && value == NotesDefaultTag(scope, version);
    }
}
