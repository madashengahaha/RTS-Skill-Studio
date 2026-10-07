using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RtsSkillStudio.Agent.Llm;

public sealed record DamageChangeSummary(
    string Text,
    string? ConfirmationQuestion
);

public static partial class DamageChangeSummaryBuilder
{
    public static DamageChangeSummary? TryBuild(
        string message,
        IReadOnlyList<AgentToolExecution> executions
    )
    {
        Match request = DamageChangeRegex().Match(message);
        if (
            !request.Success
            || !decimal.TryParse(
                request.Groups["value"].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal logicalDamage
            )
        )
        {
            return null;
        }

        DamageNode? damageNode = FindDamageNode(executions);
        DamageContract? contract = FindDamageContract(executions);
        if (
            damageNode is null
            || contract is null
            || damageNode.ActionParameters.Count < 4
        )
        {
            return null;
        }

        string? elementToken = request.Groups["element"].Success
            ? request.Groups["element"].Value
            : null;
        DamageEnumValue? enumOption = elementToken is null
            ? null
            : contract.FindEnumValueByAlias("NumericType", elementToken);
        if (elementToken is not null && enumOption is null)
        {
            return null;
        }
        int? enumValue = enumOption?.Value;

        int scale = contract.FixedDamageScale;
        decimal rawDamage = logicalDamage * scale;
        if (rawDamage != decimal.Truncate(rawDamage))
        {
            return null;
        }

        string[] targetParameters = damageNode.ActionParameters.ToArray();
        if (enumValue is not null)
        {
            targetParameters[1] = enumValue.Value.ToString(
                CultureInfo.InvariantCulture
            );
        }
        targetParameters[2] = ((long)rawDamage).ToString(
            CultureInfo.InvariantCulture
        );

        string currentParameters = FormatParameters(
            damageNode.ActionParameters
        );
        string targetParametersText = FormatParameters(targetParameters);
        int currentAttackType = ParseInt(
            damageNode.ActionParameters.ElementAtOrDefault(1)
        );
        int currentFixedDamage = ParseInt(
            damageNode.ActionParameters.ElementAtOrDefault(2)
        );
        string attackTypeBefore = currentAttackType == 0
            ? damageNode.ActionParameters.ElementAtOrDefault(1) ?? ""
            : $"{currentAttackType} ({contract.FindEnumName("NumericType", currentAttackType) ?? "unknown"})";
        string attackTypeAfter = enumValue is null
            ? attackTypeBefore
            : $"{enumValue.Value} ({enumOption!.Name})";

        var builder = new StringBuilder();
        builder.AppendLine("**Excel 修改清单**");
        builder.AppendLine();
        builder.AppendLine(
            "| Excel 表 | 记录 | 字段 | 当前值 | 目标值 | 依据 |"
        );
        builder.AppendLine("|---|---|---|---|---|---|");
        builder.AppendLine(
            $"| `{damageNode.TableKey}` | `Id={damageNode.Id}`（第 {damageNode.SourceRow} 行） | `action_param[1]` `attackType` | `{attackTypeBefore}` | `{attackTypeAfter}` | `NumericType` 精确枚举名解析 |"
        );
        builder.AppendLine(
            $"| `{damageNode.TableKey}` | `Id={damageNode.Id}`（第 {damageNode.SourceRow} 行） | `action_param[2]` `fixedDamage` | `{currentFixedDamage}` | `{(long)rawDamage}` | 契约 `scale={scale}`；逻辑值 `{logicalDamage.ToString(CultureInfo.InvariantCulture)}` |"
        );
        builder.AppendLine();
        builder.AppendLine(
            $"建议 `action_param`：`{currentParameters}` → `{targetParametersText}`"
        );
        builder.AppendLine();
        builder.AppendLine(
            "这是字段级修改建议，当前阶段不会执行 Excel 写入。"
        );

        string? confirmation = null;
        if (
            message.Contains("固定", StringComparison.Ordinal)
            && damageNode.ActionParameters.Count >= 4
        )
        {
            confirmation =
                $"如果“固定 {logicalDamage.ToString(CultureInfo.InvariantCulture)}”表示最终伤害不再叠加攻击力倍率，需要同时确认 `action_param[3]` `attackScale` 是否从 `10000` 改为 `0`。";
        }

        return new DamageChangeSummary(
            builder.ToString().Trim(),
            confirmation
        );
    }

    private static DamageNode? FindDamageNode(
        IEnumerable<AgentToolExecution> executions
    )
    {
        foreach (AgentToolExecution execution in executions.Where(
            execution => string.Equals(
                execution.Name,
                "get_graph",
                StringComparison.Ordinal
            )
        ))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    execution.ResultJson
                );
                if (
                    !TryGetProperty(
                        document.RootElement,
                        "nodes",
                        out JsonElement nodes
                    )
                    || nodes.ValueKind != JsonValueKind.Array
                )
                {
                    continue;
                }

                foreach (JsonElement node in nodes.EnumerateArray())
                {
                    if (
                        !TryGetProperty(node, "fields", out JsonElement fields)
                        || fields.ValueKind != JsonValueKind.Object
                    )
                    {
                        continue;
                    }

                    string actionType = ReadFirstArrayString(
                        fields,
                        "action_type"
                    );
                    string executor = ReadFirstArrayString(
                        fields,
                        "__executor"
                    );
                    if (
                        !actionType.Contains(
                            "伤害",
                            StringComparison.Ordinal
                        )
                        && !executor.Contains(
                            "Damage",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        continue;
                    }

                    IReadOnlyList<string> parameters = ReadArrayStrings(
                        fields,
                        "action_param"
                    );
                    if (parameters.Count == 0)
                    {
                        continue;
                    }

                    return new DamageNode(
                        ReadInt(node, "id"),
                        ReadString(node, "tableKey") ?? "effect",
                        ReadInt(node, "sourceRow"),
                        parameters
                    );
                }
            }
            catch (JsonException)
            {
                // Ignore malformed tool output and keep looking.
            }
        }

        return null;
    }

    private static DamageContract? FindDamageContract(
        IEnumerable<AgentToolExecution> executions
    )
    {
        foreach (AgentToolExecution execution in executions.Where(
            execution => string.Equals(
                execution.Name,
                "get_capability_context",
                StringComparison.Ordinal
            )
        ))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    execution.ResultJson
                );
                if (
                    !TryGetProperty(
                        document.RootElement,
                        "effects",
                        out JsonElement effects
                    )
                    || effects.ValueKind != JsonValueKind.Array
                )
                {
                    continue;
                }

                foreach (JsonElement effect in effects.EnumerateArray())
                {
                    if (
                        !string.Equals(
                            ReadString(effect, "key"),
                            "Damage",
                            StringComparison.OrdinalIgnoreCase
                        )
                        || !TryGetProperty(
                            effect,
                            "parameters",
                            out JsonElement parameters
                        )
                        || parameters.ValueKind != JsonValueKind.Array
                    )
                    {
                        continue;
                    }

                    int fixedScale = 0;
                    foreach (JsonElement parameter in parameters.EnumerateArray())
                    {
                        string key = ReadString(parameter, "key") ?? "";
                        if (
                            string.Equals(
                                key,
                                "fixedDamage",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            fixedScale = ReadInt(parameter, "scale");
                        }
                    }
                    if (fixedScale <= 0)
                    {
                        continue;
                    }

                    if (
                        !TryGetProperty(
                            document.RootElement,
                            "enums",
                            out JsonElement enums
                        )
                        || enums.ValueKind != JsonValueKind.Array
                    )
                    {
                        continue;
                    }

                    var enumValues =
                        new Dictionary<
                            string,
                            IReadOnlyList<DamageEnumValue>
                        >(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonElement item in enums.EnumerateArray())
                    {
                        string enumName = ReadString(item, "name") ?? "";
                        if (
                            string.IsNullOrWhiteSpace(enumName)
                            || !TryGetProperty(
                                item,
                                "values",
                                out JsonElement values
                            )
                            || values.ValueKind != JsonValueKind.Array
                        )
                        {
                            continue;
                        }

                        enumValues[enumName] = values
                            .EnumerateArray()
                            .Select(
                                value => new DamageEnumValue(
                                    ReadString(value, "name") ?? "",
                                    ReadInt(value, "value"),
                                    ReadAliases(value)
                                )
                            )
                            .Where(
                                value =>
                                    !string.IsNullOrWhiteSpace(value.Name)
                            )
                            .ToArray();
                    }

                    return new DamageContract(fixedScale, enumValues);
                }
            }
            catch (JsonException)
            {
                // Ignore malformed tool output and keep looking.
            }
        }

        return null;
    }

    private static string FormatParameters(IEnumerable<string> parameters)
    {
        return "["
            + string.Join(
                ", ",
                parameters.Select(
                    value =>
                        "\""
                        + value.Replace("\"", "\\\"", StringComparison.Ordinal)
                        + "\""
                )
            )
            + "]";
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int result
        )
            ? result
            : 0;
    }

    private static string ReadFirstArrayString(
        JsonElement node,
        string propertyName
    )
    {
        return ReadArrayStrings(node, propertyName).FirstOrDefault() ?? "";
    }

    private static IReadOnlyList<string> ReadArrayStrings(
        JsonElement node,
        string propertyName
    )
    {
        if (
            !TryGetProperty(node, propertyName, out JsonElement values)
            || values.ValueKind != JsonValueKind.Array
        )
        {
            return [];
        }

        return values
            .EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? "")
            .ToArray();
    }

    private static string? ReadString(
        JsonElement node,
        string propertyName
    )
    {
        return TryGetProperty(node, propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int ReadInt(JsonElement node, string propertyName)
    {
        return TryGetProperty(node, propertyName, out JsonElement value)
            && value.TryGetInt32(out int result)
            ? result
            : 0;
    }

    private static IReadOnlyList<string> ReadAliases(JsonElement value)
    {
        return new[]
            {
                ReadString(value, "alias") ?? "",
                ReadString(value, "comment") ?? ""
            }
            .SelectMany(
                text => text.Split(
                    [',', '，', '/', ';', '；'],
                    StringSplitOptions.RemoveEmptyEntries
                        | StringSplitOptions.TrimEntries
                )
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryGetProperty(
        JsonElement node,
        string propertyName,
        out JsonElement value
    )
    {
        if (node.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (JsonProperty property in node.EnumerateObject())
        {
            if (
                string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    [GeneratedRegex(
        @"(?:(?:固定(?:为)?|改成|改为|设为|设置成|调整到|调到)\s*(?<value>\d+(?:\.\d+)?)\s*(?<element>[\p{IsCJKUnifiedIdeographs}]{1,8})?\s*(?:点)?\s*伤害)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex DamageChangeRegex();

    private sealed record DamageNode(
        int Id,
        string TableKey,
        int SourceRow,
        IReadOnlyList<string> ActionParameters
    );

    private sealed record DamageContract(
        int FixedDamageScale,
        IReadOnlyDictionary<string, IReadOnlyList<DamageEnumValue>> Enums
    )
    {
        public DamageEnumValue? FindEnumValueByAlias(
            string enumName,
            string valueName
        )
        {
            if (!Enums.TryGetValue(enumName, out var values))
            {
                return null;
            }

            return values.FirstOrDefault(
                value =>
                    string.Equals(
                        value.Name,
                        valueName,
                        StringComparison.OrdinalIgnoreCase
                    )
                    || value.Aliases.Any(
                        alias => string.Equals(
                            alias,
                            valueName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
            );
        }

        public string? FindEnumName(string enumName, int enumValue)
        {
            if (!Enums.TryGetValue(enumName, out var values))
            {
                return null;
            }

            return values.FirstOrDefault(
                value => value.Value == enumValue
            )?.Name;
        }
    }

    private sealed record DamageEnumValue(
        string Name,
        int Value,
        IReadOnlyList<string> Aliases
    );
}
