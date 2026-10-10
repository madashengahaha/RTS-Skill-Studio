using System.Text.Json;
using NJsonSchema;
using NJsonSchema.Validation;

namespace RtsSkillStudio.Agent.Llm;

public sealed record SkillPlanClarification(
    string Key,
    string Question,
    string FieldPath
);

public sealed record SkillPlanUnsupported(
    string Code,
    string Message,
    string ManualPath
);

public sealed record SkillConfigPlanValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    string? Status,
    IReadOnlyList<SkillPlanClarification> Clarifications,
    IReadOnlyList<SkillPlanUnsupported> Unsupported
);

public sealed class SkillConfigPlanValidator
{
    public Task<string> ReadSemanticReviewSchemaAsync(CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(_schemaPath)!,
            "skill-plan-semantic-review.schema.json"), cancellationToken);

    public async Task<string> ReadSchemaAsync(CancellationToken cancellationToken, string? operationDefinition = null)
    {
        string schema = await File.ReadAllTextAsync(_schemaPath, cancellationToken);
        if (operationDefinition is null) return schema.Replace("\"value\": true", "\"value\": {}", StringComparison.Ordinal);
        var root = System.Text.Json.Nodes.JsonNode.Parse(schema)!.AsObject();
        root["properties"]!.AsObject().Remove("assumptions");
        root["properties"]!.AsObject().Remove("summary");
        root["$defs"]!["Operation"] = new System.Text.Json.Nodes.JsonObject
            { ["$ref"] = "#/$defs/" + operationDefinition };
        var definitions = root["$defs"]!.AsObject();
        var required = new HashSet<string>(StringComparer.Ordinal);
        Collect(root["properties"]);
        Collect(root["allOf"]);
        foreach (string key in definitions.Select(pair => pair.Key).ToArray())
            if (!required.Contains(key)) definitions.Remove(key);
        return root.ToJsonString().Replace("\"value\":true", "\"value\":{}", StringComparison.Ordinal);
        void Collect(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                if (obj["$ref"]?.GetValue<string>() is string reference && reference.StartsWith("#/$defs/", StringComparison.Ordinal))
                {
                    string key = reference[8..];
                    if (required.Add(key)) Collect(definitions[key]);
                }
                foreach (var pair in obj) Collect(pair.Value);
            }
            else if (node is System.Text.Json.Nodes.JsonArray array)
                foreach (var child in array) Collect(child);
        }
    }
    private readonly string _schemaPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsonSchema? _schema;

    public SkillConfigPlanValidator(string contractRoot)
    {
        _schemaPath = Path.Combine(
            contractRoot,
            "contracts",
            "skill-config-plan.schema.json"
        );
    }

    public async Task<SkillConfigPlanValidationResult> ValidateAsync(
        string planJson,
        CancellationToken cancellationToken
    )
    {
        var errors = new List<string>();
        try
        {
            JsonSchema schema = await GetSchemaAsync(cancellationToken);
            errors.AddRange(
                schema
                    .Validate(planJson)
                    .SelectMany(FlattenError)
                    .Distinct(StringComparer.Ordinal)
            );
        }
        catch (JsonException exception)
        {
            errors.Add($"Plan JSON 无法解析：{exception.Message}");
        }

        using JsonDocument? document = TryParse(planJson);
        return new SkillConfigPlanValidationResult(
            errors.Count == 0,
            errors,
            ReadString(document?.RootElement, "status"),
            ReadClarifications(document?.RootElement),
            ReadUnsupported(document?.RootElement)
        );
    }

    private async Task<JsonSchema> GetSchemaAsync(
        CancellationToken cancellationToken
    )
    {
        if (_schema is not null)
        {
            return _schema;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_schema is not null)
            {
                return _schema;
            }

            string schemaJson = await File.ReadAllTextAsync(
                _schemaPath,
                cancellationToken
            );
            schemaJson = schemaJson
                .Replace("\"$defs\"", "\"definitions\"", StringComparison.Ordinal)
                .Replace("#/$defs/", "#/definitions/", StringComparison.Ordinal)
                .Replace("\"value\": true", "\"value\": {}", StringComparison.Ordinal);
            _schema = await JsonSchema.FromJsonAsync(
                schemaJson,
                new Uri(_schemaPath).AbsoluteUri,
                cancellationToken
            );
            return _schema;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IEnumerable<string> FlattenError(ValidationError error)
    {
        yield return $"{error.Path}: {error.Kind}";
        if (error is ChildSchemaValidationError child)
        {
            foreach (ValidationError nested in child.Errors.Values.SelectMany(
                         value => value
                     ))
            {
                foreach (string message in FlattenError(nested))
                {
                    yield return message;
                }
            }
        }
        else if (error is MultiTypeValidationError multi)
        {
            foreach (ValidationError nested in multi.Errors.Values.SelectMany(
                         value => value
                     ))
            {
                foreach (string message in FlattenError(nested))
                {
                    yield return message;
                }
            }
        }
    }

    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? root, string propertyName)
    {
        return root is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static IReadOnlyList<SkillPlanClarification> ReadClarifications(
        JsonElement? root
    )
    {
        if (
            root is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty(
                "clarifications",
                out JsonElement clarifications
            )
            || clarifications.ValueKind != JsonValueKind.Array
        )
        {
            return [];
        }

        return clarifications
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(
                item => new SkillPlanClarification(
                    ReadString(item, "key") ?? "",
                    ReadString(item, "question") ?? "",
                    ReadString(item, "fieldPath") ?? ""
                )
            )
            .ToArray();
    }

    private static IReadOnlyList<SkillPlanUnsupported> ReadUnsupported(
        JsonElement? root
    )
    {
        if (
            root is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty("unsupported", out JsonElement unsupported)
            || unsupported.ValueKind != JsonValueKind.Array
        )
        {
            return [];
        }

        return unsupported
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(
                item => new SkillPlanUnsupported(
                    ReadString(item, "code") ?? "",
                    ReadString(item, "message") ?? "",
                    ReadString(item, "manualPath") ?? ""
                )
            )
            .ToArray();
    }
}
