using System.Collections.Immutable;

namespace Laundry.Api.Orders;

public static class OrderPlanningPermissions
{
    public const string Read = "orders.read";
    public const string Create = "orders.create";
    public const string EditPlan = "orders.plan.edit";
    public const string EditNotesDefault = "orders.notes.default.edit";

    // A scoped bundle, not a global administrator role or implicit access to every plant.
    public static ImmutableHashSet<string> Management { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, Read, Create, EditPlan);

    public static bool IsKnown(string permission) => Management.Contains(permission) || permission == EditNotesDefault;
}
