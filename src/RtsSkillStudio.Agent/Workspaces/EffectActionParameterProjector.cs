using System.Globalization;
using System.Text.Json.Nodes;

namespace RtsSkillStudio.Agent.Workspaces;

public static class EffectActionParameterProjector
{
    public static JsonObject? Project(
        JsonObject registry,
        SkillChainNode node,
        IReadOnlyList<SkillChainEdge> edges,
        IReadOnlyDictionary<string, SkillChainNode> nodesByKey
    )
    {
        string category = Category(node.Namespace);
        string? actionKey = ActionKey(node);
        if (category.Length == 0 || string.IsNullOrWhiteSpace(actionKey))
        {
            return null;
        }

        JsonObject? action = FindAction(registry, category, actionKey);
        IReadOnlyList<string> rawValues =
            node.Fields.TryGetValue("action_param", out var values)
                ? values
                : [];
        if (action is null)
        {
            return new JsonObject
            {
                ["actionKey"] = actionKey,
                ["contractStatus"] = "ActionNotFound",
                ["parameters"] = new JsonArray()
            };
        }

        JsonArray contractParameters =
            action["parameters"] as JsonArray ?? [];
        var parameters = new JsonArray();
        for (int index = 0; index < rawValues.Count; index++)
        {
            JsonObject? contractParameter = FindParameter(
                contractParameters,
                index
            );
            parameters.Add(
                BuildParameter(
                    registry,
                    node.Key,
                    index,
                    rawValues[index],
                    contractParameter,
                    edges,
                    nodesByKey
                )
            );
        }

        return new JsonObject
        {
            ["actionKey"] = actionKey,
            ["actionLabel"] =
                StringValue(action, "label") ?? actionKey,
            ["contractStatus"] =
                rawValues.Count == 0
                    ? "NoParameters"
                    : "Ready",
            ["parameterCount"] = rawValues.Count,
            ["parameters"] = parameters
        };
    }

    private static JsonObject BuildParameter(
        JsonObject registry,
        string nodeKey,
        int index,
        string rawValue,
        JsonObject? contract,
        IReadOnlyList<SkillChainEdge> edges,
        IReadOnlyDictionary<string, SkillChainNode> nodesByKey
    )
    {
        var result = new JsonObject
        {
            ["index"] = index,
            ["rawValue"] = rawValue
        };
        if (contract is null)
        {
            result["resolutionStatus"] = "NotInContract";
            return result;
        }

        CopyString(contract, result, "key");
        CopyString(contract, result, "label");
        CopyString(contract, result, "description");
        CopyString(contract, result, "kind");
        CopyString(contract, result, "referenceTarget");
        CopyString(contract, result, "enumName");
        CopyString(contract, result, "unit");
        CopyString(contract, result, "conversionStatus");
        CopyNode(contract, result, "scale");
        CopyNode(contract, result, "conversionEvidence");

        string kind = StringValue(contract, "kind") ?? "";
        if (string.Equals(kind, "ScaledInteger", StringComparison.Ordinal))
        {
            ResolveScaledInteger(result, rawValue, contract);
        }
        else if (string.Equals(kind, "Enum", StringComparison.Ordinal))
        {
            ResolveEnum(registry, result, rawValue);
        }
        else if (
            string.Equals(kind, "Reference", StringComparison.Ordinal)
        )
        {
            ResolveReference(
                result,
                nodeKey,
                index,
                edges,
                nodesByKey
            );
        }
        else
        {
            result["resolutionStatus"] = "Resolved";
        }

        return result;
    }

    private static void ResolveScaledInteger(
        JsonObject result,
        string rawValue,
        JsonObject contract
    )
    {
        if (
            !decimal.TryParse(
                rawValue.Trim(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal raw
            )
            || !TryDecimal(contract["scale"], out decimal scale)
            || scale == 0
        )
        {
            result["resolutionStatus"] = "InvalidValue";
            return;
        }

        decimal logicalValue = raw / scale;
        result["logicalValue"] = FormatDecimal(logicalValue);
        result["resolutionStatus"] = "Resolved";
    }

    private static void ResolveEnum(
        JsonObject registry,
        JsonObject result,
        string rawValue
    )
    {
        string? enumName = StringValue(result, "enumName");
        if (
            string.IsNullOrWhiteSpace(enumName)
            || !int.TryParse(
                rawValue.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value
            )
        )
        {
            result["resolutionStatus"] = "Unresolved";
            return;
        }

        JsonArray enums = registry["enums"] as JsonArray ?? [];
        JsonObject? enumDefinition = enums
            .OfType<JsonObject>()
            .FirstOrDefault(
                item =>
                    string.Equals(
                        StringValue(item, "name"),
                        enumName,
                        StringComparison.OrdinalIgnoreCase
                    )
            );
        JsonObject? enumValue = (enumDefinition?["values"] as JsonArray)
            ?.OfType<JsonObject>()
            .FirstOrDefault(
                item =>
                    TryInt(item["value"], out int candidateValue)
                    && candidateValue == value
            );
        if (enumValue is null)
        {
            result["resolutionStatus"] = "Unresolved";
            return;
        }

        result["enumValue"] = enumValue.DeepClone();
        result["resolutionStatus"] = "Resolved";
    }

    private static void ResolveReference(
        JsonObject result,
        string nodeKey,
        int index,
        IReadOnlyList<SkillChainEdge> edges,
        IReadOnlyDictionary<string, SkillChainNode> nodesByKey
    )
    {
        SkillChainEdge? edge = edges.FirstOrDefault(
            candidate =>
                string.Equals(
                    candidate.Source,
                    nodeKey,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    candidate.SourceField,
                    "action_param",
                    StringComparison.Ordinal
                )
                && candidate.ParameterIndex == index
        );
        if (edge is null)
        {
            result["resolutionStatus"] = "Unresolved";
            return;
        }

        result["resolvedKey"] = edge.Target;
        if (nodesByKey.TryGetValue(edge.Target, out SkillChainNode? target))
        {
            result["resolvedLabel"] = target.Label;
            result["resolvedKind"] = target.Kind;
        }
        result["resolutionStatus"] = "Resolved";
    }

    private static JsonObject? FindAction(
        JsonObject registry,
        string category,
        string actionKey
    )
    {
        string collectionName = category == "effect"
            ? "effects"
            : "conditions";
        return (registry[collectionName] as JsonArray)
            ?.OfType<JsonObject>()
            .FirstOrDefault(
                action =>
                    string.Equals(
                        StringValue(action, "key"),
                        actionKey,
                        StringComparison.OrdinalIgnoreCase
                    )
            );
    }

    private static JsonObject? FindParameter(
        JsonArray parameters,
        int index
    )
    {
        return parameters
            .OfType<JsonObject>()
            .FirstOrDefault(
                parameter =>
                    TryInt(parameter["index"], out int candidateIndex)
                    && candidateIndex == index
            );
    }

    private static string Category(string @namespace)
    {
        return @namespace switch
        {
            "TbEffect" => "effect",
            "TbCondition" => "condition",
            _ => ""
        };
    }

    private static string? ActionKey(SkillChainNode node)
    {
        if (
            !node.Fields.TryGetValue("__executor", out var values)
            || values.Count == 0
        )
        {
            return null;
        }

        return values[0]
            .Split('·', StringSplitOptions.TrimEntries)
            .FirstOrDefault(
                part =>
                    part.Length > 1
                    && char.IsAsciiLetter(part[0])
                    && part.All(
                        character =>
                            char.IsAsciiLetterOrDigit(character)
                            || character == '_'
                    )
            );
    }

    private static void CopyString(
        JsonObject source,
        JsonObject target,
        string propertyName
    )
    {
        if (source.TryGetPropertyValue(propertyName, out JsonNode? value))
        {
            target[propertyName] = value?.DeepClone();
        }
    }

    private static void CopyNode(
        JsonObject source,
        JsonObject target,
        string propertyName
    )
    {
        if (source.TryGetPropertyValue(propertyName, out JsonNode? value))
        {
            target[propertyName] = value?.DeepClone();
        }
    }

    private static string? StringValue(
        JsonObject value,
        string propertyName
    )
    {
        return value[propertyName] is JsonValue node
            && node.TryGetValue(out string? text)
                ? text
                : null;
    }

    private static bool TryInt(JsonNode? value, out int result)
    {
        result = default;
        if (
            value is JsonValue node
            && node.TryGetValue(out result)
        )
        {
            return true;
        }

        return value is JsonValue textNode
            && textNode.TryGetValue(out string? text)
            && int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out result
            );
    }

    private static bool TryDecimal(JsonNode? value, out decimal result)
    {
        result = default;
        if (
            value is JsonValue node
            && node.TryGetValue(out result)
        )
        {
            return true;
        }

        return value is JsonValue textNode
            && textNode.TryGetValue(out string? text)
            && decimal.TryParse(
                text,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out result
            );
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString(
            "0.############################",
            CultureInfo.InvariantCulture
        );
    }
}
