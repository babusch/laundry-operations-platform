namespace Laundry.Api.Orders;

public sealed record OrderPlan(Guid CustomerId, DateOnly PlannedReceivingDate,
    DateOnly PlannedDeliveryDate, string? CustomerReference = null, string? Notes = null)
{
    public OrderPlan Normalize()
    {
        if (CustomerId == Guid.Empty || PlannedReceivingDate == default ||
            PlannedDeliveryDate < PlannedReceivingDate || CustomerReference?.Length > 100 || Notes?.Length > 2000)
        {
            throw new OrderPlanningException("invalid_plan");
        }

        return this with
        {
            CustomerReference = string.IsNullOrWhiteSpace(CustomerReference) ? null : CustomerReference.Trim(),
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim()
        };
    }
}

// Supplied by a future verified-human application boundary, never directly from request JSON.
public sealed record OrderPlanningScope(Guid TenantId, Guid PlantId, string ActorIssuer, string ActorSubject);

// The owning identity/customer modules must authorize every call, including historical retries.
// There is deliberately no permissive production implementation or API registration in this slice.
public interface IOrderPlanningAccess
{
    // Server-verified display metadata only; never an authorization or retry identity.
    string? ActorDisplayName => null;
    Task<bool> IsAllowedAsync(OrderPlanningScope scope, string permission, CancellationToken cancellationToken);
    Task<bool> IsCustomerEligibleAsync(OrderPlanningScope scope, Guid customerId, CancellationToken cancellationToken);
}

// Relational composition seam: participants join the owning order transaction, not a
// separate permissive snapshot. No persistence entities are part of this contract.
public interface ITransactionalOrderPlanningAccess : IOrderPlanningAccess
{
    Task<IAsyncDisposable> BindTransactionAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, OrderPlanningScope scope, CancellationToken ct);
}

public sealed class OrderPlanningException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record OrderPlanningReceipt(int Status, string Body, string ETag, string Location);

public sealed class ServiceOrder
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PlantId { get; set; }
    public Guid CustomerId { get; set; }
    public string OrderNumber { get; set; } = "";
    public DateOnly PlannedReceivingDate { get; set; }
    public DateOnly PlannedDeliveryDate { get; set; }
    public string? CustomerReference { get; set; }
    public string? Notes { get; set; }
    public string Status { get; set; } = "planned";
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedByIssuer { get; set; } = "";
    public string CreatedBySubject { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string UpdatedByIssuer { get; set; } = "";
    public string UpdatedBySubject { get; set; } = "";

    public OrderPlan Plan() => new(CustomerId, PlannedReceivingDate, PlannedDeliveryDate, CustomerReference, Notes);
    public void Apply(OrderPlan plan)
    {
        CustomerId = plan.CustomerId;
        PlannedReceivingDate = plan.PlannedReceivingDate;
        PlannedDeliveryDate = plan.PlannedDeliveryDate;
        CustomerReference = plan.CustomerReference;
        Notes = plan.Notes;
    }
}

public sealed class OrderAuditEntry
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PlantId { get; set; }
    public Guid OrderId { get; set; }
    public string Action { get; set; } = "";
    public long OrderVersion { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string ActorIssuer { get; set; } = "";
    public string ActorSubject { get; set; } = "";
    public string? ActorDisplayName { get; set; }
    public Guid RequestId { get; set; }
    public string? BeforePlan { get; set; }
    public string AfterPlan { get; set; } = "";
}

public sealed class OrderCommandReceipt
{
    public Guid TenantId { get; set; }
    public Guid PlantId { get; set; }
    public string ActorIssuer { get; set; } = "";
    public string ActorSubject { get; set; } = "";
    public Guid IdempotencyKey { get; set; }
    public string Operation { get; set; } = "";
    public string Target { get; set; } = "";
    public string RequestFingerprint { get; set; } = "";
    public Guid OrderId { get; set; }
    public int ResponseStatus { get; set; }
    public string ResponseBody { get; set; } = "";
    public string ResponseEtag { get; set; } = "";
    public string ResponseLocation { get; set; } = "";
    public DateTimeOffset AcceptedAtUtc { get; set; }

    public OrderPlanningReceipt Result() => new(ResponseStatus, ResponseBody, ResponseEtag, ResponseLocation);
}

public sealed class OrderNumberCounter
{
    public Guid TenantId { get; set; }
    public Guid PlantId { get; set; }
    public long LastNumber { get; set; }
}
