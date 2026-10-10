using RtsSkillStudio.Agent.Patch;
using RtsSkillStudio.Agent.Llm;
using System.Text.Json.Nodes;
using TianshuDM.Application.HeroAuthoring;
using TianshuDM.Domain.GameData;

namespace RtsSkillStudio.Api.Workspaces;

public sealed record WorkbookPatchCompileResponse(
    string Status,
    string? PatchJson,
    IReadOnlyList<WorkbookPatchCompileError> Errors,
    WorkbookPatchValidationReport? Validation,
    IReadOnlyList<WorkbookPatchDiffRow> Diff,
    string? PlanJson = null
);

public sealed record WorkbookPatchApplyResponse(
    string Status,
    WorkbookPatchValidationReport Validation,
    TemporaryWorkbookPatchApplyResult? ApplyResult
);

public sealed record WorkbookPatchFinalApplyResponse(
    string Status,
    WorkbookPatchValidationReport? Validation,
    FinalWorkbookPatchApplyResult? ApplyResult
);

public sealed class WorkbookPatchWorkspaceService(
    SkillWorkspaceService workspace,
    SkillConfigPlanValidator planValidator,
    WorkbookPatchRegistry registry,
    WorkbookPatchCompiler compiler,
    WorkbookPatchValidator validator,
    TemporaryWorkbookPatchApplyService applyService,
    FinalWorkbookPatchApplyService finalApplyService,
    ILogger<WorkbookPatchWorkspaceService> logger
)
{
    public async Task<WorkbookPatchCompileResponse> CompileAsync(
        string planJson,
        CancellationToken cancellationToken,
        string? reviewedPatchJson = null,
        bool rebase = false
    )
    {
        SkillConfigPlanValidationResult planValidation =
            await planValidator.ValidateAsync(
                planJson,
                cancellationToken
            );
        if (!planValidation.IsValid)
        {
            return new WorkbookPatchCompileResponse(
                "Invalid",
                null,
                planValidation.Errors
                    .Select(
                        error => new WorkbookPatchCompileError(
                            "compiler.invalid_plan",
                            error
                        )
                    )
                    .ToArray(),
                null,
                []
            );
        }

        WorkbookPatchWorkspace context = await BuildWorkspaceAsync(
            cancellationToken
        );
        WorkbookPatchCompileResult compile = compiler.Compile(
            planJson,
            context
        );
        if (rebase)
        {
            if (string.IsNullOrWhiteSpace(reviewedPatchJson))
                return new("Invalid", null, [new("compiler.rebase_conflict", "重基需要上次已编译的 Patch 作为原值依据。")], null, []);
            WorkbookPlanRebaseResult rebased = WorkbookPlanRebaser.Rebase(planJson, reviewedPatchJson, context, registry);
            if (rebased.PlanJson is null) return new("Invalid", null, rebased.Errors, null, []);
            planJson = rebased.PlanJson;
            compile = compiler.Compile(planJson, context);
        }
        if (compile.Patch is null || string.IsNullOrWhiteSpace(compile.PatchJson))
        {
            logger.LogInformation(
                "WorkbookPatch compile finished with {Status}: {Errors}.",
                compile.Status,
                string.Join("; ", compile.Errors.Select(item => item.Code))
            );
            return new WorkbookPatchCompileResponse(
                compile.Status,
                null,
                compile.Errors,
                null,
                []
            );
        }

        WorkbookPatchValidationReport validation =
            await validator.ValidateAsync(
                compile.PatchJson,
                context,
                cancellationToken
            );
        IReadOnlyList<WorkbookPatchDiffRow> diff =
            WorkbookPatchDiffProjector.Project(compile.Patch);
        return new WorkbookPatchCompileResponse(
            compile.Status,
            compile.PatchJson,
            compile.Errors,
            validation,
            diff,
            planJson
        );
    }

    public async Task<WorkbookPatchApplyResponse> ApplyTemporaryAsync(
        string patchJson,
        CancellationToken cancellationToken
    )
    {
        WorkbookPatchWorkspace context = await BuildWorkspaceAsync(
            cancellationToken
        );
        WorkbookPatchValidationReport validation =
            await validator.ValidateAsync(
                patchJson,
                context,
                cancellationToken
            );
        if (validation.Status != "Valid")
        {
            return new WorkbookPatchApplyResponse(
                "Blocked",
                validation,
                null
            );
        }

        TemporaryWorkbookPatchApplyResult applyResult =
            await applyService.ApplyAsync(
                patchJson,
                validation,
                cancellationToken
            );
        return new WorkbookPatchApplyResponse(
            applyResult.Status,
            validation,
            applyResult
        );
    }

    public async Task<WorkbookPatchFinalApplyResponse> ApplyFinalAsync(
        string patchJson,
        CancellationToken cancellationToken
    )
    {
        WorkbookPatchWorkspace context = await BuildWorkspaceAsync(
            cancellationToken
        );
        WorkbookPatchValidationReport validation =
            await validator.ValidateAsync(
                patchJson,
                context,
                cancellationToken
            );
        if (validation.Status != "Valid")
        {
            IReadOnlyList<string> blockingReasons = validation.Checks
                .Where(
                    check =>
                        check.Required
                        && check.Status is "Failed" or "NotRun"
                )
                .Select(check => check.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            string message =
                blockingReasons.FirstOrDefault()
                ?? "WorkbookPatch 校验未通过，正式写入已阻止。";
            return new WorkbookPatchFinalApplyResponse(
                "Blocked",
                validation,
                new FinalWorkbookPatchApplyResult(
                    "Blocked",
                    validation.PatchId,
                    "",
                    "",
                    "",
                    0,
                    blockingReasons,
                    message
                )
            );
        }

        FinalWorkbookPatchApplyResult applyResult =
            await finalApplyService.ApplyAsync(
                patchJson,
                validation,
                cancellationToken
            );
        if (applyResult.Status == "Applied")
        {
            workspace.InvalidateSnapshot();
        }

        return new WorkbookPatchFinalApplyResponse(
            applyResult.Status,
            validation,
            applyResult
        );
    }

    public async Task<WorkbookPatchFinalApplyResponse> UndoAsync(
        string transactionId,
        CancellationToken cancellationToken
    )
    {
        FinalWorkbookPatchApplyResult applyResult =
            await finalApplyService.UndoAsync(
                transactionId,
                cancellationToken
            );
        if (applyResult.Status == "Undone")
        {
            workspace.InvalidateSnapshot();
        }

        return new WorkbookPatchFinalApplyResponse(
            applyResult.Status,
            null,
            applyResult
        );
    }

    private async Task<WorkbookPatchWorkspace> BuildWorkspaceAsync(
        CancellationToken cancellationToken
    )
    {
        WorkbookPatchWorkspaceSnapshot snapshot =
            await workspace.GetPatchWorkspaceSnapshotAsync(cancellationToken);
        var entityByNamespace = registry.Entities.ToDictionary(
            item => item.Namespace,
            StringComparer.OrdinalIgnoreCase
        );
        var tables = new List<WorkbookPatchTable>();
        foreach (GameDataTable table in snapshot.Catalog.Tables)
        {
            if (
                !HeroAuthoringGraphProjector.TryGetNamespace(
                    table.Key,
                    out string nodeNamespace
                )
                || !entityByNamespace.TryGetValue(
                    nodeNamespace,
                    out WorkbookPatchRegistryEntity? entity
                )
            )
            {
                continue;
            }

            WorkbookPatchRecord[] records = table.Records
                .Select(
                    record => new WorkbookPatchRecord(
                        record.Id,
                        record.Fields,
                        record.SourceOrder
                    )
                )
                .ToArray();
            var fields = table.Fields
                .Select(field => BuildField(entity, field))
                .ToList();
            foreach (GameDataRecord record in table.Records)
            {
                fields.AddRange(
                    BuildActionParameterFields(entity, table, record)
                );
            }
            tables.Add(
                new WorkbookPatchTable(
                    entity.Key,
                    nodeNamespace,
                    table.Key,
                    fields.ToArray(),
                    records
                )
            );
        }

        return new WorkbookPatchWorkspace(
            snapshot.WorkspaceId,
            snapshot.Revision,
            snapshot.SourceHash,
            tables
        );
    }

    private WorkbookPatchField BuildField(
        WorkbookPatchRegistryEntity entity,
        GameDataFieldDefinition field
    )
    {
        string path = $"{entity.Key}.{field.Key}";
        WorkbookPatchRegistryField? registryField = registry.EntityFields
            .FirstOrDefault(
                item => string.Equals(
                    item.Path,
                    path,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        JsonObject? creationField = (registry.Creation?["entities"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => item["namespace"]?.GetValue<string>() == entity.Namespace)
            .SelectMany(item => item["fields"]!.AsArray().OfType<JsonObject>())
            .FirstOrDefault(item => item["key"]?.GetValue<string>() == field.Key);
        string? referenceNamespace = null;
        if (!string.IsNullOrWhiteSpace(field.ReferenceTable))
        {
            referenceNamespace =
                HeroAuthoringGraphProjector.TryGetNamespace(
                    field.ReferenceTable,
                    out string nodeNamespace
                )
                    ? nodeNamespace
                    : field.ReferenceTable;
        }

        // The workbook type annotation is the executable binding; the registry
        // contract declares the expected reference kind and target. Both are
        // kept so the compiler can reject a contract that drifted from the data.
        string? referenceTarget = ResolveReferenceNamespace(
            registryField?.ReferenceTarget ?? creationField?["referenceTarget"]?.GetValue<string>()
        );
        return new WorkbookPatchField(
            field.Key,
            path,
            registryField?.SemanticName ?? creationField?["semanticName"]?.GetValue<string>(),
            field.Kind,
            field.RawType,
            field.Required,
            registryField?.Scale ?? 1,
            registryField?.Unit,
            registryField?.Minimum,
            registryField?.Maximum,
            field.Options,
            referenceNamespace ?? referenceTarget,
            ReferenceTarget: referenceTarget,
            ReferenceRemoval: registryField?.ReferenceRemoval,
            ColumnCount: field.ColumnCount,
            PackingSeparator: field.PackingSeparator
        );
    }

    private IReadOnlyList<WorkbookPatchField> BuildActionParameterFields(
        WorkbookPatchRegistryEntity entity,
        GameDataTable table,
        GameDataRecord record
    )
    {
        string? category = (registry.Creation?["entities"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(item => item["namespace"]?.GetValue<string>() == entity.Namespace)?["actionCategory"]?.GetValue<string>();
        if (category is null) return [];
        string? actionKey = ResolveActionKey(table, record, category);
        WorkbookPatchAction? action = registry.Actions.FirstOrDefault(
            candidate =>
                string.Equals(
                    candidate.Category,
                    category,
                    StringComparison.OrdinalIgnoreCase
                )
                && string.Equals(
                    candidate.Key,
                    actionKey,
                    StringComparison.OrdinalIgnoreCase
                )
        );
        if (action is null)
        {
            return [];
        }

        return action.Parameters
            .Select(
                parameter => new WorkbookPatchField(
                    $"action_param[{parameter.Index}]",
                    $"{entity.Key}.action_param[{parameter.Index}]",
                    parameter.Key,
                    parameter.FieldKind,
                    parameter.RawType,
                    parameter.Required,
                    parameter.Scale,
                    parameter.Unit,
                    parameter.Minimum,
                    parameter.Maximum,
                    parameter.Options,
                    ResolveReferenceNamespace(parameter.ReferenceTarget),
                    WorkbookPatchFieldBindingKind.ActionParameter,
                    record.Id,
                    action.Key,
                    parameter.Index,
                    parameter.Repeating,
                    ColumnCount: table.Fields.First(item => item.Key == "action_param").ColumnCount,
                    RepeatStep: parameter.RepeatStep,
                    ReferenceTarget: ResolveReferenceNamespace(parameter.ReferenceTarget),
                    AllowsMultipleEnumValues: parameter.AllowsMultipleEnumValues
                )
            )
            .ToArray();
    }

    private string? ResolveActionKey(
        GameDataTable table,
        GameDataRecord record,
        string category
    )
    {
        GameDataFieldDefinition? actionTypeField = table.Fields.FirstOrDefault(
            field => string.Equals(
                field.Key,
                "action_type",
                StringComparison.OrdinalIgnoreCase
            )
        );
        string raw = record.Fields.TryGetValue(
                "action_type",
                out IReadOnlyList<string>? values
            )
            ? values.FirstOrDefault(
                value => !string.IsNullOrWhiteSpace(value)
            ) ?? ""
            : "";
        if (actionTypeField is null || raw.Length == 0)
        {
            return null;
        }

        GameDataOption? option = actionTypeField.Options.FirstOrDefault(
            candidate =>
                string.Equals(
                    candidate.Value,
                    raw,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(
                    candidate.Label,
                    raw,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(
                    candidate.Code,
                    raw,
                    StringComparison.OrdinalIgnoreCase
                )
                || (
                    candidate.LegacyValue is { } optionLegacy
                    && string.Equals(
                        optionLegacy.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                        raw,
                        StringComparison.Ordinal
                    )
                )
        );
        int? optionValue =
            option is not null
            && int.TryParse(
                option.Value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int parsedOption
            )
                ? parsedOption
                : null;
        int? legacyValue =
            option?.LegacyValue
            ?? optionValue
            ?? (
                int.TryParse(
                    raw,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int rawValue
                )
                    ? rawValue
                    : null
            );
        return registry.Actions
            .Where(
                candidate =>
                    candidate.Category
                    == category
            )
            .FirstOrDefault(
                candidate => candidate.LegacyValue == legacyValue
            )
            ?.Key;
    }

    private static string? ResolveReferenceNamespace(string? referenceTarget)
    {
        if (string.IsNullOrWhiteSpace(referenceTarget))
        {
            return null;
        }

        return HeroAuthoringGraphProjector.TryGetNamespace(
            referenceTarget,
            out string nodeNamespace
        )
            ? nodeNamespace
            : referenceTarget;
    }
}
