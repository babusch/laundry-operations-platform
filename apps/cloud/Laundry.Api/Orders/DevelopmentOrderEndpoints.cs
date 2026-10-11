using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Laundry.Api.Customers;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Orders;

public static class DevelopmentOrderEndpoints
{
    public static bool Enabled(IConfiguration configuration, IHostEnvironment environment) =>
        ManagementSessionAccess.Enabled(configuration, environment) && configuration.GetValue<bool>("Customers:Enabled") &&
        configuration.GetValue<bool>("OrderPlanning:Enabled");

    public static void AddDevelopmentOrderPlanning(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (!Enabled(configuration, environment)) return;
        services.AddDbContext<OrderDbContext>(options => options.UseNpgsql(configuration.GetConnectionString("Laundry")
            ?? throw new InvalidOperationException("Configure the cloud database."),
            provider => provider.MigrationsHistoryTable("__order_migrations_history", "orders")));
    }

    public static void UseDevelopmentOrderBoundary(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            if (http.Request.Path.StartsWithSegments("/api/v1/plants", out var remaining) &&
                System.Text.RegularExpressions.Regex.IsMatch(remaining.Value ?? "", "^/[^/]+/orders(?:/|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                http.Response.Headers.CacheControl = "no-store";
                if (!Enabled(app.Configuration, app.Environment)) { http.Response.StatusCode = 404; return; }
                if (!http.Request.IsHttps || http.Request.Host.Value != "localhost:7100" ||
                    http.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
                { http.Response.StatusCode = 403; return; }
            }
            await next(http);
        });
    }

    public static void MapDevelopmentOrderPlanning(this WebApplication app)
    {
        if (!Enabled(app.Configuration, app.Environment)) return;
        app.MapGet("/api/v1/plants/{plantId:guid}/orders", async (Guid plantId, HttpContext http,
            ManagementAccessStore memberships, DbContextOptions<OrderDbContext> orderOptions,
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
                    return Error(http, 400, "invalid_order_query");
                var protector = protection.CreateProtector("Laundry.Orders.ListCursor.v1");
                OrderListCursor? cursor = null;
                if (query.ContainsKey("cursor") && (query["cursor"].Count != 1 ||
                    !OrderListCursor.TryRead(protector, query["cursor"][0] ?? "", scope, limit, out cursor)))
                    return Error(http, 400, "invalid_order_query");
                var access = new SessionOrderPlanningAccess(actor, membershipOptions, customerOptions);
                var rows = await new OrderPlanningStore(orderOptions, access, clock)
                    .ListAsync(scope, limit, cursor?.ReceivingDate, cursor?.OrderId, ct);
                var items = rows.Take(limit).Select(OrderDetails.From).ToArray();
                var next = rows.Length > limit
                    ? new OrderListCursor(scope.TenantId, plantId, limit, items[^1].PlannedReceivingDate, items[^1].Id).Encode(protector)
                    : null;
                return Results.Ok(new OrderListPage(items, next));
            }
            catch (OrderPlanningException exception) { return Error(http, 403, exception.Code); }
            catch (Exception exception) when (exception is DbException or TimeoutException ||
                exception is InvalidOperationException { InnerException: DbException })
            {
                return Error(http, 503, "order_storage_unavailable");
            }
        }).RequireAuthorization(ManagementAuthentication.Policy).WithName("ListPlannedOrders");

        app.MapGet("/api/v1/plants/{plantId:guid}/orders/{orderId:guid}", async (Guid plantId, Guid orderId,
            HttpContext http, ManagementAccessStore memberships, DbContextOptions<OrderDbContext> orderOptions,
            DbContextOptions<ManagementAccessDbContext> membershipOptions, DbContextOptions<CustomerDbContext> customerOptions,
            TimeProvider clock, CancellationToken ct) =>
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
                var access = new SessionOrderPlanningAccess(actor, membershipOptions, customerOptions);
                var order = await new OrderPlanningStore(orderOptions, access, clock).GetAsync(scope, orderId, ct);
                if (order is null) return Error(http, 404, "order_not_found");
                var details = OrderDetails.From(order);
                http.Response.Headers.ETag = details.EntityTag();
                return Results.Ok(details);
            }
            catch (OrderPlanningException exception) { return Error(http, 403, exception.Code); }
            catch (Exception exception) when (exception is DbException or TimeoutException ||
                exception is InvalidOperationException { InnerException: DbException })
            {
                return Error(http, 503, "order_storage_unavailable");
            }
        }).RequireAuthorization(ManagementAuthentication.Policy).WithName("GetPlannedOrder");

        app.MapPost("/api/v1/plants/{plantId:guid}/orders", (Guid plantId, HttpContext http) =>
            Save(app, plantId, null, http)).RequireAuthorization(ManagementAuthentication.Policy).WithName("CreatePlannedOrder");
        app.MapPut("/api/v1/plants/{plantId:guid}/orders/{orderId:guid}/plan", (Guid plantId, Guid orderId, HttpContext http) =>
            Save(app, plantId, orderId, http)).RequireAuthorization(ManagementAuthentication.Policy).WithName("UpdatePlannedOrder");
    }

    private static async Task<IResult> Save(WebApplication app, Guid plantId, Guid? orderId, HttpContext http)
    {
        var services = http.RequestServices;
        var antiforgery = services.GetRequiredService<IAntiforgery>();
        var memberships = services.GetRequiredService<ManagementAccessStore>();
        var orderOptions = services.GetRequiredService<DbContextOptions<OrderDbContext>>();
        var membershipOptions = services.GetRequiredService<DbContextOptions<ManagementAccessDbContext>>();
        var customerOptions = services.GetRequiredService<DbContextOptions<CustomerDbContext>>();
        var clock = services.GetRequiredService<TimeProvider>();
        var ct = http.RequestAborted;
        var session = await http.AuthenticateAsync(ManagementAuthentication.CookieScheme);
        if (!ManagementSessionAccess.TryReadActor(session, app.Configuration["ManagementAuthentication:Authority"]!.TrimEnd('/'), out var actor))
            return Error(http, 401, "management_session_required");
        try
        {
            await antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException) { return Error(http, 400, "management_csrf_required"); }
        if (!http.Request.HasJsonContentType()) return Error(http, 415, "order_json_required");
        var keyHeader = http.Request.Headers["Idempotency-Key"];
        if (keyHeader.Count != 1 || !Guid.TryParseExact(keyHeader[0], "D", out var key) || key == Guid.Empty)
            return Error(http, 400, "order_idempotency_key_required");
        try
        {
            var assignments = (await memberships.ReadAssignmentsAsync(actor!, ct)).Where(x => x.PlantId == plantId).ToArray();
            if (assignments.Length == 0) return Error(http, 403, "access_denied");
            if (assignments.Length != 1) return Error(http, 409, "plant_scope_ambiguous");
            if (!assignments[0].Permissions.Contains(orderId.HasValue ? OrderPlanningPermissions.EditPlan : OrderPlanningPermissions.Create, StringComparer.Ordinal))
                return Error(http, 403, "access_denied");

            long expectedVersion = 0;
            if (orderId.HasValue)
            {
                var condition = http.Request.Headers.IfMatch;
                if (condition.Count == 0) return Error(http, 428, "order_precondition_required");
                if (condition.Count != 1 || !TryReadETag(condition[0]!, out var taggedId, out expectedVersion))
                    return Error(http, 400, "invalid_order_precondition");
                if (taggedId != orderId.Value) return Error(http, 412, "version_conflict");
            }

            const int maxBodyBytes = 16 * 1024;
            if (http.Request.ContentLength > maxBodyBytes) return Error(http, 413, "order_request_too_large");
            var buffer = new byte[maxBodyBytes + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await http.Request.Body.ReadAsync(buffer.AsMemory(count), ct);
                if (read == 0) break;
                count += read;
            }
            if (count > maxBodyBytes) return Error(http, 413, "order_request_too_large");
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (!TryParsePlan(document.RootElement, out var plan) || (orderId.HasValue &&
                (!document.RootElement.TryGetProperty("customerReference", out _) || !document.RootElement.TryGetProperty("notes", out _))))
                return Error(http, 400, "invalid_order_request");
            var scope = new OrderPlanningScope(assignments[0].TenantId, plantId, actor!.Issuer, actor.Subject);
            var access = new SessionOrderPlanningAccess(actor, membershipOptions, customerOptions);
            var store = new OrderPlanningStore(orderOptions, access, clock);
            var receipt = orderId.HasValue
                ? await store.UpdateAsync(scope, key, orderId.Value, expectedVersion, plan!, ct)
                : await store.CreateAsync(scope, key, plan!, ct);
            http.Response.Headers.ETag = receipt.ETag;
            http.Response.Headers.Location = receipt.Location;
            return Results.Content(receipt.Body, "application/json", statusCode: receipt.Status);
        }
        catch (JsonException) { return Error(http, 400, "invalid_order_request"); }
        catch (OrderPlanningException exception)
        {
            return Error(http, exception.Code switch
            {
                "access_denied" => 403,
                "idempotency_conflict" => 409,
                "order_not_planned" => 409,
                "order_not_found" => 404,
                "version_conflict" => 412,
                _ => 400
            }, exception.Code);
        }
        catch (Exception exception) when (exception is DbException or TimeoutException ||
            exception is InvalidOperationException { InnerException: DbException } ||
            exception is DbUpdateException { InnerException: DbException })
        {
            // A transport failure can follow commit: preserve/retry the exact key and body.
            return Error(http, 503, "order_storage_unavailable");
        }
    }

    private static bool TryReadETag(string value, out Guid orderId, out long version)
    {
        orderId = default;
        version = default;
        // Exactly one strong, canonical ID/version tag: no wildcard, weak tag or list.
        if (value.Length < 40 || value[0] != '"' || value[^1] != '"' || value[37] != ':') return false;
        return Guid.TryParseExact(value.Substring(1, 36), "D", out orderId) && orderId != Guid.Empty &&
            long.TryParse(value.AsSpan(38, value.Length - 39), NumberStyles.None, CultureInfo.InvariantCulture, out version) && version > 0 &&
            value == $"\"{orderId:D}:{version.ToString(CultureInfo.InvariantCulture)}\"";
    }

    private static IResult Error(HttpContext http, int status, string code) => Results.Problem(
        statusCode: status, title: "Order request could not be accepted.",
        extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = http.TraceIdentifier });

    private static bool TryParsePlan(JsonElement json, out OrderPlan? plan)
    {
        plan = null;
        if (json.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in json.EnumerateObject())
        {
            if (!names.Add(property.Name) || property.Name is not ("customerId" or "plannedReceivingDate" or "plannedDeliveryDate" or "customerReference" or "notes")) return false;
        }
        if (!json.TryGetProperty("customerId", out var customer) || customer.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(customer.GetString(), "D", out var id) || id == Guid.Empty) return false;
        if (!Date(json, "plannedReceivingDate", out var receiving) || !Date(json, "plannedDeliveryDate", out var delivery)) return false;
        if (!OptionalText(json, "customerReference", out var reference) || !OptionalText(json, "notes", out var notes)) return false;
        plan = new OrderPlan(id, receiving, delivery, reference, notes);
        return true;
    }

    private static bool Date(JsonElement json, string name, out DateOnly date)
    {
        date = default;
        return json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool OptionalText(JsonElement json, string name, out string? value)
    {
        value = null;
        if (!json.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return true;
    }
}
