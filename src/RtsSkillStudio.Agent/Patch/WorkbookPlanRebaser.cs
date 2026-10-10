using System.Text.Json.Nodes;

namespace RtsSkillStudio.Agent.Patch;

public sealed record WorkbookPlanRebaseResult(string? PlanJson, IReadOnlyList<WorkbookPatchCompileError> Errors);

/// <summary>Retains user/model/default proposals only if every reviewed before
/// value is still current. Never replaces a baseline silently.</summary>
public static class WorkbookPlanRebaser
{
    public static WorkbookPlanRebaseResult Rebase(string planJson, string reviewedPatchJson,
        WorkbookPatchWorkspace workspace, WorkbookPatchRegistry registry)
    {
        try
        {
            WorkbookPatchDocument patch = WorkbookPatchJson.Deserialize(reviewedPatchJson)!;
            if (patch.SourcePlanHash != WorkbookPatchJsonUtilities.ComputePlanHash(planJson)
                || patch.PatchId != WorkbookPatchJsonUtilities.ComputePatchId(patch)
                || patch.Base.WorkspaceId != workspace.WorkspaceId
                || patch.Base.CapabilityRegistryVersion != registry.CapabilityRegistryVersion
                || patch.Base.DefaultValueContractVersion != registry.DefaultValueContractVersion
                || patch.Base.DefaultMechanismContractVersion != registry.DefaultMechanismContractVersion)
                return new(null, [new("compiler.rebase_conflict", "复核 Patch 与 Plan、工作区或契约版本不一致。")]);
            var errors = new List<WorkbookPatchCompileError>();
            if (WorkbookPatchImpact.RowCommands(patch).Count > 0)
                return new(null, [new("compiler.rebase_conflict", "结构修改需要基于新快照重新生成，不能自动重基。")]);
            foreach (WorkbookFieldChange change in patch.FieldChanges)
            {
                WorkbookPatchTable? table = workspace.Tables.SingleOrDefault(table => table.Namespace == change.Namespace);
                WorkbookPatchRecord? record = table?.Records.SingleOrDefault(record => record.Id == change.Id);
                bool creation = patch.Commands.Any(command => command.Kind == "CreateNode" && command.Target.Namespace == change.Namespace && command.Target.Id == change.Id);
                if (table is null || creation && record is not null || !creation && record is null)
                {
                    errors.Add(new("compiler.rebase_conflict", "表或记录已变化，需要重新生成并审阅。", change.LogicalAddress.ToString()));
                    continue;
                }
                if (creation) continue;
                string key = change.Field;
                int index = key.LastIndexOf('[');
                string current = index > 0 && key.EndsWith(']') && int.TryParse(key[(index + 1)..^1], out int offset)
                    ? (record!.Fields.GetValueOrDefault(key[..index]) ?? []).ElementAtOrDefault(offset) ?? ""
                    : (record!.Fields.GetValueOrDefault(key) ?? []).FirstOrDefault() ?? "";
                if (current != WorkbookFieldChangeJson.RawText(change.Before))
                    errors.Add(new("compiler.rebase_conflict", $"{change.Source} 提案的原值已被其他修改改变，不能自动覆盖。", change.LogicalAddress.ToString()));
            }
            if (errors.Count > 0) return new(null, errors);
            JsonObject plan = JsonNode.Parse(planJson)!.AsObject();
            plan["base"] = System.Text.Json.JsonSerializer.SerializeToNode(new WorkbookPatchBase(workspace.WorkspaceId,
                workspace.Revision, workspace.SourceHash, registry.CapabilityRegistryVersion,
                registry.DefaultValueContractVersion, registry.DefaultMechanismContractVersion), WorkbookPatchJson.Options);
            return new(plan.ToJsonString(), []);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or ArgumentException or NullReferenceException)
        {
            return new(null, [new("compiler.rebase_conflict", "无法验证重基依据：" + exception.Message)]);
        }
    }
}
