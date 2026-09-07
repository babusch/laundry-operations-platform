using System.Text;
using System.Text.Json;

namespace Laundry.Edge.Synchronization;

public sealed record DeliveryResult(string Status, string? Error = null,
    DateTimeOffset? CloudReceivedAtUtc = null, TimeSpan? RetryAfter = null);

public sealed class CloudDelivery(HttpClient client)
{
    public async Task<DeliveryResult> SendAsync(Uri endpoint, Guid eventId, string payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var code = (int)response.StatusCode;
            if (code is 200 or 201)
            {
                // Bound even chunked receipts; never trust status code alone as acknowledgement.
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var bytes = new byte[4097];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
                    if (read == 0) break;
                    count += read;
                }
                if (count > 4096) return new("pending", "invalid_receipt");
                using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
                var json = document.RootElement;
                if (json.ValueKind != JsonValueKind.Object || json.EnumerateObject().Select(x => x.Name).Distinct().Count() != json.EnumerateObject().Count() ||
                    !json.TryGetProperty("eventId", out var id) || !id.TryGetGuid(out var parsedId) || parsedId != eventId ||
                    !json.TryGetProperty("status", out var status) || status.GetString() != (code == 201 ? "accepted" : "alreadyProcessed") ||
                    !json.TryGetProperty("cloudReceivedAtUtc", out var received) || !received.TryGetDateTimeOffset(out var timestamp) ||
                    timestamp == default || timestamp.Offset != TimeSpan.Zero)
                    return new("pending", "invalid_receipt");
                return new("synchronized", CloudReceivedAtUtc: timestamp);
            }
            if (code is 408 or 429 || code >= 500)
            {
                var hint = response.Headers.RetryAfter?.Delta;
                if (hint is null && response.Headers.RetryAfter?.Date is { } date)
                    hint = date - DateTimeOffset.UtcNow;
                return new("pending", $"http_{code}", RetryAfter: hint);
            }
            // Do not repeatedly send permanent rejects, auth/config errors, or redirects.
            return new("needsAttention", $"http_{code}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("pending", "timeout");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new("pending", "connection_failed");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return new("pending", "invalid_receipt");
        }
    }
}
