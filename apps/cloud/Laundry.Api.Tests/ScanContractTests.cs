using System.Text.Json;
using System.Text.Json.Nodes;
using Laundry.Api.Integrations.Scans;

namespace Laundry.Api.Tests;

public sealed class ScanContractTests
{
    private readonly ScanContractValidator _validator = new();

    internal static JsonObject Example(string technology = "barcode", int version = 1)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Contracts", $"scan-observed.v{version}.{technology}.json")))!.AsObject();
        json["eventId"] = Guid.NewGuid();
        return json;
    }

    [Theory]
    [InlineData("barcode", 1)]
    [InlineData("rfid", 1)]
    [InlineData("barcode", 2)]
    [InlineData("rfid", 2)]
    public void CheckedInExamplesAreValid(string technology, int version) =>
        Assert.True(Valid(Example(technology, version)));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryRequiredPropertyIsEnforced(int version)
    {
        var json = Example(version: version);
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
        json["schemaVersion"] = 3;
        Assert.False(Valid(json));
        json["schemaVersion"] = 1;
        json["identifier"]!["value"] = new string('x', 513);
        Assert.False(Valid(json));
    }

    [Fact]
    public void VersionNumberCannotDisguisePayloadFromAnotherVersion()
    {
        var v2 = Example(version: 2);
        v2["schemaVersion"] = 1;
        Assert.False(Valid(v2));

        var v1 = Example();
        v1["schemaVersion"] = 2;
        Assert.False(Valid(v1));
    }

    private bool Valid(JsonNode json)
    {
        using var document = JsonDocument.Parse(json.ToJsonString());
        return _validator.IsValid(document.RootElement);
    }
}
