using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RtsSkillStudio.Agent.Patch;

public sealed partial class WorkbookPatchCompiler
{
    private WorkbookPatchCompileError? ValidateOwnership(JsonElement plan, JsonElement operation, WorkbookPatchWorkspace workspace)
    {
        if (registry.Creation?["editing"]?["sharedAssetPolicy"]?.GetValue<string>() != "RequireAcknowledgement"
            || GetString(plan, "sharedAssetPolicy") == "AcknowledgeShared") return null;
        foreach (string key in new[] { "skill", "asset", "parent", "group" })
        {
            if (!operation.TryGetProperty(key, out JsonElement target) || target.ValueKind != JsonValueKind.Object
                || GetString(target, "binding") != "Existing") continue;
            string ns = GetString(target, "namespace") ?? "";
            int id = target.TryGetProperty("groupKey", out JsonElement groupKey) && groupKey.TryGetInt32(out int group)
                ? group : target.TryGetProperty("id", out JsonElement idValue) && idValue.TryGetInt32(out int value) ? value : 0;
            if (CountIncoming(workspace, ns, id) > 1) return new("compiler.shared_asset", "目标被多个资产复用，请审阅共享修改范围后明确确认。", $"{ns}:{id}");
            JsonObject? definition = new WorkbookSemanticValueCodec(registry, workspace).Definitions().SingleOrDefault(item => item["namespace"]?.GetValue<string>() == ns);
            if (definition?["groupField"] is not null)
            {
                WorkbookPatchRecord? record = workspace.Tables.FirstOrDefault(table => table.Namespace == ns)?.Records.FirstOrDefault(record => record.Id == id);
                string raw = (record?.Fields.GetValueOrDefault(definition["groupField"]!.GetValue<string>()) ?? []).FirstOrDefault() ?? "";
                if (int.TryParse(raw, out int parentGroup) && CountIncoming(workspace, definition["groupNamespace"]!.GetValue<string>(), parentGroup) > 1)
                    return new("compiler.shared_asset", "所属分组被多个资产复用，请审阅共享修改范围后明确确认。", $"{ns}:{id}");
            }
        }
        return null;
    }

    private WorkbookPatchCompileResult CompileStructure(string planJson, WorkbookPatchWorkspace workspace)
    {
        try
        {
            JsonObject policy = registry.Creation?["editing"] as JsonObject ?? throw new InvalidDataException("未发布结构编辑契约。");
            JsonObject plan = JsonNode.Parse(planJson)!.AsObject();
            JsonObject op = plan["operations"]![0]!.AsObject();
            string kind = op["kind"]!.GetValue<string>();
            JsonObject target = op[kind == "DeleteAsset" ? "asset" : "group"]!.AsObject();
            if (target["binding"]?.GetValue<string>() != "Existing") throw new InvalidDataException("结构修改必须指定 Existing 目标。");
            string ns = target["namespace"]!.GetValue<string>();
            int id = target["groupKey"]?.GetValue<int>() ?? target["id"]!.GetValue<int>();
            JsonObject[] definitions = new WorkbookSemanticValueCodec(registry, workspace).Definitions();
            JsonObject definition = definitions.Single(item => item[kind == "DeleteAsset" ? "namespace" : "groupNamespace"]?.GetValue<string>() == ns);
            WorkbookPatchTable table = workspace.Tables.Single(table => table.Namespace == definition["namespace"]!.GetValue<string>());
            var args = new JsonObject { ["tableKey"] = table.TableKey, ["sourcePlan"] = plan.DeepClone() };
            string commandKind;
            if (kind == "DeleteAsset")
            {
                if (policy["deletionPolicy"]?.GetValue<string>() != "UnreferencedGroupMemberOnly" || definition["groupField"] is null)
                    throw new InvalidDataException("只允许删除契约声明的未被直接引用的分组成员。");
                WorkbookPatchRecord record = table.Records.Single(record => record.Id == id);
                if (CountIncoming(workspace, ns, id) > 0) throw new InvalidDataException("资产仍被直接引用，必须先解除引用。");
                string groupField = definition["groupField"]!.GetValue<string>();
                string group = (record.Fields.GetValueOrDefault(groupField) ?? []).FirstOrDefault() ?? "";
                int groupId = int.Parse(group, CultureInfo.InvariantCulture);
                if (CountIncoming(workspace, definition["groupNamespace"]!.GetValue<string>(), groupId) > 1
                    && plan["sharedAssetPolicy"]?.GetValue<string>() != "AcknowledgeShared")
                    throw new InvalidDataException("所属分组被多个资产复用，必须明确审阅共享修改。");
                if (table.Records.Count(candidate => (candidate.Fields.GetValueOrDefault(groupField) ?? []).Contains(group)) == 1
                    && CountIncoming(workspace, definition["groupNamespace"]!.GetValue<string>(), groupId) > 0)
                    throw new InvalidDataException("删除会使被引用的分组为空，必须先解除分组入口。");
                args["before"] = JsonSerializer.SerializeToNode(record.Fields);
                commandKind = "DeleteOwnedMember";
            }
            else
            {
                if (policy["groupOrderPolicy"]?.GetValue<string>() != "WorksheetRowOrder") throw new InvalidDataException("未发布分组排序策略。");
                string groupField = definition["groupField"]!.GetValue<string>();
                WorkbookPatchRecord[] members = table.Records.Where(record => (record.Fields.GetValueOrDefault(groupField) ?? []).Contains(id.ToString(CultureInfo.InvariantCulture)))
                    .OrderBy(record => record.SourceOrder).ToArray();
                JsonArray ordered = op["orderedMembers"]!.AsArray();
                if (ordered.Any(value => value?["binding"]?.GetValue<string>() != "Existing" || value?["namespace"]?.GetValue<string>() != table.Namespace))
                    throw new InvalidDataException("排序成员的类型或引用绑定错误。");
                int[] ids = ordered.Select(value => value!["id"]!.GetValue<int>()).ToArray();
                if (ids.Length == 0 || !ids.Order().SequenceEqual(members.Select(record => record.Id).Order()))
                    throw new InvalidDataException("排序必须且只能包含当前分组的全部成员，每个成员一次。");
                if (CountIncoming(workspace, ns, id) > 1 && plan["sharedAssetPolicy"]?.GetValue<string>() != "AcknowledgeShared")
                    throw new InvalidDataException("分组被多个资产复用，必须明确审阅共享修改。");
                if (ids.SequenceEqual(members.Select(record => record.Id))) return new("NoChange", null, null, [new("compiler.no_change", "成员顺序未变化。")]);
                args["before"] = new JsonArray(members.Select(record => (JsonNode?)JsonValue.Create(record.Id)).ToArray());
                args["orderedIds"] = new JsonArray(ids.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                commandKind = "ReorderGroupMembers";
            }
            var command = new WorkbookPatchCommand(0, op["operationId"]!.GetValue<string>(), commandKind,
                kind == "DeleteAsset" ? new(ns, id) : new(ns, GroupKey: id), args);
            var patch = new WorkbookPatchDocument(0, new string('0', 64), WorkbookPatchJsonUtilities.ComputePlanHash(planJson),
                new(workspace.WorkspaceId, workspace.Revision, workspace.SourceHash, registry.CapabilityRegistryVersion,
                    registry.DefaultValueContractVersion, registry.DefaultMechanismContractVersion), [], [command], []);
            patch = patch with { PatchId = WorkbookPatchJsonUtilities.ComputePatchId(patch) };
            return new("Compiled", patch, WorkbookPatchJson.Serialize(patch), []);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or JsonException or FormatException or NullReferenceException)
        {
            return new("Invalid", null, null, [new("compiler.invalid_structure", exception.Message)]);
        }
    }

    private int CountIncoming(WorkbookPatchWorkspace workspace, string ns, int id)
    {
        string raw = id.ToString(CultureInfo.InvariantCulture);
        int count = 0;
        foreach (WorkbookPatchTable table in workspace.Tables)
            foreach (WorkbookPatchRecord record in table.Records)
                foreach (WorkbookPatchField field in table.Fields.Where(field => field.RecordId is null || field.RecordId == record.Id))
                {
                    string? target = field.ReferenceTarget ?? (field.BindingKind == WorkbookPatchFieldBindingKind.ActionParameter ? field.ReferenceNamespace : null);
                    target ??= new WorkbookSemanticValueCodec(registry, workspace).Definitions().Where(item => item["namespace"]?.GetValue<string>() == table.Namespace)
                        .SelectMany(item => item["fields"]!.AsArray().OfType<JsonObject>()).FirstOrDefault(item => item["key"]?.GetValue<string>() == field.Key)?["referenceTarget"]?.GetValue<string>();
                    if (target != ns) continue;
                    if (field.BindingKind == WorkbookPatchFieldBindingKind.ActionParameter)
                    {
                        if (WorkbookPatchRecordValue.Read(record, field) == raw) count++;
                    }
                    else if ((record.Fields.GetValueOrDefault(field.Key) ?? []).Contains(raw)) count++;
                }
        return count;
    }
}
