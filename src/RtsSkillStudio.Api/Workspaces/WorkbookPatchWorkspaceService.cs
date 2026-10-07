using RtsSkillStudio.Agent.Patch;
using RtsSkillStudio.Agent.Llm;
using TianshuDM.Application.HeroAuthoring;
using TianshuDM.Domain.GameData;

namespace RtsSkillStudio.Api.Workspaces;

public sealed record WorkbookPatchCompileResponse(
    string Status,
    string? PatchJson,
    IReadOnlyList<WorkbookPatchCompileError> Errors,
    WorkbookPatchValidationReport? Validation,
    IReadOnlyList<WorkbookPatchDiffRow> Diff
);

public sealed record WorkbookPatchApplyResponse(
    string Status,
    WorkbookPatchValidationReport Validation,
    TemporaryWorkbookPatchApplyResult? ApplyResult
);

public sealed class WorkbookPatchWorkspaceService(
    SkillWorkspaceService workspace,
    SkillConfigPlanValidator planValidator,
    WorkbookPatchRegistry registry,
    WorkbookPatchCompiler compiler,
    WorkbookPatchValidator validator,
    TemporaryWorkbookPatchApplyService applyService,
    ILogger<WorkbookPatchWorkspaceService> logger
)
{
    public async Task<WorkbookPatchCompileResponse> CompileAsync(
        string planJson,
        CancellationToken cancellationToken
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
            diff
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

            WorkbookPatchField[] fields = table.Fields
                .Select(field => BuildField(entity, field))
                .ToArray();
            WorkbookPatchRecord[] records = table.Records
                .Select(
                    record => new WorkbookPatchRecord(
                        record.Id,
                        record.Fields
                    )
                )
                .ToArray();
            tables.Add(
                new WorkbookPatchTable(
                    entity.Key,
                    nodeNamespace,
                    table.Key,
                    fields,
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

        return new WorkbookPatchField(
            field.Key,
            path,
            registryField?.SemanticName,
            field.Kind,
            field.RawType,
            field.Required,
            registryField?.Scale ?? 1,
            registryField?.Unit,
            registryField?.Minimum,
            registryField?.Maximum,
            field.Options,
            referenceNamespace
        );
    }
}
