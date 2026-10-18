using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Laundry.Edge.Synchronization;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Edge.Orders;

public sealed record OrderDownloadRunResult(string Status, int Pages, int Applied, int Duplicates, int Older, string? Error = null);

// Explicit bounded invocation only; no timer, hosted worker or dependency on scan acceptance.
public sealed class OrderDownloadOnce(HttpClient client, IGatewayTokenProvider tokens, OrderDownloadStore store,
    OrderDownloadScope scope, Uri endpoint)
{
    public async Task<OrderDownloadRunResult> RunAsync(int pageLimit = 100, int maxPages = 100, CancellationToken ct = default)
    {
        var pages = 0; var applied = 0; var duplicates = 0; var older = 0;
        OrderDownloadRunResult Result(string status, string? error = null) => new(status, pages, applied, duplicates, older, error);
        if (!endpoint.IsAbsoluteUri || endpoint.AbsoluteUri != "https://localhost:7100/api/v1/gateway/order-plans" ||
            pageLimit is < 1 or > 100 || maxPages is < 1 or > 100)
            return Result("stopped", "order_download_configuration_invalid");
        try
        {
            for (var page = 0; page < maxPages; page++)
            {
                var progress = await store.ReadProgressAsync(ct);
                var uri = new Uri(endpoint + $"?limit={pageLimit}" +
                    (progress.Cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(progress.Cursor)));
                var json = await FetchAsync(uri, ct);
                var parsed = OrderDownloadBatchParser.Parse(json, scope);
                if (parsed.Items.Length > pageLimit || (parsed.HasMore && parsed.NextCursor == progress.Cursor))
                    throw new OrderDownloadException("invalid_order_batch");
                var batchId = Guid.NewGuid();
                OrderBatchResult receipt;
                try
                {
                    receipt = await store.ApplyAsync(batchId, progress.Revision, progress.Cursor, json, ct);
                }
                catch (Exception exception) when (IsStorageFailure(exception) && !ct.IsCancellationRequested)
                {
                    // A lost commit acknowledgement is uncertain. Retry this exact batch, not a fresh GET.
                    receipt = await store.ApplyAsync(batchId, progress.Revision, progress.Cursor, json, ct);
                }
                pages++; applied += receipt.Applied; duplicates += receipt.Duplicates; older += receipt.Older;
                // A historical receipt is not current progress; another invocation may have advanced it.
                var saved = await store.ReadProgressAsync(ct);
                if (saved.Revision != receipt.ProgressRevision || saved.Cursor != parsed.NextCursor)
                    return Result("stopped", "order_download_progress_changed");
                if (!parsed.HasMore) return Result("caught_up");
            }
            return Result("more_available");
        }
        catch (OrderDownloadException exception) { return Result("stopped", exception.Code); }
        catch (OperationCanceledException) { return Result("stopped", ct.IsCancellationRequested ? "cancelled" : "order_download_timeout"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        { return Result("stopped", "order_download_connection_failed"); }
        catch (Exception exception) when (IsStorageFailure(exception))
        { return Result("stopped", "order_download_storage_unavailable"); }
    }

    private static bool IsStorageFailure(Exception exception) => exception is DbException or DbUpdateException or TimeoutException ||
        (exception is InvalidOperationException && exception.InnerException is DbException);

    private async Task<string> FetchAsync(Uri uri, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await tokens.GetAsync(ct);
            if (!token.Succeeded) throw new OrderDownloadException("order_download_identity_unavailable");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken!);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                tokens.Invalidate(token.AccessToken!); continue;
            }
            if (response.StatusCode != HttpStatusCode.OK)
                throw new OrderDownloadException($"order_download_http_{(int)response.StatusCode}");
            if (response.Content.Headers.ContentType?.MediaType != "application/json" ||
                response.Content.Headers.ContentLength > 4 * 1024 * 1024)
                throw new OrderDownloadException("invalid_order_batch");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[4 * 1024 * 1024 + 1]; var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
                if (read == 0) break;
                count += read;
            }
            if (count == bytes.Length) throw new OrderDownloadException("invalid_order_batch");
            try { return new UTF8Encoding(false, true).GetString(bytes, 0, count); }
            catch (DecoderFallbackException) { throw new OrderDownloadException("invalid_order_batch"); }
        }
        throw new OrderDownloadException("order_download_http_401");
    }
}
