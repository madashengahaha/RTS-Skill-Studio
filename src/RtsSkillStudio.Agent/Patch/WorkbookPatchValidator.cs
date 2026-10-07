using System.Globalization;
using System.Text.Json;
using NJsonSchema;
using NJsonSchema.Validation;
using TianshuDM.Domain.GameData;

namespace RtsSkillStudio.Agent.Patch;

public sealed class WorkbookPatchValidator
{
    private readonly string _schemaPath;
    private readonly WorkbookPatchRegistry _registry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsonSchema? _schema;

    public WorkbookPatchValidator(
        string contractRoot,
        WorkbookPatchRegistry registry
    )
    {
        _registry = registry;
        _schemaPath = Path.Combine(
            contractRoot,
            "contracts",
            "workbook-patch.schema.json"
        );
    }

    public async Task<WorkbookPatchValidationReport> ValidateAsync(
        string patchJson,
        WorkbookPatchWorkspace workspace,
        CancellationToken cancellationToken
    )
    {
        var checks = new List<WorkbookPatchValidationCheck>();
        IReadOnlyList<string> schemaErrors = await ValidateSchemaAsync(
            patchJson,
            cancellationToken
        );
        checks.Add(
            Check(
                "validation.schema",
                schemaErrors.Count == 0 ? "Passed" : "Failed",
                schemaErrors.Count == 0
                    ? "Patch 符合 WorkbookPatch JSON Schema。"
                    : "Patch JSON Schema 校验失败。",
                "patch",
                schemaErrors
            )
        );

        WorkbookPatchDocument? patch = null;
        try
        {
            patch = WorkbookPatchJson.Deserialize(patchJson);
        }
        catch (JsonException exception)
        {
            schemaErrors = schemaErrors
                .Concat([exception.Message])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (schemaErrors.Count > 0)
            {
                checks[0] = Check(
                    "validation.schema",
                    "Failed",
                    "Patch 无法反序列化。",
                    "patch",
                    schemaErrors
                );
            }
        }

        string reportPatchId = patch?.PatchId is { Length: 64 } patchId
            ? patchId
            : new string('0', 64);
        if (patch is null)
        {
            checks.AddRange(
                RequiredNotRunChecks(
                    "Patch 无法读取，后续校验未运行。"
                )
            );
            return CreateReport(reportPatchId, checks);
        }

        checks.Add(
            Check(
                "validation.patch_id",
                string.Equals(
                    patch.PatchId,
                    WorkbookPatchJsonUtilities.ComputePatchId(patch),
                    StringComparison.Ordinal
                )
                    ? "Passed"
                    : "Failed",
                "patchId 与规范化 Patch JSON 的 SHA-256 一致。",
                "patchId",
                [
                    $"expected={WorkbookPatchJsonUtilities.ComputePatchId(patch)}",
                    $"actual={patch.PatchId}"
                ]
            )
        );
        checks.Add(
            Check(
                "validation.workspace",
                string.Equals(
                    patch.Base.WorkspaceId,
                    workspace.WorkspaceId,
                    StringComparison.Ordinal
                )
                    ? "Passed"
                    : "Failed",
                "Patch workspaceId 与当前工作区一致。",
                patch.Base.WorkspaceId,
                [workspace.WorkspaceId]
            )
        );
        checks.Add(
            Check(
                "validation.revision",
                string.Equals(
                    patch.Base.Revision,
                    workspace.Revision,
                    StringComparison.Ordinal
                )
                    ? "Passed"
                    : "Failed",
                "Patch revision 与当前工作区一致。",
                patch.Base.Revision,
                [workspace.Revision]
            )
        );
        checks.Add(
            Check(
                "validation.source_hash",
                string.Equals(
                    patch.Base.SourceHash,
                    workspace.SourceHash,
                    StringComparison.Ordinal
                )
                    ? "Passed"
                    : "Failed",
                "Patch sourceHash 与当前源数据根一致。",
                patch.Base.SourceHash,
                [workspace.SourceHash]
            )
        );
        checks.Add(
            Check(
                "validation.contract_versions",
                string.Equals(
                        patch.Base.CapabilityRegistryVersion,
                        _registry.CapabilityRegistryVersion,
                        StringComparison.Ordinal
                    )
                    && string.Equals(
                        patch.Base.DefaultValueContractVersion,
                        _registry.DefaultValueContractVersion,
                        StringComparison.Ordinal
                    )
                    && string.Equals(
                        patch.Base.DefaultMechanismContractVersion,
                        _registry.DefaultMechanismContractVersion,
                        StringComparison.Ordinal
                    )
                    ? "Passed"
                    : "Failed",
                "Patch base 的契约版本与当前 Studio 契约一致。",
                "base",
                [
                    patch.Base.CapabilityRegistryVersion,
                    patch.Base.DefaultValueContractVersion,
                    patch.Base.DefaultMechanismContractVersion
                ]
            )
        );

        IReadOnlyList<string> operationErrors = ValidateOperations(patch);
        checks.Add(
            Check(
                "validation.operation",
                operationErrors.Count == 0 ? "Passed" : "Failed",
                operationErrors.Count == 0
                    ? "Patch 只包含阶段 B 支持的 UpdateNode 命令。"
                    : "Patch 包含阶段 B 不支持的 operation。",
                "commands",
                operationErrors
            )
        );

        var assetTypeErrors = new List<string>();
        var assetNotFoundErrors = new List<string>();
        var fieldErrors = new List<string>();
        var typeErrors = new List<string>();
        var enumErrors = new List<string>();
        var rangeErrors = new List<string>();
        var referenceErrors = new List<string>();
        var scaleUnitErrors = new List<string>();
        var beforeErrors = new List<string>();
        foreach (WorkbookFieldChange change in patch.FieldChanges)
        {
            WorkbookPatchTable? table = FindTable(
                workspace,
                change.Namespace
            );
            if (
                table is null
                || !string.Equals(
                    table.TableKey,
                    change.LogicalAddress.TableKey,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                assetTypeErrors.Add(
                    $"{change.Namespace}:{change.Id} 没有匹配的 {change.LogicalAddress.TableKey} 表。"
                );
                continue;
            }

            WorkbookPatchRecord? record = table.Records.FirstOrDefault(
                item => item.Id == change.Id
            );
            if (record is null)
            {
                assetNotFoundErrors.Add(
                    $"{change.Namespace}:{change.Id} 不存在。"
                );
                continue;
            }

            WorkbookPatchField? field = FindField(table, change.Field);
            if (field is null)
            {
                fieldErrors.Add(
                    $"{table.TableKey}.{change.Field} 不存在。"
                );
                continue;
            }

            string after = WorkbookFieldChangeJson.RawText(change.After);
            if (!WorkbookPatchValueConverter.IsRawValueCompatible(after, field))
            {
                typeErrors.Add(
                    $"{field.Path}={after} 不符合 {field.Kind}/{field.RawType}。"
                );
            }

            if (
                field.Kind == GameDataFieldKind.Enum
                && !EnumMatches(field, after)
            )
            {
                enumErrors.Add($"{field.Path}={after} 不是合法枚举值。");
            }

            if (
                field.Minimum is not null
                || field.Maximum is not null
            )
            {
                if (!TryReadDecimal(after, out decimal raw))
                {
                    rangeErrors.Add($"{field.Path}={after} 不是可比较的数值。");
                }
                else if (
                    field.Minimum is { } minimum
                    && raw < minimum
                )
                {
                    rangeErrors.Add(
                        $"{field.Path}={after} 小于最小值 {minimum}。"
                    );
                }
                else if (
                    field.Maximum is { } maximum
                    && raw > maximum
                )
                {
                    rangeErrors.Add(
                        $"{field.Path}={after} 大于最大值 {maximum}。"
                    );
                }
            }

            if (
                !string.IsNullOrWhiteSpace(field.ReferenceNamespace)
                && after.Length > 0
            )
            {
                WorkbookPatchTable? referenceTable = FindTable(
                    workspace,
                    field.ReferenceNamespace
                );
                if (
                    referenceTable is null
                    || !int.TryParse(
                        after,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int referenceId
                    )
                    || referenceTable.Records.All(
                        item => item.Id != referenceId
                    )
                )
                {
                    referenceErrors.Add(
                        $"{field.Path}={after} 未解析到 {field.ReferenceNamespace}。"
                    );
                }
            }

            if (
                !TryValidateScaleAndUnit(
                    change,
                    field,
                    out string scaleUnitError
                )
            )
            {
                scaleUnitErrors.Add(scaleUnitError);
            }

            string current = record.Fields.TryGetValue(
                field.Key,
                out IReadOnlyList<string>? values
            )
                ? values.FirstOrDefault() ?? ""
                : "";
            if (
                !string.Equals(
                    current,
                    WorkbookFieldChangeJson.RawText(change.Before),
                    StringComparison.Ordinal
                )
            )
            {
                beforeErrors.Add(
                    $"{field.Path} before={WorkbookFieldChangeJson.RawText(change.Before)} current={current}"
                );
            }
        }

        checks.Add(
            Check(
                "validation.asset_type",
                assetTypeErrors.Count == 0 ? "Passed" : "Failed",
                assetTypeErrors.Count == 0
                    ? "所有变更目标均能解析到正确表。"
                    : "存在无法解析的目标资产类型。",
                "fieldChanges.namespace",
                assetTypeErrors
            )
        );
        checks.Add(
            Check(
                "validation.asset_not_found",
                assetNotFoundErrors.Count == 0 ? "Passed" : "Failed",
                assetNotFoundErrors.Count == 0
                    ? "所有目标记录均存在。"
                    : "存在不存在的目标记录。",
                "fieldChanges.id",
                assetNotFoundErrors
            )
        );
        checks.Add(
            Check(
                "validation.field_unknown",
                fieldErrors.Count == 0 ? "Passed" : "Failed",
                fieldErrors.Count == 0
                    ? "所有目标字段均存在。"
                    : "存在未知字段。",
                "fieldChanges.field",
                fieldErrors
            )
        );
        checks.Add(
            Check(
                "validation.field_type",
                typeErrors.Count == 0 ? "Passed" : "Failed",
                typeErrors.Count == 0
                    ? "所有 after 值的类型合法。"
                    : "存在类型非法的 after 值。",
                "fieldChanges.after",
                typeErrors
            )
        );
        checks.Add(
            Check(
                "validation.enum",
                enumErrors.Count == 0 ? "Passed" : "Failed",
                enumErrors.Count == 0
                    ? "枚举字段值合法。"
                    : "存在非法枚举值。",
                "fieldChanges.after",
                enumErrors
            )
        );
        checks.Add(
            Check(
                "validation.value_range",
                rangeErrors.Count == 0 ? "Passed" : "Failed",
                rangeErrors.Count == 0
                    ? "字段值均在声明范围内。"
                    : "存在超出范围的值。",
                "fieldChanges.after",
                rangeErrors
            )
        );
        checks.Add(
            Check(
                "validation.reference",
                referenceErrors.Count == 0 ? "Passed" : "Failed",
                referenceErrors.Count == 0
                    ? "引用字段均可解析。"
                    : "存在无法解析的引用。",
                "fieldChanges.after",
                referenceErrors
            )
        );
        checks.Add(
            Check(
                "validation.scale_unit",
                scaleUnitErrors.Count == 0 ? "Passed" : "Failed",
                scaleUnitErrors.Count == 0
                    ? "语义值、单位和 scale 均能产生 after。"
                    : "存在语义值、单位或 scale 不一致的变更。",
                "fieldChanges",
                scaleUnitErrors
            )
        );
        checks.Add(
            Check(
                "validation.ownership",
                !workspace.OwnershipPolicyPublished || workspace.OwnershipAllowed
                    ? "Passed"
                    : "Failed",
                !workspace.OwnershipPolicyPublished
                    ? "工作区未发布所有权约束。"
                    : "目标符合工作区所有权约束。",
                "workspace.ownership"
            )
        );
        checks.Add(
            Check(
                "validation.edit_lock",
                !workspace.EditLockPublished || !workspace.IsEditLocked
                    ? "Passed"
                    : "Failed",
                !workspace.EditLockPublished
                    ? "工作区未发布编辑锁。"
                    : "目标未被其他编辑器锁定。",
                "workspace.editLock"
            )
        );

        if (beforeErrors.Count > 0)
        {
            int revisionIndex = checks.FindIndex(
                item => item.Code == "validation.revision"
            );
            if (revisionIndex >= 0)
            {
                checks[revisionIndex] = Check(
                    "validation.revision",
                    "Failed",
                    "Patch before 值与当前工作区记录不一致。",
                    "fieldChanges.before",
                    beforeErrors
                );
            }
        }

        return CreateReport(reportPatchId, checks);
    }

    private async Task<IReadOnlyList<string>> ValidateSchemaAsync(
        string patchJson,
        CancellationToken cancellationToken
    )
    {
        try
        {
            JsonSchema schema = await GetSchemaAsync(cancellationToken);
            return schema
                .Validate(patchJson)
                .SelectMany(FlattenError)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException exception)
        {
            return [$"Patch JSON 无法解析：{exception.Message}"];
        }
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
                .Replace(
                    "\"$defs\"",
                    "\"definitions\"",
                    StringComparison.Ordinal
                )
                .Replace(
                    "#/$defs/",
                    "#/definitions/",
                    StringComparison.Ordinal
                )
                .Replace(
                    "\"semanticValue\": true",
                    "\"semanticValue\": {}",
                    StringComparison.Ordinal
                )
                .Replace(
                    "\"before\": true",
                    "\"before\": {}",
                    StringComparison.Ordinal
                )
                .Replace(
                    "\"after\": true",
                    "\"after\": {}",
                    StringComparison.Ordinal
                );
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
            foreach (
                ValidationError nested in child.Errors.Values.SelectMany(
                    value => value
                )
            )
            {
                foreach (string message in FlattenError(nested))
                {
                    yield return message;
                }
            }
        }
        else if (error is MultiTypeValidationError multi)
        {
            foreach (
                ValidationError nested in multi.Errors.Values.SelectMany(
                    value => value
                )
            )
            {
                foreach (string message in FlattenError(nested))
                {
                    yield return message;
                }
            }
        }
    }

    private static IReadOnlyList<string> ValidateOperations(
        WorkbookPatchDocument patch
    )
    {
        var errors = new List<string>();
        if (patch.Commands.Count == 0)
        {
            errors.Add("Patch.commands 不能为空。");
        }

        HashSet<string> operationIds = patch.Commands
            .Select(item => item.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (WorkbookPatchCommand command in patch.Commands)
        {
            if (!string.Equals(command.Kind, "UpdateNode", StringComparison.Ordinal))
            {
                errors.Add($"不支持 command.kind={command.Kind}。");
            }
        }

        foreach (WorkbookFieldChange change in patch.FieldChanges)
        {
            if (!operationIds.Contains(change.OperationId))
            {
                errors.Add(
                    $"fieldChange {change.LogicalAddress.TableKey}.{change.Field} 缺少对应 command。"
                );
            }
        }

        return errors;
    }

    private static WorkbookPatchValidationReport CreateReport(
        string patchId,
        IReadOnlyList<WorkbookPatchValidationCheck> checks
    )
    {
        bool invalid = checks.Any(
            item =>
                item.Required
                && item.Status is "Failed" or "NotRun"
        );
        return new WorkbookPatchValidationReport(
            0,
            patchId,
            invalid ? "Invalid" : "Valid",
            checks
        );
    }

    private static WorkbookPatchValidationCheck Check(
        string code,
        string status,
        string message,
        string? subject = null,
        IReadOnlyList<string>? evidence = null
    )
    {
        string severity = status == "Failed" ? "Error" : "Info";
        return new WorkbookPatchValidationCheck(
            code,
            status,
            severity,
            true,
            message,
            subject,
            evidence ?? []
        );
    }

    private static IReadOnlyList<WorkbookPatchValidationCheck> RequiredNotRunChecks(
        string message
    )
    {
        return
        [
            Check("validation.patch_id", "NotRun", message, "patchId"),
            Check("validation.workspace", "NotRun", message, "base.workspaceId"),
            Check("validation.revision", "NotRun", message, "base.revision"),
            Check("validation.source_hash", "NotRun", message, "base.sourceHash"),
            Check("validation.contract_versions", "NotRun", message, "base"),
            Check("validation.operation", "NotRun", message, "commands"),
            Check("validation.asset_type", "NotRun", message, "fieldChanges"),
            Check("validation.asset_not_found", "NotRun", message, "fieldChanges"),
            Check("validation.field_unknown", "NotRun", message, "fieldChanges"),
            Check("validation.field_type", "NotRun", message, "fieldChanges"),
            Check("validation.enum", "NotRun", message, "fieldChanges"),
            Check("validation.value_range", "NotRun", message, "fieldChanges"),
            Check("validation.reference", "NotRun", message, "fieldChanges"),
            Check("validation.scale_unit", "NotRun", message, "fieldChanges"),
            Check("validation.ownership", "NotRun", message, "workspace"),
            Check("validation.edit_lock", "NotRun", message, "workspace")
        ];
    }

    private static WorkbookPatchTable? FindTable(
        WorkbookPatchWorkspace workspace,
        string namespaceOrEntity
    )
    {
        return workspace.Tables.FirstOrDefault(
            item =>
                string.Equals(
                    item.Namespace,
                    namespaceOrEntity,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(
                    item.EntityKey,
                    namespaceOrEntity,
                    StringComparison.OrdinalIgnoreCase
                )
        );
    }

    private static WorkbookPatchField? FindField(
        WorkbookPatchTable table,
        string field
    )
    {
        return table.Fields.FirstOrDefault(
            item =>
                string.Equals(
                    item.Key,
                    field,
                    StringComparison.OrdinalIgnoreCase
                )
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
                string.Equals(
                    option.Value,
                    rawValue,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(
                    option.Label,
                    rawValue,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(
                    option.Code,
                    rawValue,
                    StringComparison.OrdinalIgnoreCase
                )
                || (
                    option.LegacyValue is { } legacy
                    && string.Equals(
                        legacy.ToString(CultureInfo.InvariantCulture),
                        rawValue,
                        StringComparison.Ordinal
                    )
                )
        );
    }

    private static bool TryReadDecimal(
        string value,
        out decimal result
    )
    {
        return decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out result
        );
    }

    private bool TryValidateScaleAndUnit(
        WorkbookFieldChange change,
        WorkbookPatchField field,
        out string error
    )
    {
        error = "";
        if (change.SemanticValue is null)
        {
            error = $"{field.Path} 缺少 semanticValue。";
            return false;
        }

        if (
            !string.IsNullOrWhiteSpace(field.Unit)
            && string.IsNullOrWhiteSpace(change.SemanticUnit)
        )
        {
            error = $"{field.Path} 缺少 semanticUnit。";
            return false;
        }

        if (
            !WorkbookPatchValueConverter.TryConvert(
                change.SemanticValue.Value,
                change.SemanticUnit,
                field,
                _registry.ConversionRules,
                out string expected,
                out WorkbookPatchValueFailure? failure
            )
        )
        {
            error = failure?.Message
                ?? $"{field.Path} 的 semanticValue 无法转换。";
            return false;
        }

        string actual = WorkbookFieldChangeJson.RawText(change.After);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            error =
                $"{field.Path} 语义值应写为 {expected}，Patch after={actual}。";
            return false;
        }

        return true;
    }
}
