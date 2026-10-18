using Laundry.Edge.Security;

namespace Laundry.Edge.Textiles;

// Synthetic fixtures only. Not a production textile registry or a source of ownership changes.
public static class DevelopmentItemCatalog
{
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static readonly Guid PlantId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    public static bool Supports(SourceScope scope) => scope.TenantId == TenantId && scope.PlantId == PlantId;

    public static DevelopmentItemLookupResult Lookup(SourceScope scope, string identifier)
    {
        if (!Supports(scope)) throw new ArgumentException("The synthetic catalog is limited to the demo installation.", nameof(scope));
        var customer = new DevelopmentItemCustomer(Guid.Parse("33333333-3333-4333-8333-333333333333"), "DEMO-001", "Demo laundry customer");
        var team = new DevelopmentItemCustomer(Guid.Parse("44444444-4444-4444-8444-444444444444"), "SYNTHETIC-TEAM", "Synthetic cleaning team");
        var item = identifier switch
        {
            "SIMULATED-RFID-TOWEL-001" => new DevelopmentTextileItem(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1"), "Synthetic towel 001", "towel", customer),
            "SIMULATED-RFID-SHEET-001" => new DevelopmentTextileItem(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2"), "Synthetic sheet 001", "sheet", customer),
            "SIMULATED-RFID-MOP-001" => new DevelopmentTextileItem(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa3"), "Synthetic mop 001", "mop", team),
            _ => null
        };
        return new(true, item is null ? "notRecognized" : "recognized", item);
    }
}

public sealed record DevelopmentItemCustomer(Guid Id, string Code, string DisplayName);
public sealed record DevelopmentTextileItem(Guid Id, string DisplayName, string ArticleType, DevelopmentItemCustomer Customer);
public sealed record DevelopmentItemLookupResult(bool IsSynthetic, string Status, DevelopmentTextileItem? Item);
