using System.Text.Json;
using Json.Schema;

namespace Laundry.Api.Integrations.Scans;

public sealed class ScanContractValidator
{
    private readonly IReadOnlyDictionary<int, JsonSchema> _schemas;

    public ScanContractValidator()
    {
        _schemas = new Dictionary<int, JsonSchema>
        {
            [1] = Load("Contracts.ScanObservedV1.json"),
            [2] = Load("Contracts.ScanObservedV2.json")
        };
    }

    public bool IsValid(JsonElement payload)
    {
        if (HasDuplicateProperties(payload) || payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("schemaVersion", out var versionElement) ||
            !versionElement.TryGetInt32(out var version) || !_schemas.TryGetValue(version, out var schema))
        {
            return false;
        }

        return schema.Evaluate(payload, new EvaluationOptions { RequireFormatValidation = true }).IsValid;
    }

    private static JsonSchema Load(string resourceName)
    {
        using var stream = typeof(ScanContractValidator).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The scan contract {resourceName} is missing from the application.");
        using var document = JsonDocument.Parse(stream);
        return JsonSchema.Build(document.RootElement.Clone(), new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new()
        });
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Any(HasDuplicateProperties);
        }

        return false;
    }
}
