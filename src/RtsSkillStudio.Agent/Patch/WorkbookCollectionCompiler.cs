using System.Text.Json;
using System.Text.Json.Nodes;
using TianshuDM.Domain.GameData;

namespace RtsSkillStudio.Agent.Patch;

public sealed partial class WorkbookPatchCompiler
{
    private static WorkbookPatchWorkspace ReserveCreatedRecords(WorkbookPatchWorkspace workspace, WorkbookPatchDocument patch)
    {
        return workspace with { Tables = workspace.Tables.Select(table => table with
        {
            Records = table.Records.Concat(patch.Commands.Where(command => command.Kind == "CreateNode" && command.Target.Namespace == table.Namespace)
                .Select(command => new WorkbookPatchRecord(command.Target.Id!.Value,
                    command.Arguments["fields"]!.AsObject().ToDictionary(pair => pair.Key,
                        pair => (IReadOnlyList<string>)pair.Value!.AsArray().Select(value => value!.GetValue<string>()).ToArray())))).ToArray()
        }).ToArray() };
    }

    private bool RequiresCollectionCompiler(JsonElement operation, WorkbookPatchWorkspace workspace)
    {
        string? kind = GetString(operation, "kind");
        if (kind == "LinkExisting") return true;
        if (kind is not ("ModifySkill" or "ModifyAsset" or "RemoveLink")) return false;
        string targetKey = kind == "ModifySkill" ? "skill" : kind == "ModifyAsset" ? "asset" : "parent";
        if (!operation.TryGetProperty(targetKey, out JsonElement target)) return false;
        string? ns = GetString(target, "namespace");
        WorkbookPatchTable? table = workspace.Tables.FirstOrDefault(table => table.Namespace == ns || table.EntityKey == ns);
        if (table is null) return false;
        if (kind == "RemoveLink")
            return table.Fields.Any(field => field.SemanticName == GetString(operation, "field") && WorkbookSemanticValueCodec.IsList(field));
        if (!operation.TryGetProperty("fields", out JsonElement fields) || fields.ValueKind != JsonValueKind.Object) return false;
        return fields.EnumerateObject().Any(input => table.Fields.Any(field => field.SemanticName == input.Name
            && (WorkbookSemanticValueCodec.IsList(field) || field.Repeating || field.Kind == GameDataFieldKind.Map
                || field.ReferenceTarget is not null && registry.Entities.Any(entity => entity.Namespace == field.ReferenceTarget && entity.Kind == "Virtual")
                || (registry.Creation?["nestedTypes"] as JsonArray ?? []).OfType<JsonObject>().Any(type => type["key"]?.GetValue<string>() == field.RawType))));
    }

    private WorkbookPatchCompileResult CompileCollections(string planJson, WorkbookPatchWorkspace workspace)
    {
        try
        {
            JsonObject policy = registry.Creation?["editing"] as JsonObject ?? throw new InvalidDataException("未发布集合编辑契约。");
            if (policy["sequencePolicy"]?.GetValue<string>() != "Replace" || policy["repeatingPolicy"]?.GetValue<string>() != "ParallelArrays")
                throw new InvalidDataException("不支持的集合编辑策略。");
            JsonObject plan = JsonNode.Parse(planJson)!.AsObject();
            JsonObject op = plan["operations"]![0]!.AsObject();
            string kind = op["kind"]!.GetValue<string>();
            string operationId = op["operationId"]!.GetValue<string>();
            JsonObject target = op[kind == "ModifySkill" ? "skill" : kind == "ModifyAsset" ? "asset" : "parent"]!.AsObject();
            if (target["binding"]?.GetValue<string>() != "Existing") throw new InvalidDataException("编辑目标必须为 Existing。");
            int id = target["id"]!.GetValue<int>();
            string ns = target["namespace"]!.GetValue<string>();
            WorkbookPatchTable table = workspace.Tables.Single(table => table.Namespace == ns || table.EntityKey == ns);
            WorkbookPatchRecord record = table.Records.Single(record => record.Id == id);
            var codec = new WorkbookSemanticValueCodec(registry, workspace);
            JsonObject inputs;
            if (kind is "LinkExisting" or "RemoveLink")
            {
                string name = op["field"]!.GetValue<string>();
                WorkbookPatchField field = Resolve(name);
                if (op["parameterIndex"] is not null && op["parameterIndex"]!.GetValue<int>() != field.ParameterIndex)
                    throw new InvalidDataException("parameterIndex 与语义字段不一致。");
                string raw = codec.Scalar(op["target"], null, WorkbookSemanticValueCodec.IsList(field) ? codec.Element(field) : field);
                JsonNode? result = op["target"]!.DeepClone();
                if (WorkbookSemanticValueCodec.IsList(field))
                {
                    List<string> values = (record.Fields.GetValueOrDefault(field.Key) ?? []).Where(value => value.Length > 0).ToList();
                    if (kind == "RemoveLink")
                    {
                        if (!values.Remove(raw)) throw new InvalidDataException("要移除的链接与当前列表不一致。");
                    }
                    else if (!values.Contains(raw)) values.Add(raw);
                    result = new JsonArray(values.Select(value => (JsonNode?)new JsonObject
                    {
                        ["binding"] = "Existing", ["namespace"] = field.ReferenceTarget,
                        ["id"] = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                    }).ToArray());
                }
                else if (kind == "RemoveLink") throw new InvalidDataException("标量移除应使用已声明的移除编码。");
                inputs = new JsonObject { [name] = new JsonObject { ["value"] = result, ["source"] = "ModelProposed" } };
            }
            else inputs = op["fields"]!.AsObject();
            var changes = new List<WorkbookFieldChange>();
            var repeated = new Dictionary<string, (WorkbookPatchField Field, JsonObject Value, JsonArray Items)>();
            foreach ((string name, JsonNode? input) in inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                WorkbookPatchField field = Resolve(name);
                JsonObject value = input!.AsObject();
                string source = value["source"]?.GetValue<string>() ?? "";
                if (source is not ("UserEdited" or "ModelProposed" or "Default") || !value.ContainsKey("value"))
                    throw new InvalidDataException($"{name} 的 Value 来源或值不合法。");
                JsonObject? definition = codec.Definitions().SingleOrDefault(item => item["namespace"]?.GetValue<string>() == table.Namespace);
                if (field.BindingKind == WorkbookPatchFieldBindingKind.Scalar && definition is not null
                    && new[] { "identityField", "actionField", "parameterField", "groupField" }.Any(key => definition[key]?.GetValue<string>() == field.Key))
                    throw new InvalidDataException($"{name} 由结构编译器管理。");
                if (field.Repeating)
                {
                    repeated.Add(name, (field, value, value["value"] as JsonArray ?? throw new InvalidDataException($"重复参数 {name} 需要数组。")));
                    continue;
                }
                if (WorkbookSemanticValueCodec.IsList(field) || field.Kind == GameDataFieldKind.Map)
                {
                    bool map = field.Kind == GameDataFieldKind.Map;
                    JsonArray items = map ? new JsonArray(codec.Map(value["value"], field).Select(item => (JsonNode?)JsonValue.Create(item)).ToArray())
                        : value["value"] as JsonArray ?? throw new InvalidDataException($"{name} 需要数组。" );
                    if (field.Kind != GameDataFieldKind.DelimitedList && items.Count > field.ColumnCount)
                        throw new InvalidDataException($"{name} 超出工作簿列容量。");
                    IReadOnlyList<string> before = record.Fields.GetValueOrDefault(field.Key) ?? [];
                    for (int i = 0; i < Math.Max(before.Count, items.Count); i++)
                        Change(field.Key + "[" + i + "]", name, i < before.Count ? before[i] : "",
                            i < items.Count ? map ? items[i]!.GetValue<string>() : codec.Scalar(items[i], value["unit"]?.GetValue<string>(), codec.Element(field)) : "", value, source);
                }
                else Change(field.Key, name, WorkbookPatchRecordValue.Read(record, field),
                    codec.Scalar(value["value"], value["unit"]?.GetValue<string>(), field), value, source);
            }
            if (repeated.Count > 0)
            {
                WorkbookPatchField[] peers = table.Fields.Where(field => field.RecordId == id && field.Repeating).ToArray();
                if (peers.Length != repeated.Count || peers.Any(peer => !repeated.ContainsKey(peer.SemanticName!)))
                    throw new InvalidDataException("重复参数必须同时提供所有成对数组，不能部分覆盖。");
                int count = repeated.First().Value.Items.Count;
                if (repeated.Values.Any(item => item.Items.Count != count || item.Field.RepeatStep <= 0) || count == 0 && peers.Any(peer => peer.Required))
                    throw new InvalidDataException("重复参数数组长度必须一致，必填参数不能为空。");
                WorkbookPatchField storage = table.Fields.Single(field => field.Key == "action_param" && field.RecordId is null);
                List<string> output = [];
                foreach (var item in repeated.Values)
                    for (int i = 0; i < count; i++)
                    {
                        int index = checked(item.Field.ParameterIndex!.Value + i * item.Field.RepeatStep);
                        if (storage.Kind != GameDataFieldKind.DelimitedList && index >= storage.ColumnCount) throw new InvalidDataException("重复参数超出工作簿列容量。");
                        while (output.Count <= index) output.Add("");
                        output[index] = codec.Scalar(item.Items[i], item.Value["unit"]?.GetValue<string>(), item.Field);
                    }
                if (output.Any(value => value.Length == 0)) throw new InvalidDataException("重复参数编码不能有空槽。");
                IReadOnlyList<string> before = record.Fields.GetValueOrDefault(storage.Key) ?? [];
                for (int index = 0; index < Math.Max(output.Count, before.Count); index++)
                {
                    var owner = repeated.Values.FirstOrDefault(item => index >= item.Field.ParameterIndex && (index - item.Field.ParameterIndex) % item.Field.RepeatStep == 0);
                    JsonObject provenance = owner.Value ?? repeated.First().Value.Value;
                    Change(storage.Key + "[" + index + "]", owner.Field?.SemanticName ?? storage.Key,
                        index < before.Count ? before[index] : "", index < output.Count ? output[index] : "", provenance, provenance["source"]!.GetValue<string>());
                }
            }
            if (changes.Count == 0) return new("NoChange", null, null, [new("compiler.no_change", "请求没有产生字段变化。")]);
            var command = new WorkbookPatchCommand(0, operationId, "UpdateNode", new(table.Namespace, id),
                new JsonObject { ["fields"] = new JsonArray(changes.Select(change => (JsonNode?)JsonValue.Create(change.Field)).ToArray()), ["sourcePlan"] = plan.DeepClone() });
            var patch = new WorkbookPatchDocument(0, new string('0', 64), WorkbookPatchJsonUtilities.ComputePlanHash(planJson),
                new(workspace.WorkspaceId, workspace.Revision, workspace.SourceHash, registry.CapabilityRegistryVersion,
                    registry.DefaultValueContractVersion, registry.DefaultMechanismContractVersion), [], [command], changes);
            patch = patch with { PatchId = WorkbookPatchJsonUtilities.ComputePatchId(patch) };
            return new("Compiled", patch, WorkbookPatchJson.Serialize(patch), []);

            WorkbookPatchField Resolve(string name) => table.Fields.Where(field => field.SemanticName == name && (field.RecordId is null || field.RecordId == id))
                .OrderByDescending(field => field.RecordId == id).FirstOrDefault() ?? throw new InvalidDataException($"未知语义字段 {name}。");
            void Change(string address, string semantic, string before, string after, JsonObject value, string source)
            {
                if (before == after) return;
                changes.Add(new(operationId, new(table.TableKey, id, address), table.Namespace, id, address, semantic,
                    JsonSerializer.SerializeToElement(value["value"]), value["unit"]?.GetValue<string>(),
                    JsonSerializer.SerializeToElement(before), JsonSerializer.SerializeToElement(after), source, [$"Capability:{table.Namespace}.{semantic}"]));
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or JsonException or FormatException or OverflowException or ArgumentException or NullReferenceException)
        {
            return new("Invalid", null, null, [new("compiler.invalid_collection", exception.Message)]);
        }
    }
}
