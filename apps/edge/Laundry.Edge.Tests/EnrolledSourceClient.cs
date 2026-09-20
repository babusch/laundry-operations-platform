using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Laundry.Edge.Tests;

internal sealed class EnrolledSourceClient(HttpClient client, Guid sourceId, string antiforgeryHeader,
    string antiforgeryToken) : IDisposable
{
    public HttpClient Client { get; } = client;
    public Guid SourceId { get; } = sourceId;

    public static async Task<EnrolledSourceClient> CreateAsync(WebApplicationFactory<Program> application)
    {
        var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
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

            var session = await client.GetFromJsonAsync<SessionResponse>("/api/source-session",
                CancellationToken.None) ?? throw new InvalidOperationException("Source session was unavailable.");
            return new EnrolledSourceClient(client, session.SourceId, session.AntiforgeryHeaderName,
                session.AntiforgeryToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task<HttpResponseMessage> PostScanAsync(JsonObject json, bool includeAntiforgery = true) =>
        SendScanAsync(new HttpRequestMessage(HttpMethod.Post, "/api/scans")
        {
            Content = JsonContent.Create(json)
        }, includeAntiforgery);

    public Task<HttpResponseMessage> SendScanAsync(HttpRequestMessage request, bool includeAntiforgery = true)
    {
        if (includeAntiforgery) request.Headers.Add(antiforgeryHeader, antiforgeryToken);
        return Client.SendAsync(request, CancellationToken.None);
    }

    public void Dispose() => Client.Dispose();

    private sealed record EnrollmentResponse(Guid SourceId, string EnrollmentCode, DateTimeOffset ExpiresAtUtc);
    private sealed record SessionResponse(Guid SourceId, Guid StationId, string[] Permissions,
        string AntiforgeryToken, string AntiforgeryHeaderName);
}
