using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Laundry.Edge.Tests;

internal sealed class EnrolledSourceClient(HttpClient client, Guid sourceId, string sourceCookie,
    string antiforgeryCookie, string antiforgeryHeader, string antiforgeryToken) : IDisposable
{
    public HttpClient Client { get; } = client;
    public Guid SourceId { get; } = sourceId;
    public string SourceCookie { get; } = sourceCookie;

    public static async Task<EnrolledSourceClient> CreateAsync(WebApplicationFactory<Program> application)
    {
        var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        try
        {
            using var bootstrap = await client.PostAsync("/api/development/source-enrollments", null,
                CancellationToken.None);
            if (bootstrap.StatusCode != HttpStatusCode.Created)
                throw new InvalidOperationException($"Source bootstrap failed with {bootstrap.StatusCode}.");
            var enrollment = await bootstrap.Content.ReadFromJsonAsync<EnrollmentResponse>(CancellationToken.None)
                ?? throw new InvalidOperationException("Source bootstrap returned no enrollment.");

            using var exchange = await client.PostAsJsonAsync("/api/source-enrollment/exchange",
                new { enrollmentCode = enrollment.EnrollmentCode }, CancellationToken.None);
            if (exchange.StatusCode != HttpStatusCode.NoContent)
                throw new InvalidOperationException($"Source enrollment failed with {exchange.StatusCode}.");
            var enrolledCookie = ReadCookie(exchange, "__Host-laundry-source");

            return await CreateSessionAsync(client, enrolledCookie);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task<EnrolledSourceClient> ResumeAsync(WebApplicationFactory<Program> application,
        string sourceCookie)
    {
        var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        try
        {
            return await CreateSessionAsync(client, sourceCookie);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task<HttpResponseMessage> PostScanAsync(JsonObject json, bool includeAntiforgery = true,
        string? antiforgeryOverride = null) =>
        SendScanAsync(new HttpRequestMessage(HttpMethod.Post, "/api/scans")
        {
            Content = JsonContent.Create(json)
        }, includeAntiforgery, antiforgeryOverride);

    public Task<HttpResponseMessage> SendScanAsync(HttpRequestMessage request, bool includeAntiforgery = true,
        string? antiforgeryOverride = null)
    {
        request.Headers.Add("Cookie", $"{SourceCookie}; {antiforgeryCookie}");
        if (includeAntiforgery)
            request.Headers.Add(antiforgeryHeader, antiforgeryOverride ?? antiforgeryToken);
        return Client.SendAsync(request, CancellationToken.None);
    }

    public void Dispose() => Client.Dispose();

    private static async Task<EnrolledSourceClient> CreateSessionAsync(HttpClient client, string sourceCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/source-session");
        request.Headers.Add("Cookie", sourceCookie);
        using var response = await client.SendAsync(request, CancellationToken.None);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Source session failed with {response.StatusCode}.");
        var session = await response.Content.ReadFromJsonAsync<SessionResponse>(CancellationToken.None)
            ?? throw new InvalidOperationException("Source session was unavailable.");
        return new EnrolledSourceClient(client, session.SourceId, sourceCookie,
            ReadCookie(response, "__Host-laundry-source-csrf"), session.AntiforgeryHeaderName,
            session.AntiforgeryToken);
    }

    private static string ReadCookie(HttpResponseMessage response, string name)
    {
        var header = response.Headers.GetValues("Set-Cookie").Single(value =>
            value.StartsWith(name + "=", StringComparison.Ordinal));
        var separator = header.IndexOf(';');
        return separator < 0 ? header : header[..separator];
    }

    private sealed record EnrollmentResponse(Guid SourceId, string EnrollmentCode, DateTimeOffset ExpiresAtUtc);
    private sealed record SessionResponse(Guid SourceId, Guid StationId, string[] Permissions,
        string AntiforgeryToken, string AntiforgeryHeaderName);
}
