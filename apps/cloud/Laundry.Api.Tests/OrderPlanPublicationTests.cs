using System.Text.Json;
using System.Text.Json.Serialization;
using Laundry.Api.Orders;

namespace Laundry.Api.Tests;

public sealed class OrderPlanPublicationTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Plant = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid CustomerId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static OrderPublicationCustomer Customer => new(Tenant, CustomerId, "DEMO-001", "Synthetic laundry customer");
    private static ServiceOrder Order() => new()
    {
        Id = Guid.Parse("44444444-4444-4444-8444-444444444444"),
        TenantId = Tenant,
        PlantId = Plant,
        CustomerId = CustomerId,
        OrderNumber = "ORD-000001",
        PlannedReceivingDate = new(2030, 1, 2),
        PlannedDeliveryDate = new(2030, 1, 3),
        NotesVisibleToOperators = true,
        Notes = "Synthetic instruction: keep the demonstration batch together.",
        CustomerReference = "SYNTHETIC-MANAGEMENT-REFERENCE",
        CreatedBySubject = "SYNTHETIC-PRIVATE-ACTOR",
        Version = 41
    };
    private static OrderPlanPublication Map(ServiceOrder order, long version = 1) => OrderPlanPublication.From(Tenant, Plant, order, Customer, version);

    [Theory]
    [InlineData(true, 1, "visible")]
    [InlineData(false, 2, "hidden")]
    public void SerializedOutputMatchesSharedContractExample(bool visible, long revision, string example)
    {
        var order = Order(); order.NotesVisibleToOperators = visible;
        var json = JsonSerializer.Serialize(Map(order, revision), new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", $"order-plan.v1.{example}.json")));
        using var actual = JsonDocument.Parse(json);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        using var defaultSerialization = JsonDocument.Parse(JsonSerializer.Serialize(Map(order, revision)));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, defaultSerialization.RootElement));
        Assert.DoesNotContain("SYNTHETIC-MANAGEMENT-REFERENCE", json);
        Assert.DoesNotContain("SYNTHETIC-PRIVATE-ACTOR", json);
        Assert.DoesNotContain("customerReference", json);
        Assert.Equal(12, actual.RootElement.EnumerateObject().Count());
        // Ignored build artifact used for independent JSON Schema verification, never live data.
        var directory = Path.Combine(AppContext.BaseDirectory, "publication-preview"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, example + ".json"), json);
    }

    [Fact]
    public void HiddenTextIsAbsentAndOriginalManagementRecordIsUnchanged()
    {
        var order = Order(); order.NotesVisibleToOperators = false; order.Notes = "SYNTHETIC-HIDDEN-TEXT";
        var copy = Map(order);
        Assert.Null(copy.Notes);
        Assert.DoesNotContain(order.Notes, JsonSerializer.Serialize(copy));
        Assert.Equal("SYNTHETIC-HIDDEN-TEXT", order.Notes);
        Assert.Equal(41, order.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public void EmptyVisibleNotesAreExplicitlyNull(string? notes)
    {
        var order = Order(); order.Notes = notes;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Map(order), new JsonSerializerOptions
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("notes").ValueKind);
        Assert.True(json.RootElement.GetProperty("notesVisibleToOperators").GetBoolean());
    }

    [Fact]
    public void CopyIsDetachedAndUsesExplicitPublicationRevisionNotOrderVersion()
    {
        var order = Order(); order.Notes = "  Synthetic visible note  ";
        var copy = Map(order, 7);
        order.Notes = "Later change"; order.OrderNumber = "Later number";
        Assert.Equal("Synthetic visible note", copy.Notes); Assert.Equal("ORD-000001", copy.OrderNumber);
        Assert.Equal(7, copy.Version);
        var renamed = OrderPlanPublication.From(Tenant, Plant, Order(), Customer with { DisplayName = "Synthetic renamed customer" }, 8);
        Assert.Equal(8, renamed.Version); Assert.Equal("Synthetic renamed customer", renamed.Customer.DisplayName);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("plant")]
    [InlineData("customerTenant")]
    [InlineData("customerId")]
    [InlineData("emptyOrder")]
    [InlineData("emptyTenant")]
    [InlineData("emptyPlant")]
    [InlineData("emptyCustomer")]
    public void MismatchedOrEmptyScopeIsRejectedWithoutExposingContents(string field)
    {
        var order = Order(); var customer = Customer; var tenant = Tenant; var plant = Plant;
        switch (field)
        {
            case "tenant": order.TenantId = Guid.NewGuid(); break;
            case "plant": order.PlantId = Guid.NewGuid(); break;
            case "customerTenant": customer = customer with { TenantId = Guid.NewGuid() }; break;
            case "customerId": customer = customer with { Id = Guid.NewGuid() }; break;
            case "emptyOrder": order.Id = Guid.Empty; break;
            case "emptyTenant": tenant = Guid.Empty; break;
            case "emptyPlant": plant = Guid.Empty; break;
            case "emptyCustomer": customer = customer with { Id = Guid.Empty }; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => OrderPlanPublication.From(tenant, plant, order, customer, 1));
        Assert.Equal("order_publication_scope_mismatch", error.Message);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(9007199254740992L)]
    public void InvalidPublicationRevisionsAreRejected(long revision) => Assert.Throws<ArgumentOutOfRangeException>(() => Map(Order(), revision));

    [Fact]
    public void RepeatedMappingIsDeterministicAndAcceptsSupportedBoundaries()
    {
        var order = Order(); order.Notes = new string('x', 2000); order.PlannedDeliveryDate = order.PlannedReceivingDate;
        var customer = Customer with { Code = new string('c', 50), DisplayName = new string('n', 200) };
        var first = OrderPlanPublication.From(Tenant, Plant, order, customer, 9007199254740991);
        var retry = OrderPlanPublication.From(Tenant, Plant, order, customer, 9007199254740991);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(retry));
        Assert.Equal(2000, first.Notes!.Length);
        Assert.Equal(9007199254740991, first.Version);
    }

    [Fact]
    public void MissingInputsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => OrderPlanPublication.From(Tenant, Plant, null!, Customer, 1));
        Assert.Throws<ArgumentNullException>(() => OrderPlanPublication.From(Tenant, Plant, Order(), null!, 1));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("number")]
    [InlineData("receiving")]
    [InlineData("delivery")]
    [InlineData("code")]
    [InlineData("longCode")]
    [InlineData("name")]
    [InlineData("longName")]
    [InlineData("longNotes")]
    public void InvalidSourceValuesFailClosed(string field)
    {
        var order = Order(); var customer = Customer;
        switch (field)
        {
            case "status": order.Status = "receiving"; break;
            case "number": order.OrderNumber = " "; break;
            case "receiving": order.PlannedReceivingDate = default; break;
            case "delivery": order.PlannedDeliveryDate = new(2030, 1, 1); break;
            case "code": customer = customer with { Code = " " }; break;
            case "longCode": customer = customer with { Code = new string('x', 51) }; break;
            case "name": customer = customer with { DisplayName = " " }; break;
            case "longName": customer = customer with { DisplayName = new string('x', 201) }; break;
            case "longNotes": order.Notes = new string('x', 2001); break;
        }
        Assert.Equal("invalid_order_publication", Assert.Throws<InvalidOperationException>(() =>
            OrderPlanPublication.From(Tenant, Plant, order, customer, 1)).Message);
    }
}
