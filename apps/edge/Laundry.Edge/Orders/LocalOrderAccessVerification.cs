using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Laundry.Edge.Persistence;
using Laundry.Edge.Security;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Edge.Orders;

public sealed record LocalOrderVerificationResult(Guid RunId, bool Passed, bool CleanupComplete, int OrdersRead);

// A local setup verifier, not an administrative endpoint or a general permission-management service.
public sealed class LocalOrderAccessVerification(DbContextOptions<PlantDbContext> options, OrderDownloadScope scope, HttpClient client)
{
    private Guid? _ownedSource;
    private Guid? _ownedEnrollment;
    private bool _enrollmentAttempted;
    private readonly Guid _runId = Guid.NewGuid();
    private const string Reason = "local-order-list-verification";
    private sealed record Enrollment(Guid SourceId, string EnrollmentCode);

    public async Task<LocalOrderVerificationResult> RunAsync(CancellationToken ct = default)
    {
        var passed = false; var cleaned = false; var orders = 0;
        try
        {
            await CheckAsync(HttpStatusCode.Unauthorized, ct);
            _enrollmentAttempted = true;
            using var created = await client.PostAsync("/api/development/source-enrollments", null, ct);
            Require(created, HttpStatusCode.Created);
            var enrollment = await created.Content.ReadFromJsonAsync<Enrollment>(ct) ?? throw new InvalidOperationException();
            // Claim only the just-created, pending enrollment whose one-time code we possess.
            await ClaimAsync(enrollment, ct);
            using var exchanged = await client.PostAsJsonAsync("/api/source-enrollment/exchange", new { enrollment.EnrollmentCode }, ct);
            Require(exchanged, HttpStatusCode.NoContent);
            await CheckAsync(HttpStatusCode.Forbidden, ct);
            await PermissionAsync(true, ct);
            string? cursor = null; var seen = new HashSet<Guid>();
            for (var page = 0; ; page++)
            {
                if (page == 100) throw new InvalidOperationException();
                var uri = "/api/order-plans?limit=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
                using var response = await client.GetAsync(uri, ct);
                Require(response, HttpStatusCode.OK);
                if (response.Headers.CacheControl?.NoStore != true) throw new InvalidOperationException();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = json.RootElement;
                if (root.EnumerateObject().Count() != 4 || !root.TryGetProperty("items", out var items) ||
                    items.GetArrayLength() > 1 || !root.TryGetProperty("lastSuccessfulPageAtUtc", out _)) throw new InvalidOperationException();
                foreach (var item in items.EnumerateArray())
                {
                    var allowed = new[] { "id", "orderNumber", "customer", "plannedReceivingDate", "plannedDeliveryDate", "status", "version" };
                    if (item.EnumerateObject().Count() != allowed.Length || item.EnumerateObject().Any(p => !allowed.Contains(p.Name)) ||
                        !seen.Add(item.GetProperty("id").GetGuid())) throw new InvalidOperationException();
                    var customer = item.GetProperty("customer");
                    if (customer.EnumerateObject().Count() != 3 || customer.EnumerateObject().Any(p => p.Name is not ("id" or "code" or "displayName")))
                        throw new InvalidOperationException();
                    orders++;
                }
                if (!root.GetProperty("hasMore").GetBoolean())
                {
                    if (root.GetProperty("nextCursor").ValueKind != JsonValueKind.Null) throw new InvalidOperationException();
                    break;
                }
                var next = root.GetProperty("nextCursor").GetString();
                if (string.IsNullOrWhiteSpace(next) || next == cursor || items.GetArrayLength() == 0) throw new InvalidOperationException();
                cursor = next;
            }
            await using (var db = new PlantDbContext(options))
            {
                var expected = await db.LocalOrderPlans.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.PlantId == scope.PlantId).Select(x => x.OrderId).ToArrayAsync(ct);
                if (!seen.SetEquals(expected)) throw new InvalidOperationException();
            }
            await PermissionAsync(false, ct);
            await CheckAsync(HttpStatusCode.Forbidden, ct);
            passed = true;
        }
        catch (Exception) { passed = false; } // Never expose HTTP bodies, cookies, order text or database errors.
        finally
        {
            // Cleanup has its own deadline even after cancellation of the verification.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await CleanupAsync(cleanup.Token);
                if (_ownedSource is not null) await CheckAsync(HttpStatusCode.Unauthorized, cleanup.Token);
                // A lost create response may leave an unknown pending enrollment. Never claim it was revoked.
                cleaned = _ownedSource is not null || !_enrollmentAttempted;
            }
            catch (Exception) { cleaned = false; }
        }
        return new(_runId, passed && cleaned, cleaned, orders);
    }

    private async Task ClaimAsync(Enrollment enrollment, CancellationToken ct)
    {
        if (!SourceSecretTokens.TryDigest(enrollment.EnrollmentCode, out var digest)) throw new InvalidOperationException();
        await using var db = new PlantDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var source = await db.TrustedSources.SingleAsync(x => x.SourceId == enrollment.SourceId && x.TenantId == scope.TenantId &&
            x.PlantId == scope.PlantId && x.Kind == TrustedSourceKind.BrowserStation && x.Status == TrustedSourceStatus.Pending, ct);
        var code = await db.SourceEnrollmentCodes.SingleAsync(x => x.SourceId == source.SourceId && x.CodeDigest == digest &&
            x.RedeemedAtUtc == null && x.ExpiresAtUtc > DateTimeOffset.UtcNow, ct);
        // Set before the uncertain commit so finally can reconcile an acknowledged-or-not claim.
        _ownedSource = source.SourceId; _ownedEnrollment = code.EnrollmentCodeId;
        db.SourceSecurityAudits.Add(Audit(source.SourceId, SourceSecurityAction.VerificationStarted));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    private async Task PermissionAsync(bool grant, CancellationToken ct)
    {
        await using var db = new PlantDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var source = await OwnedAsync(db, ct);
        if (grant && source.Status != TrustedSourceStatus.Active) throw new InvalidOperationException();
        var permission = await db.SourcePermissions.SingleOrDefaultAsync(x => x.SourceId == source.SourceId && x.Permission == SourcePermissions.ReadOrders, ct);
        if ((permission is not null) != grant)
        {
            if (grant) db.SourcePermissions.Add(new() { SourceId = source.SourceId, Permission = SourcePermissions.ReadOrders });
            else db.SourcePermissions.Remove(permission!);
            source.ConfigurationVersion++; source.StatusChangedAtUtc = DateTimeOffset.UtcNow;
            db.SourceSecurityAudits.Add(Audit(source.SourceId, grant ? SourceSecurityAction.PermissionGranted : SourceSecurityAction.PermissionRevoked));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private async Task<TrustedSource> OwnedAsync(PlantDbContext db, CancellationToken ct)
    {
        if (_ownedSource is null || _ownedEnrollment is null) throw new InvalidOperationException();
        // Exchange locks the enrollment before activating its source. Use the same lock order so
        // an in-flight exchange cannot reactivate a source after cleanup revoked it.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT enrollment_code_id FROM plant.source_enrollment_codes WHERE enrollment_code_id={_ownedEnrollment.Value} FOR UPDATE", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT source_id FROM plant.trusted_sources WHERE source_id={_ownedSource.Value} FOR UPDATE", ct);
        var source = await db.TrustedSources.SingleAsync(x => x.SourceId == _ownedSource && x.TenantId == scope.TenantId &&
            x.PlantId == scope.PlantId && x.Kind == TrustedSourceKind.BrowserStation, ct);
        // Ownership never comes from a command argument or a search for a convenient existing station.
        if (!await db.SourceEnrollmentCodes.AnyAsync(x => x.EnrollmentCodeId == _ownedEnrollment && x.SourceId == source.SourceId, ct))
            throw new InvalidOperationException();
        return source;
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        if (_ownedSource is null) return;
        await using var db = new PlantDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var source = await OwnedAsync(db, ct);
        var permission = await db.SourcePermissions.SingleOrDefaultAsync(x => x.SourceId == source.SourceId && x.Permission == SourcePermissions.ReadOrders, ct);
        if (permission is not null)
        {
            db.SourcePermissions.Remove(permission);
            db.SourceSecurityAudits.Add(Audit(source.SourceId, SourceSecurityAction.PermissionRevoked));
        }
        foreach (var credential in await db.SourceCredentials.Where(x => x.SourceId == source.SourceId && x.Status == SourceCredentialStatus.Active).ToArrayAsync(ct))
        {
            credential.Status = SourceCredentialStatus.Revoked; credential.RevokedAtUtc = DateTimeOffset.UtcNow;
            db.SourceSecurityAudits.Add(Audit(source.SourceId, SourceSecurityAction.CredentialRevoked, credential.CredentialId));
        }
        // An unexchanged code remains as evidence but cannot enroll a revoked source.
        if (source.Status != TrustedSourceStatus.Revoked)
        {
            source.Status = TrustedSourceStatus.Revoked; source.StatusChangedAtUtc = DateTimeOffset.UtcNow; source.ConfigurationVersion++;
            db.SourceSecurityAudits.Add(Audit(source.SourceId, SourceSecurityAction.SourceRevoked));
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    private SourceSecurityAudit Audit(Guid sourceId, SourceSecurityAction action, Guid? credentialId = null) => new()
    {
        AuditId = Guid.NewGuid(),
        SourceId = sourceId,
        CredentialId = credentialId,
        TenantId = scope.TenantId,
        PlantId = scope.PlantId,
        Action = action,
        Outcome = SourceSecurityOutcome.Succeeded,
        ActorType = "development-system",
        ActorId = "local-order-access-verification:" + _runId.ToString("D"),
        ReasonCode = Reason,
        CorrelationId = _runId,
        OccurredAtUtc = DateTimeOffset.UtcNow
    };

    private static void Require(HttpResponseMessage response, HttpStatusCode status)
    {
        if (response.StatusCode != status) throw new InvalidOperationException();
    }

    private async Task CheckAsync(HttpStatusCode status, CancellationToken ct)
    {
        using var response = await client.GetAsync("/api/order-plans", ct);
        Require(response, status);
    }
}
