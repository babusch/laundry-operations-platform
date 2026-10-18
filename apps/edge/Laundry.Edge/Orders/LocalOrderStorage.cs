namespace Laundry.Edge.Orders;

// Gateway-owned persistence models; never share cloud persistence entities.
public sealed class LocalOrderPlan
{
    public Guid TenantId { get; set; }
    public Guid PlantId { get; set; }
    public Guid OrderId { get; set; }
    public long Version { get; set; }
    public string Payload { get; set; } = "";
    public string ContentFingerprint { get; set; } = "";
    public DateTimeOffset StoredAtUtc { get; set; }
}

public sealed class OrderDownloadProgress
{
    public Guid TenantId { get; set; }
    public Guid PlantId { get; set; }
    public long Revision { get; set; }
    public string Cursor { get; set; } = "";
    public DateTimeOffset LastSuccessfulCheckUtc { get; set; }
}

// Inbox evidence contains no historical order payload or note text.
public sealed class OrderDownloadBatchReceipt
{
    public Guid BatchId { get; set; }
    public Guid TenantId { get; set; }
    public Guid PlantId { get; set; }
    public string RequestFingerprint { get; set; } = "";
    public long ProgressRevision { get; set; }
    public int Applied { get; set; }
    public int Duplicates { get; set; }
    public int Older { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }

    public OrderBatchResult Result() => new(BatchId, ProgressRevision, Applied, Duplicates, Older, RecordedAtUtc);
}

public readonly record struct OrderDownloadScope(Guid TenantId, Guid PlantId);
public sealed record LocalOrderProgress(long Revision, string? Cursor, DateTimeOffset? LastSuccessfulCheckUtc);
public sealed record OrderBatchResult(Guid BatchId, long ProgressRevision, int Applied, int Duplicates, int Older, DateTimeOffset RecordedAtUtc);
public sealed class OrderDownloadException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
