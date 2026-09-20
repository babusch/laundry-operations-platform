using System.Net;
using System.Text.Json;
using Laundry.Edge.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Edge.Security;

public static class SourceEnrollmentEndpoints
{
    public const string SourceCookieName = "__Host-laundry-source";
    public const string AntiforgeryCookieName = "__Host-laundry-source-csrf";
    public const string AntiforgeryHeaderName = "X-Laundry-CSRF";

    private static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DevelopmentCredentialLifetime = TimeSpan.FromDays(30);

    public static void MapSourceEnrollment(this WebApplication app, SourceScope gatewayScope, Guid stationId)
    {
        app.MapPost("/api/development/source-enrollments", (HttpContext http, PlantDbContext database,
            TimeProvider clock, CancellationToken cancellationToken) =>
            CreateEnrollmentAsync(http, database, clock, gatewayScope, stationId, cancellationToken));

        app.MapPost("/api/source-enrollment/exchange", (HttpContext http, PlantDbContext database,
            TimeProvider clock, CancellationToken cancellationToken) =>
            ExchangeAsync(http, database, clock, cancellationToken));

        app.MapGet("/api/source-session", (HttpContext http, BrowserCookieSourceAuthenticator authenticator,
            IAntiforgery antiforgery, CancellationToken cancellationToken) =>
            GetSessionAsync(http, authenticator, antiforgery, cancellationToken));

        app.MapPost("/api/development/source-session/verify", (HttpContext http,
            BrowserCookieSourceAuthenticator authenticator, IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
            VerifySessionAsync(http, authenticator, antiforgery, cancellationToken));
    }

    private static async Task<IResult> CreateEnrollmentAsync(HttpContext http, PlantDbContext database,
        TimeProvider clock, SourceScope gatewayScope, Guid stationId, CancellationToken cancellationToken)
    {
        if (!IsLoopbackHttps(http))
            return Results.Problem(statusCode: 403, title: "Development enrollment requires local HTTPS.");

        var now = clock.GetUtcNow().ToUniversalTime();
        var expiresAt = now.Add(EnrollmentLifetime);
        var sourceId = Guid.NewGuid();
        var enrollmentId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var (code, digest) = SourceSecretTokens.Create();

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        database.TrustedSources.Add(new TrustedSource
        {
            SourceId = sourceId,
            TenantId = gatewayScope.TenantId,
            PlantId = gatewayScope.PlantId,
            StationId = stationId,
            Kind = TrustedSourceKind.BrowserStation,
            Status = TrustedSourceStatus.Pending,
            ConfigurationVersion = 1,
            CreatedAtUtc = now,
            StatusChangedAtUtc = now
        });
        database.SourcePermissions.Add(new SourcePermission
        {
            SourceId = sourceId,
            Permission = SourcePermissions.SubmitScans
        });
        database.SourceEnrollmentCodes.Add(new SourceEnrollmentCode
        {
            EnrollmentCodeId = enrollmentId,
            SourceId = sourceId,
            CodeDigest = digest,
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAt
        });
        database.SourceSecurityAudits.AddRange(
            DevelopmentAudit(sourceId, gatewayScope, SourceSecurityAction.SourceCreated, correlationId, now),
            DevelopmentAudit(sourceId, gatewayScope, SourceSecurityAction.EnrollmentCodeCreated, correlationId, now));
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        NoStore(http.Response);
        return Results.Json(new EnrollmentCreated(sourceId, code, expiresAt), statusCode: 201);
    }

    private static async Task<IResult> ExchangeAsync(HttpContext http, PlantDbContext database,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!http.Request.IsHttps)
            return Results.Problem(statusCode: 403, title: "Enrollment requires HTTPS.");

        var code = await ReadEnrollmentCodeAsync(http.Request, cancellationToken);
        if (code is null || !SourceSecretTokens.TryDigest(code, out var codeDigest)) return InvalidCode();

        var now = clock.GetUtcNow().ToUniversalTime();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var enrollment = await database.SourceEnrollmentCodes.AsNoTracking().SingleOrDefaultAsync(x =>
            x.CodeDigest == codeDigest && x.RedeemedAtUtc == null && x.ExpiresAtUtc > now, cancellationToken);
        if (enrollment is null) return InvalidCode();

        var consumed = await database.SourceEnrollmentCodes
            .Where(x => x.EnrollmentCodeId == enrollment.EnrollmentCodeId && x.RedeemedAtUtc == null &&
                        x.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RedeemedAtUtc, now), cancellationToken);
        if (consumed != 1) return InvalidCode();

        var source = await database.TrustedSources.SingleOrDefaultAsync(x =>
            x.SourceId == enrollment.SourceId && x.Status == TrustedSourceStatus.Pending, cancellationToken);
        if (source is null) return InvalidCode();

        var credentialId = Guid.NewGuid();
        var credentialExpiresAt = now.Add(DevelopmentCredentialLifetime);
        var (secret, verifierDigest) = SourceSecretTokens.Create();
        database.SourceCredentials.Add(new SourceCredential
        {
            CredentialId = credentialId,
            SourceId = source.SourceId,
            Kind = SourceCredentialKind.BrowserSecret,
            VerifierDigest = verifierDigest,
            Status = SourceCredentialStatus.Active,
            IssuedAtUtc = now,
            ExpiresAtUtc = credentialExpiresAt
        });
        source.Status = TrustedSourceStatus.Active;
        source.StatusChangedAtUtc = now;
        source.ConfigurationVersion++;
        database.SourceSecurityAudits.Add(new SourceSecurityAudit
        {
            AuditId = Guid.NewGuid(),
            SourceId = source.SourceId,
            CredentialId = credentialId,
            TenantId = source.TenantId,
            PlantId = source.PlantId,
            Action = SourceSecurityAction.EnrollmentCompleted,
            Outcome = SourceSecurityOutcome.Succeeded,
            ActorType = "enrollment-code",
            ActorId = enrollment.EnrollmentCodeId.ToString("N"),
            CorrelationId = Guid.NewGuid(),
            OccurredAtUtc = now
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        http.Response.Cookies.Append(SourceCookieName, $"{credentialId:N}.{secret}", new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/",
            Expires = credentialExpiresAt,
            MaxAge = DevelopmentCredentialLifetime
        });
        NoStore(http.Response);
        return Results.NoContent();
    }

    private static async Task<IResult> GetSessionAsync(HttpContext http,
        BrowserCookieSourceAuthenticator authenticator, IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (!http.Request.IsHttps) return Results.StatusCode(403);
        var identity = await authenticator.AuthenticateAsync(http, cancellationToken);
        if (identity is null) return Results.Unauthorized();

        var tokens = antiforgery.GetAndStoreTokens(http);
        NoStore(http.Response);
        return Results.Json(new SourceSession(identity.SourceId, identity.StationId,
            identity.Permissions.Order(StringComparer.Ordinal).ToArray(), tokens.RequestToken!,
            AntiforgeryHeaderName));
    }

    private static async Task<IResult> VerifySessionAsync(HttpContext http,
        BrowserCookieSourceAuthenticator authenticator, IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (!http.Request.IsHttps) return Results.StatusCode(403);
        if (await authenticator.AuthenticateAsync(http, cancellationToken) is null)
            return Results.Unauthorized();
        if (!await antiforgery.IsRequestValidAsync(http))
            return Results.Problem(statusCode: 400, title: "A valid antiforgery token is required.");
        return Results.NoContent();
    }

    private static async Task<string?> ReadEnrollmentCodeAsync(HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType() || request.ContentLength is > 2048) return null;
        var buffer = new byte[2049];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        if (count == 0 || count > 2048) return null;

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count),
                new JsonDocumentOptions { MaxDepth = 3 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("enrollmentCode", out var property) ||
                property.ValueKind != JsonValueKind.String)
                return null;
            var code = property.GetString();
            return code is { Length: > 0 and <= 128 } ? code : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SourceSecurityAudit DevelopmentAudit(Guid sourceId, SourceScope scope,
        SourceSecurityAction action, Guid correlationId, DateTimeOffset occurredAtUtc) => new()
    {
        AuditId = Guid.NewGuid(),
        SourceId = sourceId,
        TenantId = scope.TenantId,
        PlantId = scope.PlantId,
        Action = action,
        Outcome = SourceSecurityOutcome.Succeeded,
        ActorType = "development-system",
        ActorId = "local-development-bootstrap",
        CorrelationId = correlationId,
        OccurredAtUtc = occurredAtUtc
    };

    private static IResult InvalidCode() =>
        Results.Problem(statusCode: 400, title: "The enrollment code is invalid or unavailable.");

    private static bool IsLoopbackHttps(HttpContext http) => http.Request.IsHttps &&
        http.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    private static void NoStore(HttpResponse response) => response.Headers.CacheControl = "no-store";

    private sealed record EnrollmentCreated(Guid SourceId, string EnrollmentCode, DateTimeOffset ExpiresAtUtc);

    private sealed record SourceSession(Guid SourceId, Guid StationId, string[] Permissions,
        string AntiforgeryToken, string AntiforgeryHeaderName);
}
