using System.Net;
using System.Text.Json;

namespace Laundry.Edge.Synchronization;

public interface IGatewayTokenProvider
{
    Task<GatewayTokenResult> GetAsync(CancellationToken cancellationToken);
    void Invalidate(string accessToken);
}

public sealed class GatewayTokenResult
{
    private GatewayTokenResult(string? accessToken, string? error)
    {
        AccessToken = accessToken;
        Error = error;
    }

    public string? AccessToken { get; }
    public string? Error { get; }
    public bool Succeeded => AccessToken is not null;

    public static GatewayTokenResult Success(string accessToken) => new(accessToken, null);
    public static GatewayTokenResult Failure(string error) => new(null, error);
}

public sealed class GatewayTokenProvider(HttpClient client, IConfiguration configuration, TimeProvider clock)
    : IGatewayTokenProvider, IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _refreshAtUtc;

    public async Task<GatewayTokenResult> GetAsync(CancellationToken cancellationToken)
    {
        if (CurrentToken() is { } current) return GatewayTokenResult.Success(current);

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (CurrentToken() is { } refreshed) return GatewayTokenResult.Success(refreshed);
            return await RequestAsync(cancellationToken);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Invalidate(string accessToken)
    {
        if (string.Equals(Volatile.Read(ref _cachedToken), accessToken, StringComparison.Ordinal))
            Volatile.Write(ref _cachedToken, null);
    }

    private string? CurrentToken()
    {
        var token = Volatile.Read(ref _cachedToken);
        return token is not null && clock.GetUtcNow() < _refreshAtUtc ? token : null;
    }

    private async Task<GatewayTokenResult> RequestAsync(CancellationToken cancellationToken)
    {
        GatewayIdentitySettings settings;
        try
        {
            settings = GatewayIdentitySettings.Read(configuration);
        }
        catch (InvalidOperationException)
        {
            return GatewayTokenResult.Failure("identity_configuration_invalid");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = settings.ClientId,
                    ["client_secret"] = settings.ClientSecret
                })
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
                return GatewayTokenResult.Failure(Classify(response.StatusCode));

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[65537];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
                if (read == 0) break;
                count += read;
            }
            if (count > 65536) return GatewayTokenResult.Failure("identity_response_invalid");

            using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object ||
                !json.TryGetProperty("access_token", out var tokenElement) ||
                tokenElement.GetString() is not { Length: > 0 } token || token.Length > 32768 ||
                token.Any(char.IsWhiteSpace) || token.Any(char.IsControl) ||
                !json.TryGetProperty("token_type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) ||
                !json.TryGetProperty("expires_in", out var expiresElement) ||
                !expiresElement.TryGetInt32(out var expiresSeconds) || expiresSeconds <= 0)
                return GatewayTokenResult.Failure("identity_response_invalid");

            var boundedLifetime = Math.Min(expiresSeconds, 86400);
            var refreshSeconds = Math.Max(0, boundedLifetime - Math.Min(30, Math.Max(1, boundedLifetime / 5)));
            _refreshAtUtc = clock.GetUtcNow().AddSeconds(refreshSeconds);
            Volatile.Write(ref _cachedToken, token);
            return GatewayTokenResult.Success(token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GatewayTokenResult.Failure("identity_timeout");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return GatewayTokenResult.Failure("identity_connection_failed");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return GatewayTokenResult.Failure("identity_response_invalid");
        }
    }

    private static string Classify(HttpStatusCode statusCode) => (int)statusCode switch
    {
        400 or 401 or 403 => "identity_credentials_rejected",
        408 => "identity_timeout",
        429 => "identity_rate_limited",
        >= 500 => "identity_unavailable",
        _ => "identity_response_invalid"
    };

    public void Dispose()
    {
        client.Dispose();
        _refreshLock.Dispose();
    }
}

public sealed class GatewayIdentitySettings
{
    private GatewayIdentitySettings(Uri tokenEndpoint, string clientId, string clientSecret)
    {
        TokenEndpoint = tokenEndpoint;
        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    public Uri TokenEndpoint { get; }
    public string ClientId { get; }
    public string ClientSecret { get; }

    public static GatewayIdentitySettings Read(IConfiguration configuration)
    {
        if (!Uri.TryCreate(configuration["GatewayIdentity:TokenEndpoint"], UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || !IsLoopback(endpoint) || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            !endpoint.AbsolutePath.StartsWith("/realms/", StringComparison.Ordinal) ||
            !endpoint.AbsolutePath.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
            throw new InvalidOperationException("Gateway identity requires a local HTTPS OpenID Connect token endpoint.");

        var clientId = configuration["GatewayIdentity:ClientId"];
        var clientSecret = configuration["GatewayIdentity:ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 200 || clientId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(clientSecret) || clientSecret.Length > 4096)
            throw new InvalidOperationException("Configure the development gateway client ID and private client secret.");

        return new(endpoint, clientId, clientSecret);
    }

    private static bool IsLoopback(Uri endpoint) =>
        string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address);
}
