using System.Data.Common;
using System.Net;
using System.Text.Json;
using Laundry.Edge.Orders;
using Laundry.Edge.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Edge.Textiles;

public static class DevelopmentItemLookupEndpoint
{
    public static void MapDevelopmentItemLookup(this WebApplication app) =>
        app.MapPost("/api/development/item-lookup", LookupAsync).WithName("LookupDevelopmentItem")
            .Produces<DevelopmentItemLookupResult>();

    public static void MapDevelopmentScanPreview(this WebApplication app) =>
        app.MapPost("/api/development/scan-preview", PreviewAsync).WithName("PreviewDevelopmentScan")
            .Produces<DevelopmentScanPreviewResult>();

    private static Task<IResult> LookupAsync(HttpContext http, BrowserCookieSourceAuthenticator authenticator,
        IAntiforgery antiforgery, ILoggerFactory logs, CancellationToken ct) => HandleAsync(http, authenticator, antiforgery, logs, ct, null);

    private static Task<IResult> PreviewAsync(HttpContext http, BrowserCookieSourceAuthenticator authenticator,
        IAntiforgery antiforgery, LocalOrderList orders, ILoggerFactory logs, CancellationToken ct) => HandleAsync(http, authenticator, antiforgery, logs, ct, orders);

    private static async Task<IResult> HandleAsync(HttpContext http, BrowserCookieSourceAuthenticator authenticator,
        IAntiforgery antiforgery, ILoggerFactory logs, CancellationToken ct, LocalOrderList? orders)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Vary = "Cookie";
        IResult Error(int status, string code) => Results.Json(new { code }, statusCode: status);
        if (!http.Request.IsHttps || http.Request.Host != new HostString("localhost", 7200) ||
            http.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
            (http.Request.Headers.TryGetValue("Origin", out var origin) && origin.ToString() != "https://localhost:7200") ||
            (http.Request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site.ToString() is not ("same-origin" or "none")))
            return Error(403, "development_item_origin_forbidden");
        try
        {
            var source = await authenticator.AuthenticateAsync(http, ct);
            if (source is null) return Error(401, "development_item_source_required");
            if (source.PrincipalType != TrustedSourceKind.BrowserStation || !source.Permissions.Contains(SourcePermissions.SubmitScans))
                return Error(403, "development_item_permission_required");
            if (orders is not null && !source.Permissions.Contains(SourcePermissions.ReadOrders))
                return Error(403, "development_item_permission_required");
            var scope = new SourceScope(source.TenantId, source.PlantId);
            if (!DevelopmentItemCatalog.Supports(scope)) return Error(403, "development_item_fixture_scope_required");
            try { await antiforgery.ValidateRequestAsync(http); }
            catch (AntiforgeryValidationException) { return Error(400, "development_item_antiforgery_required"); }
            if (http.Request.Query.Count != 0) return Error(400, "invalid_development_item_request");
            if (!http.Request.HasJsonContentType()) return Error(415, "development_item_json_required");
            // Do not put identifiers in URLs/logs, or allow unbounded reads, including chunked bodies.
            const int maximum = 1024;
            if (http.Request.ContentLength > maximum) return Error(413, "development_item_request_too_large");
            var bytes = new byte[maximum + 1]; var length = 0;
            while (length < bytes.Length)
            {
                var read = await http.Request.Body.ReadAsync(bytes.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }
            if (length > maximum) return Error(413, "development_item_request_too_large");
            string? value;
            try
            {
                using var json = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
                var root = json.RootElement;
                if (!Exact(root, "identifier")) return Error(400, "invalid_development_item_request");
                var identifier = root.GetProperty("identifier");
                if (!Exact(identifier, "technology", "value") || identifier.GetProperty("technology").ValueKind != JsonValueKind.String ||
                    identifier.GetProperty("technology").GetString() != "rfid" || identifier.GetProperty("value").ValueKind != JsonValueKind.String)
                    return Error(400, "invalid_development_item_request");
                value = identifier.GetProperty("value").GetString();
                if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value != value.Trim() || value.Any(char.IsControl))
                    return Error(400, "invalid_development_item_request");
            }
            catch (JsonException) { return Error(400, "invalid_development_item_request"); }
            var lookup = DevelopmentItemCatalog.Lookup(scope, value);
            if (orders is null) return Results.Ok(lookup);
            return Results.Ok(await DevelopmentScanPreview.ResolveAsync(lookup,
                (cursor, token) => orders.ReadAsync(new(scope.TenantId, scope.PlantId), 50, cursor, token), ct));
        }
        catch (LocalOrderListException exception)
        {
            return Error(exception.Code == "local_order_list_changed" ? 409 : 503, exception.Code);
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or TimeoutException ||
            (exception is InvalidOperationException && exception.InnerException is DbException))
        {
            logs.CreateLogger("DevelopmentItemLookup").LogWarning("Development lookup identity storage unavailable. Correlation {CorrelationId}", http.TraceIdentifier);
            http.Response.Headers.RetryAfter = "5";
            return Error(503, "development_item_storage_unavailable");
        }
    }

    private static bool Exact(JsonElement value, params string[] names) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Count() == names.Length && names.All(name => value.TryGetProperty(name, out _));
}
