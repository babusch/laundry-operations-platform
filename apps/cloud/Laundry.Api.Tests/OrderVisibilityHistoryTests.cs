using System.Text.Json;
using Laundry.Api.Orders;

namespace Laundry.Api.Tests;

public sealed class OrderVisibilityHistoryTests
{
    private static readonly string Plan = JsonSerializer.Serialize(new OrderPlan(Guid.NewGuid(), new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 3)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    [Fact]
    public void LegacyVisibilityIsUnknownRatherThanInferredFromCurrentDefaults()
    {
        var created = OrderHistoryEntry.From(new OrderAuditEntry { AfterPlan = Plan });
        Assert.Null(created.InitialPlan!.NotesVisibleToOperators);
        var changed = OrderHistoryEntry.From(new OrderAuditEntry { BeforePlan = Plan, AfterPlan = Plan[..^1] + ",\"notesVisibleToOperators\":false}" });
        var change = Assert.Single(changed.Changes);
        Assert.Equal("notesVisibleToOperators", change.Field); Assert.Null(change.Before); Assert.Equal("false", change.After);
    }
    [Theory]
    [InlineData("\"false\"")]
    [InlineData("1")]
    [InlineData("{}")]
    public void MalformedRecordedVisibilityIsRejected(string value) => Assert.Throws<JsonException>(() =>
        OrderHistoryEntry.From(new OrderAuditEntry { AfterPlan = Plan[..^1] + ",\"notesVisibleToOperators\":" + value + "}" }));
}
