using System.Text.Json;
using System.Text.Json.Nodes;

namespace RtsSkillStudio.Agent.Patch;

public sealed partial class WorkbookPatchCompiler(WorkbookPatchRegistry registry)
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

        if (operations.GetArrayLength() == 0)
        {
            return Invalid(
                "compiler.invalid_plan",
                "Ready Plan 至少需要一个 operation。"
            );
        }

        if (operations.GetArrayLength() > 1)
        {
            return CompileTransaction(planJson, operations, workspace);
        }

        JsonElement operation = operations[0];
        string operationKind = GetString(operation, "kind") ?? "";
        WorkbookPatchCompileError? ownershipError = ValidateOwnership(root, operation, workspace);
        if (ownershipError is not null) return new("Invalid", null, null, [ownershipError]);
        if (operationKind is "DeleteAsset" or "ReorderMembers")
            return CompileStructure(planJson, workspace);
        if (operationKind is "CreateSkillChain" or "ExtendAssetChain")
            return new WorkbookSkillChainCompiler(registry).Compile(planJson, workspace);
        if (RequiresCollectionCompiler(operation, workspace))
            return CompileCollections(planJson, workspace);
        bool modifySkill = string.Equals(
            operationKind,
            "ModifySkill",
            StringComparison.Ordinal
        );
        bool modifyAsset = string.Equals(
            operationKind,
            "ModifyAsset",
            StringComparison.Ordinal
        );
        bool removeLink = string.Equals(
            operationKind,
            "RemoveLink",
            StringComparison.Ordinal
        );
        if (!modifySkill && !modifyAsset && !removeLink)
        {
            return Invalid(
                "compiler.unsupported_operation",
                $"当前阶段不支持 operation kind={operationKind}。"
            );
        }

        string operationId = GetString(operation, "operationId") ?? "";
        if (operationId.Length == 0)
        {
            return Invalid(
                "compiler.invalid_plan",
                $"{operationKind}.operationId 不能为空。"
            );
        }

        string targetProperty = modifySkill
            ? "skill"
            : modifyAsset
                ? "asset"
                : "parent";
        if (
            !operation.TryGetProperty(targetProperty, out JsonElement target)
            || target.ValueKind != JsonValueKind.Object
        )
        {
            return Invalid(
                "compiler.invalid_plan",
                $"{operationKind}.{targetProperty} 必须是对象。"
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
                $"{operationKind}.{targetProperty} 必须使用 Existing binding 和整数 id。"
            );
        }

        WorkbookPatchTable? table = workspace.Tables.FirstOrDefault(
            item =>
                (
                    !modifySkill
                    || string.Equals(
                        item.EntityKey,
                        "Skill",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                && NamespaceMatches(item, targetNamespace)
        );
        if (table is null)
        {
            return Invalid(
                "compiler.missing_target",
                $"当前工作区不存在目标类型 {targetNamespace}。",
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
                $"当前工作区不存在目标资产 {table.Namespace}:{targetId}。",
                $"{table.Namespace}:{targetId}"
            );
        }

        var errors = new List<WorkbookPatchCompileError>();
        var changes = new List<WorkbookFieldChange>();
        var changedFields = new SortedSet<string>(StringComparer.Ordinal);

        if (removeLink)
        {
            WorkbookPatchCompileError? removalError = CompileRemoveLink(
                operation,
                operationId,
                workspace,
                table,
                record,
                changedFields,
                changes
            );
            if (removalError is not null)
            {
                errors.Add(removalError);
            }

            return CompleteCompilation(
                planJson,
                workspace,
                operationId,
                table,
                targetId,
                changedFields,
                changes,
                errors
            );
        }

        if (
            !operation.TryGetProperty("fields", out JsonElement fields)
            || fields.ValueKind != JsonValueKind.Object
        )
        {
            return Invalid(
                "compiler.invalid_plan",
                $"{operationKind}.fields 必须是对象。"
            );
        }

        foreach (
            JsonProperty planField in fields
                .EnumerateObject()
                .OrderBy(item => item.Name, StringComparer.Ordinal)
        )
        {
            WorkbookPatchField? field = table.Fields
                .Where(
                    item =>
                        string.Equals(
                            item.SemanticName,
                            planField.Name,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && (
                            item.RecordId is null
                            || item.RecordId == targetId
                        )
                )
                .OrderByDescending(item => item.RecordId == targetId)
                .FirstOrDefault();
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

            if (field.Repeating)
            {
                errors.Add(
                    new WorkbookPatchCompileError(
                        "compiler.unsupported_repeating_field",
                        $"字段 {field.Path} 是重复参数，当前阶段暂不支持部分修改。",
                        field.Path
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
            string after;
            string? referenceTarget = null;
            if (
                field.Kind
                == TianshuDM.Domain.GameData.GameDataFieldKind.Reference
            )
            {
                if (
                    !TryResolveReferenceValue(
                        value,
                        field,
                        workspace,
                        out after,
                        out referenceTarget,
                        out WorkbookPatchCompileError? referenceError
                    )
                )
                {
                    errors.Add(referenceError!);
                    continue;
                }
            }
            else if (
                !WorkbookPatchValueConverter.TryConvert(
                    value,
                    unit,
                    field,
                    registry.ConversionRules,
                    out after,
                    out WorkbookPatchValueFailure? failure
                )
            )
            {
                errors.Add(ToCompileError(field, failure));
                continue;
            }

            string before = WorkbookPatchRecordValue.Read(record, field);
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                continue;
            }

            // A reference change is a link, so its semantic value is the target
            // key rather than the raw cell text.
            JsonElement semanticValue =
                referenceTarget is null
                    ? value.Clone()
                    : JsonSerializer.SerializeToElement(
                        long.Parse(
                            after,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture
                        )
                    );
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
                    semanticValue,
                    unit,
                    JsonSerializer.SerializeToElement(before),
                    JsonSerializer.SerializeToElement(after),
                    source,
                    referenceTarget is null
                        ? ReadEvidence(planValue)
                        : ReadEvidence(planValue)
                            .Concat(
                                [
                                    $"Capability:{field.Path}",
                                    $"ExistingConfig:{referenceTarget}"
                                ]
                            )
                            .ToArray()
                )
            );
        }

        return CompleteCompilation(
            planJson,
            workspace,
            operationId,
            table,
            targetId,
            changedFields,
            changes,
            errors
        );
    }

    private WorkbookPatchCompileResult CompleteCompilation(
        string planJson,
        WorkbookPatchWorkspace workspace,
        string operationId,
        WorkbookPatchTable table,
        int targetId,
        SortedSet<string> changedFields,
        List<WorkbookFieldChange> changes,
        List<WorkbookPatchCompileError> errors
    )
    {
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
                ["sourcePlan"] = JsonNode.Parse(planJson),
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

    /// <summary>
    /// Compiles a schema-defined RemoveLink for one existing scalar reference
    /// field. The plan names the link it expects to remove; the compiler only
    /// unlinks when the field's current value is exactly that target. The
    /// workbook encoding of "no link" comes from the registry
    /// <c>referenceRemoval</c> metadata, never from a guess.
    /// </summary>
    private WorkbookPatchCompileError? CompileRemoveLink(
        JsonElement operation,
        string operationId,
        WorkbookPatchWorkspace workspace,
        WorkbookPatchTable table,
        WorkbookPatchRecord record,
        SortedSet<string> changedFields,
        List<WorkbookFieldChange> changes
    )
    {
        if (operation.TryGetProperty("parameterIndex", out _))
        {
            return new WorkbookPatchCompileError(
                "compiler.unsupported_action_parameter_link",
                "RemoveLink 暂不支持 action 参数引用链接。",
                "parameterIndex"
            );
        }

        string fieldName = GetString(operation, "field") ?? "";
        if (fieldName.Length == 0)
        {
            return new WorkbookPatchCompileError(
                "compiler.invalid_plan",
                "RemoveLink.field 不能为空。",
                "field"
            );
        }

        WorkbookPatchField? field = table.Fields
            .Where(
                item =>
                    string.Equals(
                        item.SemanticName,
                        fieldName,
                        StringComparison.OrdinalIgnoreCase
                    )
                    && (item.RecordId is null || item.RecordId == record.Id)
            )
            .OrderByDescending(item => item.RecordId == record.Id)
            .FirstOrDefault();
        if (field is null)
        {
            return new WorkbookPatchCompileError(
                "compiler.unknown_semantic_field",
                $"能力注册表没有声明语义字段 {fieldName}。",
                fieldName
            );
        }

        if (field.BindingKind == WorkbookPatchFieldBindingKind.ActionParameter)
        {
            return new WorkbookPatchCompileError(
                "compiler.unsupported_action_parameter_link",
                $"字段 {field.Path} 是 action 参数，RemoveLink 暂不支持。",
                field.Path
            );
        }

        if (
            field.Kind
            is TianshuDM.Domain.GameData.GameDataFieldKind.List
                or TianshuDM.Domain.GameData.GameDataFieldKind.Map
                or TianshuDM.Domain.GameData.GameDataFieldKind.DelimitedList
        )
        {
            return new WorkbookPatchCompileError(
                "compiler.unsupported_list_reference",
                $"字段 {field.Path} 是列表值，RemoveLink 暂不支持。",
                field.Path
            );
        }

        if (
            field.Kind
            != TianshuDM.Domain.GameData.GameDataFieldKind.Reference
        )
        {
            return new WorkbookPatchCompileError(
                "compiler.remove_link_not_reference",
                $"字段 {field.Path} 不是标量引用字段，RemoveLink 只接受引用字段。",
                field.Path
            );
        }

        if (
            !WorkbookPatchReference.TryResolveTarget(
                field,
                out string declaredTarget,
                out bool contractMismatch
            )
        )
        {
            return new WorkbookPatchCompileError(
                contractMismatch
                    ? "compiler.reference_contract_mismatch"
                    : "compiler.reference_target_undeclared",
                contractMismatch
                    ? $"字段 {field.Path} 的注册表引用目标 {field.ReferenceTarget} 与工作区类型标注 {field.ReferenceNamespace} 不一致。"
                    : $"字段 {field.Path} 未声明引用目标，不能解除链接。",
                field.Path
            );
        }

        if (
            !WorkbookPatchReference.TryResolveRemoval(
                field,
                out WorkbookPatchReferenceRemoval? removal
            )
        )
        {
            return new WorkbookPatchCompileError(
                "compiler.reference_removal_undeclared",
                $"字段 {field.Path} 未声明引用移除编码 referenceRemoval。",
                field.Path
            );
        }

        WorkbookPatchReferenceRemoval resolvedRemoval = removal!;

        if (
            !operation.TryGetProperty("target", out JsonElement target)
            || target.ValueKind != JsonValueKind.Object
        )
        {
            return new WorkbookPatchCompileError(
                "compiler.invalid_reference_value",
                "RemoveLink.target 必须是 Existing 引用对象。",
                "target"
            );
        }

        string binding = GetString(target, "binding") ?? "";
        string targetNamespace = GetString(target, "namespace") ?? "";
        if (
            !string.Equals(binding, "Existing", StringComparison.Ordinal)
            || targetNamespace.Length == 0
            || !target.TryGetProperty("id", out JsonElement idElement)
            || idElement.ValueKind != JsonValueKind.Number
            || !idElement.TryGetInt32(out int targetId)
        )
        {
            return new WorkbookPatchCompileError(
                "compiler.invalid_reference_value",
                $"RemoveLink.target 需要 Existing binding 的 namespace + id。",
                field.Path
            );
        }

        if (
            !WorkbookPatchReference.NamespaceMatches(
                targetNamespace,
                declaredTarget
            )
        )
        {
            return new WorkbookPatchCompileError(
                "compiler.reference_namespace_mismatch",
                $"引用字段 {field.Path} 只接受 {declaredTarget}，收到 {targetNamespace}。",
                targetNamespace
            );
        }

        WorkbookPatchTable? targetTable = workspace.Tables.FirstOrDefault(
            item =>
                WorkbookPatchReference.NamespaceMatches(
                    item.Namespace,
                    targetNamespace
                )
                || WorkbookPatchReference.NamespaceMatches(
                    item.EntityKey,
                    targetNamespace
                )
        );
        if (
            targetTable is null
            || targetTable.Records.All(item => item.Id != targetId)
        )
        {
            return new WorkbookPatchCompileError(
                "compiler.reference_target_missing",
                $"引用目标 {targetNamespace}:{targetId} 在当前工作区不存在。",
                $"{targetNamespace}:{targetId}"
            );
        }

        string current = WorkbookPatchRecordValue.Read(record, field);
        if (string.Equals(current, resolvedRemoval.RawValue, StringComparison.Ordinal))
        {
            // The field already holds the declared "no link" encoding, so the
            // requested removal is a no-op.
            return null;
        }

        string expectedTarget = targetId.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );
        if (!string.Equals(current, expectedTarget, StringComparison.Ordinal))
        {
            return new WorkbookPatchCompileError(
                "compiler.remove_link_target_mismatch",
                $"字段 {field.Path} 当前指向 {current}，与要移除的目标 {targetNamespace}:{targetId} 不一致。",
                field.Path
            );
        }

        changedFields.Add(field.Key);
        changes.Add(
            new WorkbookFieldChange(
                operationId,
                new WorkbookPatchLogicalAddress(
                    table.TableKey,
                    record.Id,
                    field.Key
                ),
                table.Namespace,
                record.Id,
                field.Key,
                field.SemanticName ?? field.Key,
                JsonSerializer.SerializeToElement(resolvedRemoval.RawValue),
                null,
                JsonSerializer.SerializeToElement(current),
                JsonSerializer.SerializeToElement(resolvedRemoval.RawValue),
                "Compiler",
                [
                    $"Capability:{field.Path}",
                    $"ReferenceRemoval:{resolvedRemoval.Kind}",
                    $"ExistingConfig:{targetNamespace}:{targetId}"
                ]
            )
        );
        return null;
    }

    private WorkbookPatchCompileResult CompileTransaction(
        string planJson,
        JsonElement operations,
        WorkbookPatchWorkspace workspace
    )
    {
        var commands = new List<WorkbookPatchCommand>();
        var changes = new List<WorkbookFieldChange>();
        var errors = new List<WorkbookPatchCompileError>();
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var addresses = new HashSet<WorkbookPatchLogicalAddress>();
        var allocations = new List<WorkbookPatchAllocation>();
        WorkbookPatchWorkspace allocationWorkspace = workspace;
        WorkbookPatchBase? identity = null;
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            if (operation.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new("compiler.invalid_plan", "operation 必须是对象。"));
                continue;
            }
            string operationId = GetString(operation, "operationId") ?? "";
            if (!operationIds.Add(operationId))
            {
                errors.Add(new("compiler.duplicate_operation_id", "operationId 必须唯一。", operationId));
                continue;
            }
            // Every operation resolves against the same immutable base revision.
            // Overlapping writes are rejected instead of depending on operation order.
            JsonObject singlePlan = JsonNode.Parse(planJson)!.AsObject();
            singlePlan["operations"] = new JsonArray(JsonNode.Parse(operation.GetRawText()));
            bool creating = GetString(operation, "kind") is "CreateSkillChain" or "ExtendAssetChain";
            WorkbookPatchCompileResult result = CompileCore(singlePlan.ToJsonString(), creating ? allocationWorkspace : workspace);
            if (result.Status == "NoChange")
                continue;
            if (result.Patch is null)
            {
                errors.AddRange(result.Errors);
                continue;
            }
            identity ??= result.Patch.Base;
            allocations.AddRange(result.Patch.Allocations.Select(allocation => allocation with { LocalKey = operationId + "/" + allocation.LocalKey }));
            if (creating) allocationWorkspace = ReserveCreatedRecords(allocationWorkspace, result.Patch);
            foreach (WorkbookFieldChange change in result.Patch.FieldChanges)
            {
                if (!addresses.Add(change.LogicalAddress))
                    errors.Add(new("compiler.conflicting_field_write", "同一事务不能重复写入同一字段。", change.LogicalAddress.ToString()));
                changes.Add(change);
            }
            foreach (WorkbookPatchCommand command in result.Patch.Commands)
                commands.Add(command with { Sequence = commands.Count });
        }
        if (errors.Count > 0)
            return new("Invalid", null, null, errors);
        if (changes.Count == 0 && commands.Count == 0)
            return Invalid("compiler.no_change", "请求没有产生任何 Excel 字段变化。") with { Status = "NoChange" };
        if (commands.Any(command => command.Arguments.ContainsKey("sourcePlan")))
        {
            foreach (WorkbookPatchCommand command in commands) command.Arguments.Remove("sourcePlan");
            commands[0].Arguments["sourcePlan"] = JsonNode.Parse(planJson);
        }
        var patch = new WorkbookPatchDocument(
            0, new string('0', 64), WorkbookPatchJsonUtilities.ComputePlanHash(planJson),
            identity!, allocations, commands, changes
        );
        patch = patch with { PatchId = WorkbookPatchJsonUtilities.ComputePatchId(patch) };
        return new("Compiled", patch, WorkbookPatchJson.Serialize(patch), []);
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

    private static bool TryResolveReferenceValue(
        JsonElement value,
        WorkbookPatchField field,
        WorkbookPatchWorkspace workspace,
        out string rawValue,
        out string? targetKey,
        out WorkbookPatchCompileError? error
    )
    {
        rawValue = "";
        targetKey = null;
        error = null;

        if (!WorkbookPatchReference.TryResolveTarget(
            field,
            out string expectedNamespace,
            out bool contractMismatch
        ))
        {
            error = new WorkbookPatchCompileError(
                contractMismatch
                    ? "compiler.reference_contract_mismatch"
                    : "compiler.reference_target_undeclared",
                contractMismatch
                    ? $"字段 {field.Path} 的注册表引用目标 {field.ReferenceTarget} 与工作区类型标注 {field.ReferenceNamespace} 不一致。"
                    : $"字段 {field.Path} 未声明引用目标，不能写入链接。",
                field.Path
            );
            return false;
        }

        string namespaceValue;
        int targetId;
        if (value.ValueKind == JsonValueKind.Object)
        {
            string binding = GetString(value, "binding") ?? "";
            namespaceValue = GetString(value, "namespace") ?? "";
            if (
                !string.Equals(binding, "Existing", StringComparison.Ordinal)
                || namespaceValue.Length == 0
                || !value.TryGetProperty("id", out JsonElement idElement)
                || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt32(out targetId)
            )
            {
                error = new WorkbookPatchCompileError(
                    "compiler.invalid_reference_value",
                    $"引用字段 {field.Path} 需要 Existing binding 的 namespace + id 目标。",
                    field.Path
                );
                return false;
            }
        }
        else if (
            value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out targetId)
        )
        {
            namespaceValue = expectedNamespace;
        }
        else
        {
            error = new WorkbookPatchCompileError(
                "compiler.invalid_reference_value",
                $"引用字段 {field.Path} 需要引用目标或整数 ID。",
                field.Path
            );
            return false;
        }

        if (
            namespaceValue.Length > 0
            && !WorkbookPatchReference.NamespaceMatches(
                namespaceValue,
                expectedNamespace
            )
        )
        {
            error = new WorkbookPatchCompileError(
                "compiler.reference_namespace_mismatch",
                $"引用字段 {field.Path} 只接受 {expectedNamespace}，收到 {namespaceValue}。",
                namespaceValue
            );
            return false;
        }

        WorkbookPatchTable? targetTable = workspace.Tables.FirstOrDefault(
            item =>
                WorkbookPatchReference.NamespaceMatches(
                    item.Namespace,
                    namespaceValue
                )
                || WorkbookPatchReference.NamespaceMatches(
                    item.EntityKey,
                    namespaceValue
                )
        );
        if (
            targetTable is null
            || targetTable.Records.All(item => item.Id != targetId)
        )
        {
            error = new WorkbookPatchCompileError(
                "compiler.reference_target_missing",
                $"引用目标 {namespaceValue}:{targetId} 在当前工作区不存在。",
                $"{namespaceValue}:{targetId}"
            );
            return false;
        }

        rawValue = targetId.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );
        targetKey = $"{namespaceValue}:{targetId}";
        return true;
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
