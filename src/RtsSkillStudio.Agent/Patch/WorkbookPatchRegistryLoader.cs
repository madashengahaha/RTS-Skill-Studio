using System.Text.Json;

namespace RtsSkillStudio.Agent.Patch;

public static class WorkbookPatchRegistryLoader
{
    public static WorkbookPatchRegistry Load(string contractRoot)
    {
        string capabilityRegistryPath = Path.Combine(
            contractRoot,
            "config",
            "capability-registry.v0.json"
        );
        string defaultValueContractPath = Path.Combine(
            contractRoot,
            "config",
            "default-value-contract.v0.json"
        );
        string defaultMechanismContractPath = Path.Combine(
            contractRoot,
            "config",
            "default-mechanism-contract.v0.json"
        );

        using JsonDocument capabilityDocument = JsonDocument.Parse(
            File.ReadAllText(capabilityRegistryPath)
        );
        using JsonDocument defaultValueDocument = JsonDocument.Parse(
            File.ReadAllText(defaultValueContractPath)
        );
        using JsonDocument defaultMechanismDocument = JsonDocument.Parse(
            File.ReadAllText(defaultMechanismContractPath)
        );

        JsonElement capability = capabilityDocument.RootElement;
        JsonElement defaults = defaultValueDocument.RootElement;
        JsonElement mechanisms = defaultMechanismDocument.RootElement;
        return new WorkbookPatchRegistry(
            GetInt32(capability, "schemaVersion"),
            GetRequiredString(capability, "registryVersion"),
            GetRequiredString(defaults, "contractVersion"),
            GetRequiredString(mechanisms, "contractVersion"),
            ReadEntities(capability),
            ReadEntityFields(capability),
            ReadConversionRules(defaults)
        );
    }

    private static IReadOnlyList<WorkbookPatchRegistryEntity> ReadEntities(
        JsonElement capability
    )
    {
        if (
            !capability.TryGetProperty("entities", out JsonElement entities)
            || entities.ValueKind != JsonValueKind.Array
        )
        {
            return [];
        }

        return entities
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(
                item => new WorkbookPatchRegistryEntity(
                    GetRequiredString(item, "key"),
                    GetRequiredString(item, "namespace"),
                    GetString(item, "kind") ?? "",
                    GetString(item, "role")
                )
            )
            .ToArray();
    }

    private static IReadOnlyList<WorkbookPatchRegistryField> ReadEntityFields(
        JsonElement capability
    )
    {
        if (
            !capability.TryGetProperty(
                "entityFields",
                out JsonElement fields
            )
            || fields.ValueKind != JsonValueKind.Array
        )
        {
            return [];
        }

        return fields
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(
                item => new WorkbookPatchRegistryField(
                    GetRequiredString(item, "path"),
                    GetString(item, "semanticName"),
                    GetString(item, "kind") ?? "",
                    GetString(item, "rawType"),
                    GetBoolean(item, "required"),
                    GetDecimal(item, "scale"),
                    GetString(item, "unit"),
                    GetDecimal(item, "minimum"),
                    GetDecimal(item, "maximum"),
                    GetString(item, "enumName"),
                    GetStringArray(item, "aliases")
                )
            )
            .ToArray();
    }

    private static IReadOnlyList<WorkbookPatchConversionRule> ReadConversionRules(
        JsonElement defaults
    )
    {
        if (
            !defaults.TryGetProperty(
                "conversionRules",
                out JsonElement rules
            )
            || rules.ValueKind != JsonValueKind.Array
        )
        {
            return [];
        }

        return rules
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(
                item => new WorkbookPatchConversionRule(
                    GetRequiredString(item, "key"),
                    GetRequiredString(item, "inputUnit"),
                    GetRequiredString(item, "outputUnit"),
                    GetDecimal(item, "factor") ?? 0,
                    GetStringArray(item, "inputAliases"),
                    GetStringArray(item, "outputAliases")
                )
            )
            .ToArray();
    }

    private static string GetRequiredString(
        JsonElement owner,
        string propertyName
    )
    {
        return GetString(owner, propertyName)
            ?? throw new InvalidDataException(
                $"WorkbookPatch registry is missing {propertyName}."
            );
    }

    private static string? GetString(
        JsonElement owner,
        string propertyName
    )
    {
        return owner.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int GetInt32(
        JsonElement owner,
        string propertyName
    )
    {
        return owner.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int result)
            ? result
            : 0;
    }

    private static bool GetBoolean(
        JsonElement owner,
        string propertyName
    )
    {
        return owner.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();
    }

    private static decimal? GetDecimal(
        JsonElement owner,
        string propertyName
    )
    {
        return owner.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out decimal result)
            ? result
            : null;
    }

    private static IReadOnlyList<string> GetStringArray(
        JsonElement owner,
        string propertyName
    )
    {
        if (
            !owner.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array
        )
        {
            return [];
        }

        return value
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => item.Length > 0)
            .ToArray();
    }
}
