using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TianshuDM.Domain.GameData;

namespace RtsSkillStudio.Agent.Patch;

public sealed record WorkbookPatchConversionRule(
    string Key,
    string InputUnit,
    string OutputUnit,
    decimal Factor,
    IReadOnlyList<string> InputAliases,
    IReadOnlyList<string> OutputAliases
);

public sealed record WorkbookPatchRegistryEntity(
    string Key,
    string Namespace,
    string Kind,
    string? Role
);

public sealed record WorkbookPatchRegistryField(
    string Path,
    string? SemanticName,
    string Kind,
    string? RawType,
    bool Required,
    decimal? Scale,
    string? Unit,
    decimal? Minimum,
    decimal? Maximum,
    string? EnumName,
    IReadOnlyList<string> Aliases
);

public sealed record WorkbookPatchActionParameter(
    int Index,
    string Key,
    string Label,
    string ContractKind,
    GameDataFieldKind FieldKind,
    string RawType,
    string? ReferenceTarget,
    string? EnumName,
    bool Required,
    decimal Scale,
    string? Unit,
    decimal? Minimum,
    decimal? Maximum,
    bool Repeating,
    int RepeatStep,
    IReadOnlyList<GameDataOption> Options
);

public sealed record WorkbookPatchAction(
    string Category,
    string Key,
    int? LegacyValue,
    IReadOnlyList<WorkbookPatchActionParameter> Parameters
);

public sealed record WorkbookPatchRegistry(
    int SchemaVersion,
    string CapabilityRegistryVersion,
    string DefaultValueContractVersion,
    string DefaultMechanismContractVersion,
    IReadOnlyList<WorkbookPatchRegistryEntity> Entities,
    IReadOnlyList<WorkbookPatchRegistryField> EntityFields,
    IReadOnlyList<WorkbookPatchConversionRule> ConversionRules,
    IReadOnlyList<WorkbookPatchAction> Actions
);

public sealed record WorkbookPatchWorkspace(
    string WorkspaceId,
    string Revision,
    string SourceHash,
    IReadOnlyList<WorkbookPatchTable> Tables,
    bool OwnershipPolicyPublished = false,
    bool OwnershipAllowed = true,
    bool EditLockPublished = false,
    bool IsEditLocked = false
);

public sealed record WorkbookPatchTable(
    string EntityKey,
    string Namespace,
    string TableKey,
    IReadOnlyList<WorkbookPatchField> Fields,
    IReadOnlyList<WorkbookPatchRecord> Records
);

public sealed record WorkbookPatchField(
    string Key,
    string Path,
    string? SemanticName,
    GameDataFieldKind Kind,
    string RawType,
    bool Required,
    decimal Scale,
    string? Unit,
    decimal? Minimum,
    decimal? Maximum,
    IReadOnlyList<GameDataOption> Options,
    string? ReferenceNamespace,
    WorkbookPatchFieldBindingKind BindingKind =
        WorkbookPatchFieldBindingKind.Scalar,
    int? RecordId = null,
    string? ActionKey = null,
    int? ParameterIndex = null,
    bool Repeating = false
);

public enum WorkbookPatchFieldBindingKind
{
    Scalar,
    ActionParameter
}

public sealed record WorkbookPatchRecord(
    int Id,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fields
);

public sealed record WorkbookPatchBase(
    string WorkspaceId,
    string Revision,
    string SourceHash,
    string CapabilityRegistryVersion,
    string DefaultValueContractVersion,
    string DefaultMechanismContractVersion
);

public sealed record WorkbookPatchAllocation(
    string Namespace,
    string LocalKey,
    int Id,
    string Kind,
    int? GroupKey = null
);

public sealed record WorkbookPatchAssetIdentity(
    string Namespace,
    int? Id = null,
    int? GroupKey = null,
    string? LocalKey = null,
    string? ParentField = null,
    int? ParameterIndex = null
);

public sealed record WorkbookPatchCommand(
    int Sequence,
    string OperationId,
    string Kind,
    WorkbookPatchAssetIdentity Target,
    JsonObject Arguments
);

public sealed record WorkbookPatchLogicalAddress(
    string TableKey,
    int RecordId,
    string Field
);

public sealed record WorkbookFieldChange(
    string OperationId,
    WorkbookPatchLogicalAddress LogicalAddress,
    string Namespace,
    int Id,
    string Field,
    string SemanticField,
    JsonElement? SemanticValue,
    string? SemanticUnit,
    JsonElement Before,
    JsonElement After,
    string Source,
    IReadOnlyList<string> Evidence
);

public sealed record WorkbookPatchDocument(
    int SchemaVersion,
    string PatchId,
    string SourcePlanHash,
    WorkbookPatchBase Base,
    IReadOnlyList<WorkbookPatchAllocation> Allocations,
    IReadOnlyList<WorkbookPatchCommand> Commands,
    IReadOnlyList<WorkbookFieldChange> FieldChanges
);

public sealed record WorkbookPatchCompileError(
    string Code,
    string Message,
    string? Subject = null
);

public sealed record WorkbookPatchCompileResult(
    string Status,
    WorkbookPatchDocument? Patch,
    string? PatchJson,
    IReadOnlyList<WorkbookPatchCompileError> Errors
);

public sealed record WorkbookPatchValidationCheck(
    string Code,
    string Status,
    string Severity,
    bool Required,
    string Message,
    string? Subject = null,
    IReadOnlyList<string>? Evidence = null
);

public sealed record WorkbookPatchValidationReport(
    int SchemaVersion,
    string PatchId,
    string Status,
    IReadOnlyList<WorkbookPatchValidationCheck> Checks
);

public sealed record WorkbookPatchDiffRow(
    string OperationId,
    string LogicalAddress,
    string TableKey,
    int RecordId,
    string Field,
    string SemanticField,
    string? SemanticValue,
    string? SemanticUnit,
    string Before,
    string After,
    string Source,
    IReadOnlyList<string> Evidence,
    bool IsNoOp
);

public sealed record WorkbookPatchWorkspaceSnapshot(
    string WorkspaceId,
    string Revision,
    string SourceHash,
    GameDataCatalog Catalog
);

public static class WorkbookPatchRecordValue
{
    public static string Read(
        WorkbookPatchRecord record,
        WorkbookPatchField field
    )
    {
        if (
            field.BindingKind != WorkbookPatchFieldBindingKind.ActionParameter
            || field.ParameterIndex is not int parameterIndex
        )
        {
            return record.Fields.TryGetValue(
                field.Key,
                out IReadOnlyList<string>? values
            )
                ? values.FirstOrDefault() ?? ""
                : "";
        }

        return record.Fields.TryGetValue(
                "action_param",
                out IReadOnlyList<string>? parameters
            )
            && parameterIndex >= 0
            && parameterIndex < parameters.Count
                ? parameters[parameterIndex]
                : "";
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Write(
        WorkbookPatchRecord record,
        WorkbookPatchField field,
        string value
    )
    {
        Dictionary<string, IReadOnlyList<string>> fields = record.Fields
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal
            );
        if (
            field.BindingKind != WorkbookPatchFieldBindingKind.ActionParameter
            || field.ParameterIndex is not int parameterIndex
        )
        {
            fields[field.Key] = [value];
            return fields;
        }

        List<string> parameters = fields.TryGetValue(
            "action_param",
            out IReadOnlyList<string>? existing
        )
            ? existing.ToList()
            : [];
        while (parameters.Count <= parameterIndex)
        {
            parameters.Add("");
        }

        parameters[parameterIndex] = value;
        fields["action_param"] = parameters;
        return fields;
    }
}

public static class WorkbookFieldChangeJson
{
    public static string RawText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => value.GetRawText()
        };
    }

    public static string? SemanticText(JsonElement? value)
    {
        return value is not { } element
            ? null
            : RawText(element);
    }
}

public static class WorkbookPatchJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize(WorkbookPatchDocument patch)
    {
        return JsonSerializer.Serialize(patch, Options);
    }

    public static WorkbookPatchDocument? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<WorkbookPatchDocument>(json, Options);
    }
}

internal enum WorkbookPatchValueFailureKind
{
    ValueType,
    Unit,
    Scale,
    Range,
    Enum
}

internal sealed record WorkbookPatchValueFailure(
    WorkbookPatchValueFailureKind Kind,
    string Message
);

internal static class WorkbookPatchValueConverter
{
    public static bool TryConvert(
        JsonElement value,
        string? planUnit,
        WorkbookPatchField field,
        IReadOnlyList<WorkbookPatchConversionRule> conversionRules,
        out string rawValue,
        out WorkbookPatchValueFailure? failure
    )
    {
        rawValue = "";
        failure = null;

        if (
            field.Kind is GameDataFieldKind.List
                or GameDataFieldKind.Map
                or GameDataFieldKind.DelimitedList
        )
        {
            failure = new WorkbookPatchValueFailure(
                WorkbookPatchValueFailureKind.ValueType,
                $"字段 {field.Path} 不是阶段 B 支持的标量字段。"
            );
            return false;
        }

        if (field.Kind == GameDataFieldKind.Boolean)
        {
            return TryConvertBoolean(value, out rawValue, out failure);
        }

        if (field.Kind == GameDataFieldKind.Enum)
        {
            return TryConvertEnum(value, field, out rawValue, out failure);
        }

        if (field.Kind == GameDataFieldKind.Text)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                failure = new WorkbookPatchValueFailure(
                    WorkbookPatchValueFailureKind.ValueType,
                    $"字段 {field.Path} 需要字符串值。"
                );
                return false;
            }

            rawValue = value.GetString() ?? "";
            return true;
        }

        if (!TryReadDecimal(value, out decimal semanticValue))
        {
            failure = new WorkbookPatchValueFailure(
                WorkbookPatchValueFailureKind.ValueType,
                $"字段 {field.Path} 需要数值。"
            );
            return false;
        }

        if (
            !TryConvertUnit(
                semanticValue,
                planUnit,
                field,
                conversionRules,
                out decimal converted,
                out failure
            )
        )
        {
            return false;
        }

        if (field.Scale <= 0)
        {
            failure = new WorkbookPatchValueFailure(
                WorkbookPatchValueFailureKind.Scale,
                $"字段 {field.Path} 的 scale 必须大于 0。"
            );
            return false;
        }

        decimal raw = converted * field.Scale;
        if (
            field.Kind is GameDataFieldKind.Integer
                or GameDataFieldKind.Reference
            && raw != decimal.Truncate(raw)
        )
        {
            failure = new WorkbookPatchValueFailure(
                WorkbookPatchValueFailureKind.ValueType,
                $"字段 {field.Path} 的最终单元格值必须是整数。"
            );
            return false;
        }

        if (field.Minimum is { } minimum && raw < minimum)
        {
            failure = new WorkbookPatchValueFailure(
                WorkbookPatchValueFailureKind.Range,
                $"字段 {field.Path} 的值低于最小值 {minimum}。"
            );
            return false;
        }

        if (field.Maximum is { } maximum && raw > maximum)
        {
            failure = new WorkbookPatchValueFailure(
                WorkbookPatchValueFailureKind.Range,
                $"字段 {field.Path} 的值高于最大值 {maximum}。"
            );
            return false;
        }

        rawValue = raw.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    public static bool IsRawValueCompatible(
        string rawValue,
        WorkbookPatchField field
    )
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return !field.Required;
        }

        return field.Kind switch
        {
            GameDataFieldKind.Text => true,
            GameDataFieldKind.Boolean => rawValue is "0" or "1"
                || bool.TryParse(rawValue, out _),
            GameDataFieldKind.Enum => EnumMatches(field, rawValue),
            GameDataFieldKind.Integer or GameDataFieldKind.Reference =>
                long.TryParse(
                    rawValue,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _
                ),
            _ => false
        };
    }

    private static bool TryConvertBoolean(
        JsonElement value,
        out string rawValue,
        out WorkbookPatchValueFailure? failure
    )
    {
        rawValue = "";
        failure = null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            rawValue = value.GetBoolean() ? "1" : "0";
            return true;
        }

        if (
            value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.GetString(), out bool parsed)
        )
        {
            rawValue = parsed ? "1" : "0";
            return true;
        }

        failure = new WorkbookPatchValueFailure(
            WorkbookPatchValueFailureKind.ValueType,
            "布尔字段需要 true/false 值。"
        );
        return false;
    }

    private static bool TryConvertEnum(
        JsonElement value,
        WorkbookPatchField field,
        out string rawValue,
        out WorkbookPatchValueFailure? failure
    )
    {
        rawValue = "";
        failure = null;
        string? candidate = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(candidate))
        {
            failure = new WorkbookPatchValueFailure(
                WorkbookPatchValueFailureKind.ValueType,
                $"字段 {field.Path} 需要枚举名称或枚举值。"
            );
            return false;
        }

        if (field.Options.Count == 0)
        {
            rawValue = candidate;
            return true;
        }

        GameDataOption? option = field.Options.FirstOrDefault(
            item =>
                string.Equals(item.Value, candidate, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Label, candidate, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Code, candidate, StringComparison.OrdinalIgnoreCase)
                || (
                    item.LegacyValue is { } legacy
                    && string.Equals(
                        legacy.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                        candidate,
                        StringComparison.Ordinal
                    )
                )
        );
        if (option is null)
        {
            failure = new WorkbookPatchValueFailure(
                WorkbookPatchValueFailureKind.Enum,
                $"字段 {field.Path} 不接受枚举值 {candidate}。"
            );
            return false;
        }

        rawValue = option.Value;
        return true;
    }

    private static bool TryConvertUnit(
        decimal semanticValue,
        string? planUnit,
        WorkbookPatchField field,
        IReadOnlyList<WorkbookPatchConversionRule> conversionRules,
        out decimal converted,
        out WorkbookPatchValueFailure? failure
    )
    {
        converted = semanticValue;
        failure = null;
        if (string.IsNullOrWhiteSpace(field.Unit))
        {
            if (!string.IsNullOrWhiteSpace(planUnit))
            {
                failure = new WorkbookPatchValueFailure(
                    WorkbookPatchValueFailureKind.Unit,
                    $"字段 {field.Path} 未声明目标单位，不能接受 unit={planUnit}。"
                );
                return false;
            }

            return true;
        }

        if (string.IsNullOrWhiteSpace(planUnit))
        {
            failure = new WorkbookPatchValueFailure(
                WorkbookPatchValueFailureKind.Unit,
                $"字段 {field.Path} 的值必须声明 unit={field.Unit}。"
            );
            return false;
        }

        string sourceUnit = NormalizeUnit(planUnit);
        string targetUnit = NormalizeUnit(field.Unit);
        if (string.Equals(sourceUnit, targetUnit, StringComparison.Ordinal))
        {
            return true;
        }

        WorkbookPatchConversionRule? rule = conversionRules.FirstOrDefault(
            item =>
                UnitMatches(item.InputUnit, item.InputAliases, sourceUnit)
                && UnitMatches(item.OutputUnit, item.OutputAliases, targetUnit)
        );
        if (rule is null)
        {
            failure = new WorkbookPatchValueFailure(
                WorkbookPatchValueFailureKind.Unit,
                $"字段 {field.Path} 不能把单位 {planUnit} 转换为 {field.Unit}。"
            );
            return false;
        }

        converted = semanticValue * rule.Factor;
        return true;
    }

    private static bool TryReadDecimal(JsonElement value, out decimal result)
    {
        result = 0;
        if (
            value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out result)
        )
        {
            return true;
        }

        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out result
            );
    }

    private static bool EnumMatches(
        WorkbookPatchField field,
        string rawValue
    )
    {
        if (field.Options.Count == 0)
        {
            return true;
        }

        return field.Options.Any(
            option =>
                string.Equals(option.Value, rawValue, StringComparison.OrdinalIgnoreCase)
                || string.Equals(option.Label, rawValue, StringComparison.OrdinalIgnoreCase)
                || string.Equals(option.Code, rawValue, StringComparison.OrdinalIgnoreCase)
                || (
                    option.LegacyValue is { } legacy
                    && string.Equals(
                        legacy.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                        rawValue,
                        StringComparison.Ordinal
                    )
                )
        );
    }

    private static string NormalizeUnit(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static bool UnitMatches(
        string unit,
        IReadOnlyList<string> aliases,
        string candidate
    )
    {
        string normalized = NormalizeUnit(unit);
        return string.Equals(normalized, candidate, StringComparison.Ordinal)
            || aliases.Any(
                alias =>
                    string.Equals(
                        NormalizeUnit(alias),
                        candidate,
                        StringComparison.Ordinal
                    )
            );
    }
}
