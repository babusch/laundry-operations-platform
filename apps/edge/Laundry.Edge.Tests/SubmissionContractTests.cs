using System.Text.Json;
using System.Text.Json.Nodes;
using Laundry.Edge.Scans;

namespace Laundry.Edge.Tests;

public sealed class SubmissionContractTests
{
    public static JsonObject Example(string technology = "barcode")
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Contracts", $"submit-scan.v1.{technology}.json")))!.AsObject();
        json["eventId"] = Guid.NewGuid().ToString();
        return json;
    }

    [Theory]
    [InlineData("barcode")]
    [InlineData("rfid")]
    public void ExamplesAreValidAndEveryRequiredFieldIsEnforced(string technology)
    {
        var json = Example(technology);
        Assert.True(Valid(json));
        foreach (var field in json.Select(x => x.Key).ToArray())
        {
            var copy = json.DeepClone().AsObject();
            copy.Remove(field);
            Assert.False(Valid(copy), field);
        }
    }

    [Theory]
    [InlineData("eventId", "not-a-uuid")]
    [InlineData("eventType", "scan.changed")]
    [InlineData("observedAtUtc", "2026-02-30T12:00:00Z")]
    [InlineData("observedAtUtc", "2026-09-07T12:00:00+00:00")]
    [InlineData("gatewayAcceptedAtUtc", "2026-09-07T12:00:00Z")]
    [InlineData("itemDescription", "not part of a scan")]
    public void InvalidFieldsAreRejected(string field, string value)
    {
        var json = Example();
        json[field] = value;
        Assert.False(Valid(json));
    }

    [Fact]
    public void InvalidVersionIdentifierAndDuplicatePropertiesAreRejected()
    {
        var json = Example();
        json["schemaVersion"] = 2;
        Assert.False(Valid(json));
        foreach (var value in new[] { "", "  ", new string('x', 513) })
        {
            json = Example();
            json["identifier"]!["value"] = value;
            Assert.False(Valid(json));
        }
        json = Example();
        json["identifier"]!["technology"] = "unknown";
        Assert.False(Valid(json));
        var raw = Example().ToJsonString().Insert(1, "\"schemaVersion\":1,");
        using var document = JsonDocument.Parse(raw);
        Assert.False(new SubmissionValidator().IsValid(document.RootElement));
    }

    private static bool Valid(JsonObject json) =>
        new SubmissionValidator().IsValid(JsonSerializer.SerializeToElement(json));
}
