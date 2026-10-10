using System.Text.Json;
using TianshuDM.Domain.GameData;

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
            ReadConversionRules(defaults),
            ReadActions(capability),
            ReadCreation(defaults, capability)
        );
    }

    private static System.Text.Json.Nodes.JsonObject? ReadCreation(JsonElement defaults, JsonElement capability)
    {
        if (!defaults.TryGetProperty("creation", out JsonElement creation)) return null;
        var result = System.Text.Json.Nodes.JsonNode.Parse(creation.GetRawText())!.AsObject();
        if (defaults.TryGetProperty("editing", out JsonElement editing))
            result["editing"] = System.Text.Json.Nodes.JsonNode.Parse(editing.GetRawText());
        foreach (string key in new[] { "nestedTypes", "enums" })
            if (capability.TryGetProperty(key, out JsonElement data))
                result[key] = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText());
        return result;
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
                    GetStringArray(item, "aliases"),
                    GetString(item, "referenceTarget"),
                    GetString(item, "referenceRemoval")
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

    private static IReadOnlyList<WorkbookPatchAction> ReadActions(
        JsonElement capability
    )
    {
        IReadOnlyDictionary<string, IReadOnlyList<GameDataOption>>
            enumOptions = ReadEnumOptions(capability);
        var actions = new List<WorkbookPatchAction>();
        foreach ((string category, string propertyName) in new[]
        {
            ("effect", "effects"),
            ("condition", "conditions")
        })
        {
            if (
                !capability.TryGetProperty(
                    propertyName,
                    out JsonElement actionElements
                )
                || actionElements.ValueKind != JsonValueKind.Array
            )
            {
                continue;
            }

            foreach (
                JsonElement actionElement in actionElements.EnumerateArray()
            )
            {
                string actionKey = GetRequiredString(actionElement, "key");
                var parameters = new List<WorkbookPatchActionParameter>();
                if (
                    actionElement.TryGetProperty(
                        "parameters",
                        out JsonElement parameterElements
                    )
                    && parameterElements.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (
                        JsonElement parameter in
                            parameterElements.EnumerateArray()
                    )
                    {
                        string contractKind =
                            GetString(parameter, "kind") ?? "Text";
                        string? enumName = GetString(parameter, "enumName");
                        parameters.Add(
                            new WorkbookPatchActionParameter(
                                GetInt32(parameter, "index"),
                                GetRequiredString(parameter, "key"),
                                GetString(parameter, "label") ?? "",
                                contractKind,
                                MapFieldKind(contractKind),
                                contractKind,
                                GetString(parameter, "referenceTarget"),
                                enumName,
                                GetBoolean(parameter, "required"),
                                GetDecimal(parameter, "scale") ?? 1,
                                GetString(parameter, "unit"),
                                GetDecimal(parameter, "minimum"),
                                GetDecimal(parameter, "maximum"),
                                GetBoolean(parameter, "repeating"),
                                GetInt32(parameter, "repeatStep"),
                                enumName is not null
                                && enumOptions.TryGetValue(
                                    enumName,
                                    out IReadOnlyList<GameDataOption>? options
                                )
                                    ? options
                                    : [],
                                parameter.TryGetProperty("defaultValue", out JsonElement defaultValue)
                                    ? defaultValue.Clone() : null,
                                GetBoolean(parameter, "allowsMultipleEnumValues")
                            )
                        );
                    }
                }

                actions.Add(
                    new WorkbookPatchAction(
                        category,
                        actionKey,
                        GetNullableInt32(actionElement, "legacyValue"),
                        parameters
                    )
                );
            }
        }

        return actions;
    }

    private static IReadOnlyDictionary<
        string,
        IReadOnlyList<GameDataOption>
    > ReadEnumOptions(JsonElement capability)
    {
        if (
            !capability.TryGetProperty("enums", out JsonElement enums)
            || enums.ValueKind != JsonValueKind.Array
        )
        {
            return new Dictionary<string, IReadOnlyList<GameDataOption>>(
                StringComparer.OrdinalIgnoreCase
            );
        }

        return enums
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(
                enumElement =>
                {
                    string name = GetRequiredString(enumElement, "name");
                    IReadOnlyList<GameDataOption> options =
                        enumElement.TryGetProperty(
                            "values",
                            out JsonElement values
                        )
                        && values.ValueKind == JsonValueKind.Array
                            ? values
                                .EnumerateArray()
                                .Where(
                                    value =>
                                        value.ValueKind
                                        == JsonValueKind.Object
                                )
                                .Select(
                                    value => new GameDataOption(
                                        GetInt32(value, "value").ToString(
                                            System.Globalization.CultureInfo.InvariantCulture
                                        ),
                                        FirstNonEmpty(
                                            GetString(value, "alias"),
                                            GetString(value, "name")
                                        ),
                                        GetString(value, "name"),
                                        GetInt32(value, "value")
                                    )
                                )
                                .ToArray()
                            : [];
                    return (Name: name, Options: options);
                }
            )
            .ToDictionary(
                item => item.Name,
                item => item.Options,
                StringComparer.OrdinalIgnoreCase
            );
    }

    private static GameDataFieldKind MapFieldKind(string contractKind)
    {
        return contractKind switch
        {
            "ScaledInteger" => GameDataFieldKind.Integer,
            "Integer" => GameDataFieldKind.Integer,
            "Enum" => GameDataFieldKind.Enum,
            "Reference" => GameDataFieldKind.Reference,
            "Boolean" => GameDataFieldKind.Boolean,
            "Text" => GameDataFieldKind.Text,
            _ => GameDataFieldKind.Text
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(
            value => !string.IsNullOrWhiteSpace(value)
        ) ?? "";
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

    private static int? GetNullableInt32(
        JsonElement owner,
        string propertyName
    )
    {
        return owner.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int result)
                ? result
                : null;
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
