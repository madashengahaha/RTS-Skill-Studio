using System.Text.Json;
using System.Text.RegularExpressions;

namespace RtsSkillStudio.Agent.Llm;

public static partial class SkillConfigPlanParser
{
    private static readonly HashSet<string> AllowedOperationKinds =
        new(StringComparer.Ordinal)
        {
            "CreateSkill",
            "ModifySkill",
            "AddEffectIntent",
            "ModifyEffectIntent",
            "DeleteEffectIntent",
            "AddConditionIntent",
            "ModifyConditionIntent",
            "DeleteConditionIntent",
            "LinkExisting",
            "RemoveLink",
            "ReorderMembers"
        };

    public static SkillConfigPlanExtraction Extract(string text)
    {
        Match? match = JsonFenceRegex().Matches(text).LastOrDefault();
        if (match is null || !match.Success)
        {
            return new SkillConfigPlanExtraction(null, []);
        }

        string json = match.Groups["json"].Value.Trim();
        var errors = new List<string>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            ValidateRoot(document.RootElement, errors);
            return new SkillConfigPlanExtraction(
                document.RootElement.GetRawText(),
                errors
            );
        }
        catch (JsonException exception)
        {
            errors.Add($"Plan JSON 无法解析：{exception.Message}");
            return new SkillConfigPlanExtraction(json, errors);
        }
    }

    public static string RemovePlanBlock(string text)
    {
        return JsonFenceRegex().Replace(text, "").Trim();
    }

    private static void ValidateRoot(JsonElement root, ICollection<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("SkillConfigPlan 必须是 JSON 对象。");
            return;
        }

        if (
            !root.TryGetProperty("schemaVersion", out JsonElement schemaVersion)
            || schemaVersion.ValueKind != JsonValueKind.Number
            || schemaVersion.GetInt32() != 0
        )
        {
            errors.Add("schemaVersion 必须为 0。");
        }

        RequireNonEmptyString(root, "planId", errors);
        ValidateBase(root, errors);
        ValidateRequest(root, errors);

        string status = GetString(root, "status") ?? "";
        if (status is not ("Ready" or "NeedsClarification" or "Unsupported"))
        {
            errors.Add(
                "status 必须是 Ready、NeedsClarification 或 Unsupported。"
            );
        }

        if (
            !root.TryGetProperty("operations", out JsonElement operations)
            || operations.ValueKind != JsonValueKind.Array
        )
        {
            errors.Add("operations 必须是数组。");
            return;
        }

        if (status == "Ready" && operations.GetArrayLength() == 0)
        {
            errors.Add("Ready 状态的 Plan 至少需要一个 operation。");
        }

        if (status == "NeedsClarification" && !HasNonEmptyArray(root, "clarifications"))
        {
            errors.Add("NeedsClarification 状态必须提供 clarifications。");
        }

        if (status == "Unsupported" && !HasNonEmptyArray(root, "unsupported"))
        {
            errors.Add("Unsupported 状态必须提供 unsupported。");
        }

        int index = 0;
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            ValidateOperation(operation, index, errors);
            index++;
        }
    }

    private static void ValidateBase(
        JsonElement root,
        ICollection<string> errors
    )
    {
        if (
            !root.TryGetProperty("base", out JsonElement baseValue)
            || baseValue.ValueKind != JsonValueKind.Object
        )
        {
            errors.Add("base 必须是对象。");
            return;
        }

        foreach (
            string property in new[]
            {
                "workspaceId",
                "revision",
                "capabilityRegistryVersion",
                "defaultValueContractVersion",
                "defaultMechanismContractVersion"
            }
        )
        {
            RequireNonEmptyString(baseValue, property, errors);
        }
    }

    private static void ValidateRequest(
        JsonElement root,
        ICollection<string> errors
    )
    {
        if (
            !root.TryGetProperty("request", out JsonElement request)
            || request.ValueKind != JsonValueKind.Object
        )
        {
            errors.Add("request 必须是对象。");
            return;
        }

        RequireNonEmptyString(request, "text", errors);
    }

    private static void ValidateOperation(
        JsonElement operation,
        int index,
        ICollection<string> errors
    )
    {
        if (operation.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"operations[{index}] 必须是对象。");
            return;
        }

        string prefix = $"operations[{index}]";
        RequireNonEmptyString(operation, "operationId", errors, prefix);
        string kind = GetString(operation, "kind") ?? "";
        if (!AllowedOperationKinds.Contains(kind))
        {
            errors.Add($"{prefix}.kind 不受支持：{kind}");
        }

        switch (kind)
        {
            case "ModifySkill":
                RequireObject(operation, "skill", errors, prefix);
                RequireObject(operation, "fields", errors, prefix);
                break;
            case "CreateSkill":
                RequireObject(operation, "owner", errors, prefix);
                RequireObject(operation, "skillType", errors, prefix);
                RequireObject(operation, "target", errors, prefix);
                break;
            case "AddEffectIntent":
            case "AddConditionIntent":
                RequireObject(operation, "owner", errors, prefix);
                RequireNonEmptyString(operation, "intent", errors, prefix);
                RequireObject(operation, "params", errors, prefix);
                break;
            case "ModifyEffectIntent":
            case "ModifyConditionIntent":
                RequireObject(operation, "owner", errors, prefix);
                RequireNonEmptyString(operation, "intentRef", errors, prefix);
                RequireObject(operation, "fields", errors, prefix);
                break;
            case "DeleteEffectIntent":
            case "DeleteConditionIntent":
                RequireObject(operation, "owner", errors, prefix);
                RequireNonEmptyString(operation, "intentRef", errors, prefix);
                break;
            case "LinkExisting":
            case "RemoveLink":
                RequireObject(operation, "parent", errors, prefix);
                RequireNonEmptyString(operation, "field", errors, prefix);
                RequireObject(operation, "target", errors, prefix);
                break;
            case "ReorderMembers":
                RequireObject(operation, "group", errors, prefix);
                if (
                    !operation.TryGetProperty(
                        "orderedMembers",
                        out JsonElement orderedMembers
                    )
                    || orderedMembers.ValueKind != JsonValueKind.Array
                )
                {
                    errors.Add($"{prefix}.orderedMembers 必须是数组。");
                }
                break;
        }
    }

    private static bool HasNonEmptyArray(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() > 0;
    }

    private static void RequireObject(
        JsonElement owner,
        string propertyName,
        ICollection<string> errors,
        string prefix = ""
    )
    {
        if (
            !owner.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Object
        )
        {
            errors.Add(
                $"{prefix}{(prefix.Length > 0 ? "." : "")}{propertyName} 必须是对象。"
            );
        }
    }

    private static void RequireNonEmptyString(
        JsonElement owner,
        string propertyName,
        ICollection<string> errors,
        string prefix = ""
    )
    {
        string value = GetString(owner, propertyName) ?? "";
        if (value.Length == 0)
        {
            errors.Add(
                $"{prefix}{(prefix.Length > 0 ? "." : "")}{propertyName} 不能为空。"
            );
        }
    }

    private static string? GetString(JsonElement owner, string propertyName)
    {
        return owner.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    [GeneratedRegex(
        @"```json\s*(?<json>\{.*?\})\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    )]
    private static partial Regex JsonFenceRegex();
}

public sealed record SkillConfigPlanExtraction(
    string? PlanJson,
    IReadOnlyList<string> Errors
);
