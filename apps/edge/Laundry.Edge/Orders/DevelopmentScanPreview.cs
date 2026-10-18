using Laundry.Edge.Textiles;

namespace Laundry.Edge.Orders;

public sealed record DevelopmentScanPreviewResult(bool IsSynthetic, string Status, DevelopmentTextileItem? Item,
    LocalOrderSummary[] Orders, DateTimeOffset? LastSuccessfulPageAtUtc);

// Development orchestration only: read validated local summaries, never choose/activate a business order.
public static class DevelopmentScanPreview
{
    public static async Task<DevelopmentScanPreviewResult> ResolveAsync(DevelopmentItemLookupResult lookup,
        Func<string?, CancellationToken, Task<LocalOrderPage>> read, CancellationToken ct)
    {
        if (lookup.Item is null) return new(true, "notRecognized", null, [], null);
        var matches = new List<LocalOrderSummary>(); var seen = new HashSet<Guid>(); var cursors = new HashSet<string>();
        string? cursor = null; DateTimeOffset? activity = null;
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await read(cursor, ct);
            if (pageNumber > 0 && activity != page.LastSuccessfulPageAtUtc)
                throw new LocalOrderListException("local_order_list_changed");
            activity = page.LastSuccessfulPageAtUtc;
            foreach (var order in page.Items)
            {
                if (!seen.Add(order.Id) || order.Status != "planned") throw new LocalOrderListException("local_order_copy_invalid");
                if (order.Customer.Id == lookup.Item.Customer.Id) matches.Add(order);
                if (matches.Count > 50) throw new LocalOrderListException("development_scan_preview_limit_exceeded");
            }
            if (!page.HasMore)
                return new(true, matches.Count switch { 0 => "noLocalOrders", 1 => "onePlannedOrder", _ => "multiplePlannedOrders" }, lookup.Item, matches.ToArray(), activity);
            if (string.IsNullOrWhiteSpace(page.NextCursor) || page.Items.Length == 0 || !cursors.Add(page.NextCursor))
                throw new LocalOrderListException("local_order_copy_invalid");
            cursor = page.NextCursor;
        }
        // Never report a truncated search as empty or a sole candidate. No partial result escapes.
        throw new LocalOrderListException("development_scan_preview_limit_exceeded");
    }
}
