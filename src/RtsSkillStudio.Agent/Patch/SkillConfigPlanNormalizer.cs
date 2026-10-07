using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RtsSkillStudio.Agent.Patch;

public sealed class SkillConfigPlanNormalizer(
    WorkbookPatchRegistry registry
)
{
    public string Normalize(
        string planJson,
        string userMessage,
        WorkbookPatchWorkspaceSnapshot workspace
    )
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(planJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return planJson;
        }

        if (node is not JsonObject plan)
        {
            return planJson;
        }

        NormalizeBase(plan, workspace);
        NormalizeAssumptions(plan);
        NormalizeOperations(plan, userMessage);
        return plan.ToJsonString();
    }

    private void NormalizeBase(
        JsonObject plan,
        WorkbookPatchWorkspaceSnapshot workspace
    )
    {
        if (plan["base"] is not JsonObject identity)
        {
            identity = new JsonObject();
            plan["base"] = identity;
        }

        identity["workspaceId"] = workspace.WorkspaceId;
        identity["revision"] = workspace.Revision;
        identity["sourceHash"] = workspace.SourceHash;
        identity["capabilityRegistryVersion"] =
            registry.CapabilityRegistryVersion;
        identity["defaultValueContractVersion"] =
            registry.DefaultValueContractVersion;
        identity["defaultMechanismContractVersion"] =
            registry.DefaultMechanismContractVersion;
    }

    private static void NormalizeAssumptions(JsonObject plan)
    {
        if (plan["assumptions"] is not JsonArray assumptions)
        {
            return;
        }

        foreach (JsonObject assumption in assumptions.OfType<JsonObject>())
        {
            string? reason = ReadString(assumption, "reason");
            if (assumption["value"] is JsonObject value)
            {
                if (
                    value.TryGetPropertyValue(
                        "reason",
                        out JsonNode? valueReason
                    )
                    && valueReason is not null
                )
                {
                    reason ??= valueReason.GetValue<string>();
                    value.Remove("reason");
                }

                if (!value.ContainsKey("source"))
                {
                    value["source"] = "ModelProposed";
                }

                if (!value.ContainsKey("evidence"))
                {
                    value["evidence"] = new JsonArray();
                }
            }

            if (!assumption.ContainsKey("reason"))
            {
                assumption["reason"] = reason ?? "Plan normalization";
            }

            if (!assumption.ContainsKey("requiresConfirmation"))
            {
                assumption["requiresConfirmation"] = false;
            }
        }
    }

    private void NormalizeOperations(
        JsonObject plan,
        string userMessage
    )
    {
        if (plan["operations"] is not JsonArray operations)
        {
            return;
        }

        foreach (JsonObject operation in operations.OfType<JsonObject>())
        {
            string kind = ReadString(operation, "kind") ?? "";
            if (
                !string.Equals(
                    kind,
                    "ModifySkill",
                    StringComparison.Ordinal
                )
                || operation["fields"] is not JsonObject fields
            )
            {
                continue;
            }

            var replacements = new List<(
                string Source,
                string Semantic,
                JsonNode? Value
            )>();
            foreach (
                KeyValuePair<string, JsonNode?> field in fields
                    .ToArray()
            )
            {
                WorkbookPatchRegistryField? metadata = ResolveField(
                    "Skill",
                    field.Key
                );
                if (
                    metadata?.SemanticName is not { Length: > 0 } semantic
                    || string.Equals(
                        semantic,
                        field.Key,
                        StringComparison.Ordinal
                    )
                )
                {
                    NormalizeValue(field.Value, metadata, userMessage);
                    continue;
                }

                replacements.Add((field.Key, semantic, field.Value));
            }

            foreach (
                (string source, string semantic, JsonNode? value) in replacements
            )
            {
                fields.Remove(source);
                if (!fields.ContainsKey(semantic))
                {
                    fields[semantic] = value?.DeepClone();
                }
            }

            foreach (
                KeyValuePair<string, JsonNode?> field in fields.ToArray()
            )
            {
                WorkbookPatchRegistryField? metadata = ResolveField(
                    "Skill",
                    field.Key
                );
                NormalizeValue(field.Value, metadata, userMessage);
            }
        }
    }

    private void NormalizeValue(
        JsonNode? node,
        WorkbookPatchRegistryField? metadata,
        string userMessage
    )
    {
        if (node is not JsonObject value)
        {
            return;
        }

        if (!value.ContainsKey("source"))
        {
            value["source"] = "ModelProposed";
        }

        if (!value.ContainsKey("evidence"))
        {
            value["evidence"] = new JsonArray();
        }

        if (
            !string.IsNullOrWhiteSpace(ReadString(value, "unit"))
            || string.IsNullOrWhiteSpace(metadata?.Unit)
        )
        {
            return;
        }

        string? inferredUnit = InferUnit(
            value["value"],
            userMessage,
            metadata.Unit
        );
        if (!string.IsNullOrWhiteSpace(inferredUnit))
        {
            value["unit"] = inferredUnit;
        }
    }

    private WorkbookPatchRegistryField? ResolveField(
        string entityKey,
        string fieldName
    )
    {
        WorkbookPatchRegistryField[] candidates = registry.EntityFields
            .Where(
                item => item.Path.StartsWith(
                    $"{entityKey}.",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();
        return candidates.FirstOrDefault(
                item => string.Equals(
                    item.SemanticName,
                    fieldName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            ?? candidates.FirstOrDefault(
                item => string.Equals(
                    item.Path[(entityKey.Length + 1)..],
                    fieldName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            ?? candidates.FirstOrDefault(
                item => item.Aliases.Any(
                    alias => string.Equals(
                        alias,
                        fieldName,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            );
    }

    private string? InferUnit(
        JsonNode? valueNode,
        string userMessage,
        string targetUnit
    )
    {
        if (
            valueNode is null
            || !decimal.TryParse(
                valueNode.ToJsonString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal value
            )
        )
        {
            return null;
        }

        var aliases = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (WorkbookPatchConversionRule rule in registry.ConversionRules)
        {
            if (!UnitMatches(rule.OutputUnit, rule.OutputAliases, targetUnit))
            {
                continue;
            }

            aliases.Add(rule.InputUnit);
            foreach (string alias in rule.InputAliases)
            {
                aliases.Add(alias);
            }
        }

        aliases.Add(targetUnit);
        foreach (WorkbookPatchConversionRule rule in registry.ConversionRules)
        {
            if (UnitMatches(rule.OutputUnit, rule.OutputAliases, targetUnit))
            {
                foreach (string alias in rule.OutputAliases)
                {
                    aliases.Add(alias);
                }
            }
        }

        string alternatives = string.Join(
            "|",
            aliases
                .Where(alias => alias.Length > 0)
                .OrderByDescending(alias => alias.Length)
                .Select(Regex.Escape)
        );
        if (alternatives.Length == 0)
        {
            return null;
        }

        Match match = Regex.Match(
            userMessage,
            $@"(?<value>-?\d+(?:\.\d+)?)\s*(?<unit>{alternatives})",
            RegexOptions.IgnoreCase
        );
        if (!match.Success)
        {
            return null;
        }

        if (
            !decimal.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal messageValue
            )
            || messageValue != value
        )
        {
            return null;
        }

        return match.Groups["unit"].Value;
    }

    private static bool UnitMatches(
        string unit,
        IReadOnlyList<string> aliases,
        string candidate
    )
    {
        string normalized = candidate.Trim().ToLowerInvariant();
        return string.Equals(
                unit.Trim(),
                normalized,
                StringComparison.OrdinalIgnoreCase
            )
            || aliases.Any(
                alias => string.Equals(
                    alias.Trim(),
                    normalized,
                    StringComparison.OrdinalIgnoreCase
                )
            );
    }

    private static string? ReadString(
        JsonObject owner,
        string property
    )
    {
        return owner.TryGetPropertyValue(
                property,
                out JsonNode? value
            )
            && value is JsonValue
            && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : null;
    }
}
