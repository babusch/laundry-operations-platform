using Laundry.Api.Orders;

namespace Laundry.Api.Tests;

public sealed class OrderPlanTests
{
    [Fact]
    public void FutureSameDayPlan_NormalizesOptionalFieldsWithoutLosingLineBreaks()
    {
        var date = new DateOnly(2030, 1, 1);
        var plan = new OrderPlan(Guid.NewGuid(), date, date, "  ", "  Line one\nLine two  ").Normalize();
        Assert.Null(plan.CustomerReference);
        Assert.Equal("Line one\nLine two", plan.Notes);
        Assert.Equal(date, plan.PlannedReceivingDate);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("date")]
    [InlineData("delivery")]
    [InlineData("reference")]
    [InlineData("notes")]
    public void InvalidPlans_AreRejected(string invalidField)
    {
        var date = new DateOnly(2030, 1, 1);
        var plan = new OrderPlan(Guid.NewGuid(), date, date);
        plan = invalidField switch
        {
            "customer" => plan with { CustomerId = Guid.Empty },
            "date" => plan with { PlannedReceivingDate = default },
            "delivery" => plan with { PlannedDeliveryDate = date.AddDays(-1) },
            "reference" => plan with { CustomerReference = new string('x', 101) },
            _ => plan with { Notes = new string('x', 2001) }
        };
        Assert.Equal("invalid_plan", Assert.Throws<OrderPlanningException>(() => plan.Normalize()).Code);
    }
}
