using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Laundry.Edge.Persistence;
using Laundry.Edge.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Laundry.Edge.Tests;

public sealed class SourceEnrollmentTests(AcceptanceFixture fixture) : IClassFixture<AcceptanceFixture>
{
    [Fact]
    public async Task EnrollmentCreatesSecureCookieAndAntiforgeryProtectedSession()
    {
        using var client = CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/source-session", CancellationToken.None)).StatusCode);

        var enrollment = await BootstrapAsync(client);
        var exchange = await ExchangeAsync(client, enrollment.EnrollmentCode);

        Assert.Equal(HttpStatusCode.NoContent, exchange.StatusCode);
        var sourceCookie = Assert.Single(exchange.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith(SourceEnrollmentEndpoints.SourceCookieName + "=", StringComparison.Ordinal));
        Assert.Contains("secure", sourceCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sourceCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", sourceCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", sourceCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", sourceCookie, StringComparison.OrdinalIgnoreCase);

        var session = await client.GetFromJsonAsync<SessionResponse>("/api/source-session",
            CancellationToken.None);
        Assert.NotNull(session);
        Assert.Equal(enrollment.SourceId, session.SourceId);
        Assert.Contains(SourcePermissions.SubmitScans, session.Permissions);
        Assert.Equal(SourceEnrollmentEndpoints.AntiforgeryHeaderName, session.AntiforgeryHeaderName);

        var missingToken = await client.PostAsync("/api/development/source-session/verify", null,
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

        using var protectedRequest = new HttpRequestMessage(HttpMethod.Post,
            "/api/development/source-session/verify");
        protectedRequest.Headers.Add(session.AntiforgeryHeaderName, session.AntiforgeryToken);
        var verified = await client.SendAsync(protectedRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);

        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        var source = await database.TrustedSources.SingleAsync(x => x.SourceId == enrollment.SourceId,
            CancellationToken.None);
        Assert.Equal(TrustedSourceStatus.Active, source.Status);
        Assert.Equal(2, source.ConfigurationVersion);
        Assert.Equal(1, await database.SourceCredentials.CountAsync(x => x.SourceId == source.SourceId,
            CancellationToken.None));
        Assert.Equal(1, await database.SourceSecurityAudits.CountAsync(x =>
            x.SourceId == source.SourceId && x.Action == SourceSecurityAction.EnrollmentCompleted,
            CancellationToken.None));
    }

    [Fact]
    public async Task EnrollmentCodeIsSingleUseEvenUnderConcurrency()
    {
        using var bootstrapClient = CreateHttpsClient();
        var enrollment = await BootstrapAsync(bootstrapClient);
        using var first = CreateHttpsClient();
        using var second = CreateHttpsClient();

        var responses = await Task.WhenAll(
            ExchangeAsync(first, enrollment.EnrollmentCode),
            ExchangeAsync(second, enrollment.EnrollmentCode));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.BadRequest);

        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        Assert.Equal(1, await database.SourceCredentials.CountAsync(x => x.SourceId == enrollment.SourceId,
            CancellationToken.None));
        Assert.NotNull((await database.SourceEnrollmentCodes.SingleAsync(x =>
            x.SourceId == enrollment.SourceId, CancellationToken.None)).RedeemedAtUtc);
    }

    [Fact]
    public async Task MissingInvalidExpiredAndReusedCodesAreRejected()
    {
        using var client = CreateHttpsClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/source-enrollment/exchange", new { },
                CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await ExchangeAsync(client, "not-a-token")).StatusCode);

        var expiredCode = await SeedExpiredCodeAsync();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await ExchangeAsync(client, expiredCode)).StatusCode);

        var enrollment = await BootstrapAsync(client);
        Assert.Equal(HttpStatusCode.NoContent,
            (await ExchangeAsync(client, enrollment.EnrollmentCode)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await ExchangeAsync(client, enrollment.EnrollmentCode)).StatusCode);
    }

    [Fact]
    public async Task EnrollmentAndSessionRejectInsecureHttp()
    {
        using var https = CreateHttpsClient();
        var enrollment = await BootstrapAsync(https);
        using var http = fixture.Application.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });

        Assert.Equal(HttpStatusCode.Forbidden,
            (await ExchangeAsync(http, enrollment.EnrollmentCode)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await http.GetAsync("/api/source-session", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task DevelopmentBootstrapRejectsNonLoopbackCaller()
    {
        await using var remoteApplication = fixture.CreateApplication(address: "192.0.2.10");
        using var client = remoteApplication.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        var response = await client.PostAsync("/api/development/source-enrollments", null,
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TamperedBrowserCookieDoesNotAuthenticate()
    {
        using var client = CreateHttpsClient(handleCookies: false);
        var enrollment = await BootstrapAsync(client);
        var exchange = await ExchangeAsync(client, enrollment.EnrollmentCode);
        var setCookie = Assert.Single(exchange.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith(SourceEnrollmentEndpoints.SourceCookieName + "=", StringComparison.Ordinal));
        var cookieValue = setCookie[(setCookie.IndexOf('=') + 1)..setCookie.IndexOf(';')];
        var separator = cookieValue.IndexOf('.');
        var tampered = cookieValue[..(separator + 1)] + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/source-session");
        request.Headers.Add("Cookie", $"{SourceEnrollmentEndpoints.SourceCookieName}={tampered}");
        var response = await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private HttpClient CreateHttpsClient(bool handleCookies = true) => fixture.Application.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = handleCookies
        });

    private static async Task<EnrollmentResponse> BootstrapAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/development/source-enrollments", null,
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var enrollment = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(CancellationToken.None);
        return Assert.IsType<EnrollmentResponse>(enrollment);
    }

    private static Task<HttpResponseMessage> ExchangeAsync(HttpClient client, string code) =>
        client.PostAsJsonAsync("/api/source-enrollment/exchange", new { enrollmentCode = code },
            CancellationToken.None);

    private async Task<string> SeedExpiredCodeAsync()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var code = WebEncoders.Base64UrlEncode(bytes);
        var now = DateTimeOffset.UtcNow;
        var source = new TrustedSource
        {
            SourceId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            PlantId = Guid.NewGuid(),
            StationId = Guid.NewGuid(),
            Kind = TrustedSourceKind.BrowserStation,
            Status = TrustedSourceStatus.Pending,
            ConfigurationVersion = 1,
            CreatedAtUtc = now.AddHours(-1),
            StatusChangedAtUtc = now.AddHours(-1)
        };
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        database.TrustedSources.Add(source);
        database.SourceEnrollmentCodes.Add(new SourceEnrollmentCode
        {
            EnrollmentCodeId = Guid.NewGuid(),
            SourceId = source.SourceId,
            CodeDigest = SHA256.HashData(bytes),
            CreatedAtUtc = now.AddMinutes(-20),
            ExpiresAtUtc = now.AddMinutes(-10)
        });
        await database.SaveChangesAsync(CancellationToken.None);
        return code;
    }

    private sealed record EnrollmentResponse(Guid SourceId, string EnrollmentCode, DateTimeOffset ExpiresAtUtc);

    private sealed record SessionResponse(Guid SourceId, Guid StationId, string[] Permissions,
        string AntiforgeryToken, string AntiforgeryHeaderName);
}
