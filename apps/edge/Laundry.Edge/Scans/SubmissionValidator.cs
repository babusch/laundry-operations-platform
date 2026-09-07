using System.Text.Json;
using Json.Schema;

namespace Laundry.Edge.Scans;

public sealed class SubmissionValidator
{
    private readonly JsonSchema _schema;

    public SubmissionValidator()
    {
        using var stream = typeof(SubmissionValidator).Assembly
            .GetManifestResourceStream("Contracts.SubmitScanV1.json")
            ?? throw new InvalidOperationException("The scan contract is missing from the application.");
        using var document = JsonDocument.Parse(stream);
        _schema = JsonSchema.Build(document.RootElement.Clone(), new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new()
        });
    }

    public bool IsValid(JsonElement payload) =>
        !HasDuplicateProperties(payload) &&
        _schema.Evaluate(payload, new EvaluationOptions { RequireFormatValidation = true }).IsValid;

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
