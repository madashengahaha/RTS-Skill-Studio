using System.Text.Json;
using System.Text.Json.Nodes;

namespace RtsSkillStudio.Agent.Patch;

public sealed class WorkbookPatchCompiler(WorkbookPatchRegistry registry)
{
    public WorkbookPatchCompileResult Compile(
        string planJson,
        WorkbookPatchWorkspace workspace
    )
    {
        try
        {
            return CompileCore(planJson, workspace);
        }
        catch (JsonException exception)
        {
            return Invalid(
                "compiler.invalid_plan",
                $"Plan JSON 无法解析：{exception.Message}"
            );
        }
    }

    private WorkbookPatchCompileResult CompileCore(
        string planJson,
        WorkbookPatchWorkspace workspace
    )
    {
        using JsonDocument document = JsonDocument.Parse(planJson);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid(
                "compiler.invalid_plan",
                "Plan 根节点必须是对象。"
            );
        }

        string status = GetString(root, "status") ?? "";
        if (!string.Equals(status, "Ready", StringComparison.Ordinal))
        {
            return Invalid(
                "compiler.status_not_ready",
                "只有 Ready 状态的 Plan 可以编译。"
            );
        }

        WorkbookPatchCompileError? baseError = ValidateBase(
            root,
            workspace
        );
        if (baseError is not null)
        {
            return new WorkbookPatchCompileResult(
                "Invalid",
                null,
                null,
                [baseError]
            );
        }

        if (
            !root.TryGetProperty("operations", out JsonElement operations)
            || operations.ValueKind != JsonValueKind.Array
        )
        {
            return Invalid(
                "compiler.invalid_plan",
                "Plan.operations 必须是数组。"
            );
        }

        if (operations.GetArrayLength() != 1)
        {
            return Invalid(
                "compiler.multiple_operations",
                "阶段 B 只接受一个 ModifySkill operation。"
            );
        }

        JsonElement operation = operations[0];
        string operationKind = GetString(operation, "kind") ?? "";
        if (!string.Equals(operationKind, "ModifySkill", StringComparison.Ordinal))
        {
            return Invalid(
                "compiler.unsupported_operation",
                $"阶段 B 不支持 operation kind={operationKind}。"
            );
        }

        string operationId = GetString(operation, "operationId") ?? "";
        if (operationId.Length == 0)
        {
            return Invalid(
                "compiler.invalid_plan",
                "ModifySkill.operationId 不能为空。"
            );
        }

        if (
            !operation.TryGetProperty("skill", out JsonElement target)
            || target.ValueKind != JsonValueKind.Object
        )
        {
            return Invalid(
                "compiler.invalid_plan",
                "ModifySkill.skill 必须是对象。"
            );
        }

        string binding = GetString(target, "binding") ?? "";
        string targetNamespace = GetString(target, "namespace") ?? "";
        if (
            !string.Equals(binding, "Existing", StringComparison.Ordinal)
            || !target.TryGetProperty("id", out JsonElement targetIdElement)
            || targetIdElement.ValueKind != JsonValueKind.Number
            || !targetIdElement.TryGetInt32(out int targetId)
        )
        {
            return Invalid(
                "compiler.invalid_plan",
                "ModifySkill.skill 必须使用 Existing binding 和整数 id。"
            );
        }

        WorkbookPatchTable? table = workspace.Tables.FirstOrDefault(
            item =>
                string.Equals(
                    item.EntityKey,
                    "Skill",
                    StringComparison.OrdinalIgnoreCase
                )
                && NamespaceMatches(item, targetNamespace)
        );
        if (table is null)
        {
            return Invalid(
                "compiler.missing_target",
                $"当前工作区不存在目标技能类型 {targetNamespace}。",
                $"{targetNamespace}:{targetId}"
            );
        }

        WorkbookPatchRecord? record = table.Records.FirstOrDefault(
            item => item.Id == targetId
        );
        if (record is null)
        {
            return Invalid(
                "compiler.missing_target",
                $"当前工作区不存在技能 {table.Namespace}:{targetId}。",
                $"{table.Namespace}:{targetId}"
            );
        }

        if (
            !operation.TryGetProperty("fields", out JsonElement fields)
            || fields.ValueKind != JsonValueKind.Object
        )
        {
            return Invalid(
                "compiler.invalid_plan",
                "ModifySkill.fields 必须是对象。"
            );
        }

        var errors = new List<WorkbookPatchCompileError>();
        var changes = new List<WorkbookFieldChange>();
        var changedFields = new SortedSet<string>(StringComparer.Ordinal);
        foreach (
            JsonProperty planField in fields
                .EnumerateObject()
                .OrderBy(item => item.Name, StringComparer.Ordinal)
        )
        {
            WorkbookPatchField? field = table.Fields.FirstOrDefault(
                item =>
                    string.Equals(
                        item.SemanticName,
                        planField.Name,
                        StringComparison.OrdinalIgnoreCase
                    )
            );
            if (field is null)
            {
                errors.Add(
                    new WorkbookPatchCompileError(
                        "compiler.unknown_semantic_field",
                        $"能力注册表没有声明语义字段 {planField.Name}。",
                        planField.Name
                    )
                );
                continue;
            }

            if (
                field.Kind is TianshuDM.Domain.GameData.GameDataFieldKind.List
                    or TianshuDM.Domain.GameData.GameDataFieldKind.Map
                    or TianshuDM.Domain.GameData.GameDataFieldKind.DelimitedList
            )
            {
                errors.Add(
                    new WorkbookPatchCompileError(
                        "compiler.unsupported_field",
                        $"字段 {field.Path} 不是阶段 B 支持的标量字段。",
                        field.Path
                    )
                );
                continue;
            }

            if (planField.Value.ValueKind != JsonValueKind.Object)
            {
                errors.Add(
                    new WorkbookPatchCompileError(
                        "compiler.invalid_value_type",
                        $"字段 {field.Path} 的 Plan value 必须是对象。",
                        field.Path
                    )
                );
                continue;
            }

            JsonElement planValue = planField.Value;
            if (!planValue.TryGetProperty("value", out JsonElement value))
            {
                errors.Add(
                    new WorkbookPatchCompileError(
                        "compiler.invalid_value_type",
                        $"字段 {field.Path} 缺少 value。",
                        field.Path
                    )
                );
                continue;
            }

            string source = GetString(planValue, "source") ?? "";
            if (
                source is not ("ModelProposed" or "UserEdited" or "Default")
            )
            {
                errors.Add(
                    new WorkbookPatchCompileError(
                        "compiler.invalid_plan",
                        $"字段 {field.Path} 的 source={source} 不受支持。",
                        field.Path
                    )
                );
                continue;
            }

            string? unit = GetString(planValue, "unit");
            if (
                !WorkbookPatchValueConverter.TryConvert(
                    value,
                    unit,
                    field,
                    registry.ConversionRules,
                    out string after,
                    out WorkbookPatchValueFailure? failure
                )
            )
            {
                errors.Add(ToCompileError(field, failure));
                continue;
            }

            string before = RecordValue(record, field.Key);
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                continue;
            }

            changedFields.Add(field.Key);
            changes.Add(
                new WorkbookFieldChange(
                    operationId,
                    new WorkbookPatchLogicalAddress(
                        table.TableKey,
                        targetId,
                        field.Key
                    ),
                    table.Namespace,
                    targetId,
                    field.Key,
                    planField.Name,
                    value.Clone(),
                    unit,
                    JsonSerializer.SerializeToElement(before),
                    JsonSerializer.SerializeToElement(after),
                    source,
                    ReadEvidence(planValue)
                )
            );
        }

        if (errors.Count > 0)
        {
            return new WorkbookPatchCompileResult(
                "Invalid",
                null,
                null,
                errors
            );
        }

        if (changes.Count == 0)
        {
            return new WorkbookPatchCompileResult(
                "NoChange",
                null,
                null,
                [
                    new WorkbookPatchCompileError(
                        "compiler.no_change",
                        "请求没有产生任何 Excel 字段变化。"
                    )
                ]
            );
        }

        var command = new WorkbookPatchCommand(
            0,
            operationId,
            "UpdateNode",
            new WorkbookPatchAssetIdentity(table.Namespace, targetId),
            new JsonObject
            {
                ["fields"] = new JsonArray(
                    changedFields
                        .Select(
                            field => (JsonNode?)JsonValue.Create(field)
                        )
                        .ToArray()
                )
            }
        );
        var identity = new WorkbookPatchBase(
            workspace.WorkspaceId,
            workspace.Revision,
            workspace.SourceHash,
            registry.CapabilityRegistryVersion,
            registry.DefaultValueContractVersion,
            registry.DefaultMechanismContractVersion
        );
        var patch = new WorkbookPatchDocument(
            0,
            new string('0', 64),
            WorkbookPatchJsonUtilities.ComputePlanHash(planJson),
            identity,
            [],
            [command],
            changes
        );
        patch = patch with
        {
            PatchId = WorkbookPatchJsonUtilities.ComputePatchId(patch)
        };
        return new WorkbookPatchCompileResult(
            "Compiled",
            patch,
            WorkbookPatchJson.Serialize(patch),
            []
        );
    }

    private WorkbookPatchCompileError? ValidateBase(
        JsonElement root,
        WorkbookPatchWorkspace workspace
    )
    {
        if (
            !root.TryGetProperty("base", out JsonElement baseValue)
            || baseValue.ValueKind != JsonValueKind.Object
        )
        {
            return new WorkbookPatchCompileError(
                "compiler.invalid_plan",
                "Plan.base 必须是对象。"
            );
        }

        var mismatches = new List<string>();
        Compare(
            mismatches,
            "workspaceId",
            GetString(baseValue, "workspaceId"),
            workspace.WorkspaceId
        );
        Compare(
            mismatches,
            "revision",
            GetString(baseValue, "revision"),
            workspace.Revision
        );
        Compare(
            mismatches,
            "sourceHash",
            GetString(baseValue, "sourceHash"),
            workspace.SourceHash
        );
        Compare(
            mismatches,
            "capabilityRegistryVersion",
            GetString(baseValue, "capabilityRegistryVersion"),
            registry.CapabilityRegistryVersion
        );
        Compare(
            mismatches,
            "defaultValueContractVersion",
            GetString(baseValue, "defaultValueContractVersion"),
            registry.DefaultValueContractVersion
        );
        Compare(
            mismatches,
            "defaultMechanismContractVersion",
            GetString(baseValue, "defaultMechanismContractVersion"),
            registry.DefaultMechanismContractVersion
        );
        return mismatches.Count == 0
            ? null
            : new WorkbookPatchCompileError(
                "compiler.stale_base",
                "Plan base 与当前工作区不一致："
                    + string.Join(", ", mismatches),
                "base"
            );
    }

    private static void Compare(
        ICollection<string> mismatches,
        string property,
        string? actual,
        string expected
    )
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            mismatches.Add(property);
        }
    }

    private static WorkbookPatchCompileError ToCompileError(
        WorkbookPatchField field,
        WorkbookPatchValueFailure? failure
    )
    {
        string code = failure?.Kind switch
        {
            WorkbookPatchValueFailureKind.Unit => "compiler.invalid_unit",
            WorkbookPatchValueFailureKind.Scale => "compiler.missing_scale",
            _ => "compiler.invalid_value_type"
        };
        return new WorkbookPatchCompileError(
            code,
            failure?.Message ?? $"字段 {field.Path} 的值无法转换。",
            field.Path
        );
    }

    private static string RecordValue(
        WorkbookPatchRecord record,
        string field
    )
    {
        return record.Fields.TryGetValue(
            field,
            out IReadOnlyList<string>? values
        )
            ? values.FirstOrDefault() ?? ""
            : "";
    }

    private static IReadOnlyList<string> ReadEvidence(JsonElement planValue)
    {
        if (
            !planValue.TryGetProperty("evidence", out JsonElement evidence)
            || evidence.ValueKind != JsonValueKind.Array
        )
        {
            return [];
        }

        return evidence
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(
                item =>
                {
                    string kind = GetString(item, "kind") ?? "";
                    string reference = GetString(item, "ref") ?? "";
                    return kind.Length == 0
                        ? reference
                        : $"{kind}:{reference}";
                }
            )
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static bool NamespaceMatches(
        WorkbookPatchTable table,
        string targetNamespace
    )
    {
        return string.Equals(
                table.Namespace,
                targetNamespace,
                StringComparison.OrdinalIgnoreCase
            )
            || string.Equals(
                table.EntityKey,
                targetNamespace,
                StringComparison.OrdinalIgnoreCase
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

    private static WorkbookPatchCompileResult Invalid(
        string code,
        string message,
        string? subject = null
    )
    {
        return new WorkbookPatchCompileResult(
            "Invalid",
            null,
            null,
            [new WorkbookPatchCompileError(code, message, subject)]
        );
    }
}
