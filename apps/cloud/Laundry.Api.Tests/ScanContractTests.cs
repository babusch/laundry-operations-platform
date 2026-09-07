using System.Text.Json;
using System.Text.Json.Nodes;
using Laundry.Api.Integrations.Scans;

namespace Laundry.Api.Tests;

public sealed class ScanContractTests
{
    private readonly ScanContractValidator _validator = new();

    internal static JsonObject Example(string technology = "barcode")
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Contracts", $"scan-observed.v1.{technology}.json")))!.AsObject();
        json["eventId"] = Guid.NewGuid();
        return json;
    }

    [Theory]
    [InlineData("barcode")]
    [InlineData("rfid")]
    public void CheckedInExamplesAreValid(string technology) =>
        Assert.True(Valid(Example(technology)));

    [Fact]
    public void EveryRequiredPropertyIsEnforced()
    {
        var json = Example();
        foreach (var name in json.Select(x => x.Key).ToArray())
        {
            var incomplete = json.DeepClone().AsObject();
            incomplete.Remove(name);
            Assert.False(Valid(incomplete), name);
        }
    }

    [Theory]
    [InlineData("eventId", "bad-uuid")]
    [InlineData("observedAtUtc", "2026-09-07T12:00:00+02:00")]
    [InlineData("observedAtUtc", "2026-02-30T12:00:00Z")]
    [InlineData("gatewayAcceptedAtUtc", "not-a-dateZ")]
    [InlineData("eventType", "item.received")]
    [InlineData("itemDescription", "SHOULD-NOT-BE-HERE")]
    public void InvalidFieldsAreRejected(string property, string value)
    {
        var json = Example();
        json[property] = value;
        Assert.False(Valid(json));
    }

    [Theory]
    [InlineData("technology", "qr-code")]
    [InlineData("value", "")]
    [InlineData("value", "   ")]
    [InlineData("antenna", "1")]
    public void InvalidIdentifierIsRejected(string property, string value)
    {
        var json = Example();
        json["identifier"]![property] = value;
        Assert.False(Valid(json));
    }

    [Fact]
    public void WrongVersionAndOversizedIdentifierAreRejected()
    {
        var json = Example();
        json["schemaVersion"] = 2;
        Assert.False(Valid(json));
        json["schemaVersion"] = 1;
        json["identifier"]!["value"] = new string('x', 513);
        Assert.False(Valid(json));
    }

    private bool Valid(JsonNode json)
    {
        using var document = JsonDocument.Parse(json.ToJsonString());
        return _validator.IsValid(document.RootElement);
    }
}
