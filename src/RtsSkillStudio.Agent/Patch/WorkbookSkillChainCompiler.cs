using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TianshuDM.Domain.GameData;

namespace RtsSkillStudio.Agent.Patch;

/// <summary>Compiles a semantic asset graph. Workbook bindings and creation policy
/// come from the versioned contract; the model never supplies IDs or cell writes.</summary>
public sealed class WorkbookSkillChainCompiler(WorkbookPatchRegistry registry)
{
    private sealed record Asset(string Key, string Namespace, int Id, JsonObject Definition, JsonObject Plan, WorkbookPatchTable? Table);
    private sealed class CompileFailure(string message) : Exception(message);

    public WorkbookPatchCompileResult Compile(string planJson, WorkbookPatchWorkspace workspace)
    {
        try { return CompileGraph(planJson, workspace); }
        catch (Exception exception) when (exception is CompileFailure or JsonException or InvalidOperationException or FormatException or OverflowException or NullReferenceException or ArgumentException)
        {
            return new("Invalid", null, null, [new("compiler.invalid_creation", exception.Message)]);
        }
    }

    private WorkbookPatchCompileResult CompileGraph(string planJson, WorkbookPatchWorkspace workspace)
    {
        JsonObject policy = registry.Creation ?? throw new CompileFailure("工作区没有发布创建契约。");
        Require(Text(policy, "allocationPolicy") == "MaxPlusOne" && Text(policy, "cyclePolicy") == "Reject", "不支持的创建策略。");
        JsonObject plan = JsonNode.Parse(planJson)!.AsObject();
        JsonArray operations = plan["operations"]!.AsArray();
        Require(operations.Count == 1, "完整创建链路必须使用一个事务。");
        JsonObject operation = operations[0]!.AsObject();
        bool extending = Text(operation, "kind") == "ExtendAssetChain";
        Require(extending || Text(operation, "kind") == "CreateSkillChain", "不是创建链路操作。");
        JsonObject? parent = extending ? operation["parent"]?.AsObject() : null;
        if (extending) Require(Text(parent!, "binding") == "Existing", "扩展链路必须指定 Existing 父资产。");
        string operationId = RequiredText(operation, "operationId");
        JsonObject[] definitions = policy["entities"]!.AsArray().OfType<JsonObject>().ToArray();
        JsonObject[] nodes = operation["nodes"]!.AsArray().OfType<JsonObject>().ToArray();
        Require(nodes.Length > 0 && nodes.Length <= policy["maximumNodes"]!.GetValue<int>()
            && nodes.Length == operation["nodes"]!.AsArray().Count, "创建节点数量不合法。");
        var assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        var usedIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var allocations = new List<WorkbookPatchAllocation>();
        JsonArray groups = operation["groups"] as JsonArray ?? [];
        Require(groups.Count <= policy["maximumNodes"]!.GetValue<int>()
            && groups.All(group => group is JsonObject), "创建分组数量或结构不合法。");
        foreach (JsonObject group in groups.OfType<JsonObject>())
        {
            string ns = RequiredText(group, "namespace");
            JsonObject definition = definitions.SingleOrDefault(definition => Text(definition, "groupNamespace") == ns)
                ?? throw new CompileFailure($"未声明分组 {ns}。");
            WorkbookPatchTable table = FindTable(workspace, RequiredText(definition, "namespace"));
            string groupField = RequiredText(definition, "groupField");
            int maximum = table.Records.SelectMany(record => record.Fields.GetValueOrDefault(groupField) ?? [])
                .Select(value => int.TryParse(value, out int id) ? id : 0).DefaultIfEmpty(0).Max();
            AddAsset(group, definition, null, ns, maximum, "GroupId");
        }
        foreach (JsonObject node in nodes)
        {
            string ns = RequiredText(node, "namespace");
            JsonObject definition = definitions.SingleOrDefault(definition => Text(definition, "namespace") == ns)
                ?? throw new CompileFailure($"未声明可创建实体 {ns}。");
            WorkbookPatchTable table = FindTable(workspace, ns);
            Require(Text(definition, "allocation") == "MaxPlusOne", "未声明 ID 分配策略。");
            AddAsset(node, definition, table, ns, table.Records.Select(record => record.Id).DefaultIfEmpty(0).Max(), "Id");
        }
        string root = RequiredText(operation, extending ? "entry" : "root");
        Require(assets.TryGetValue(root, out Asset? rootAsset) && (extending || rootAsset!.Table is not null
            && rootAsset.Definition["root"]?.GetValue<bool>() == true), "root 必须引用契约声明的根节点。");

        var changes = new List<WorkbookFieldChange>();
        var commands = new List<WorkbookPatchCommand>();
        foreach (Asset asset in assets.Values.Where(asset => asset.Table is not null))
        {
            WorkbookPatchTable table = asset.Table!;
            JsonObject supplied = asset.Plan["fields"] as JsonObject ?? throw new CompileFailure("节点 fields 必须是对象。");
            JsonObject[] declaredFields = asset.Definition["fields"]!.AsArray().OfType<JsonObject>().ToArray();
            var rawFields = declaredFields.ToDictionary(field => RequiredText(field, "key"), _ => new List<string>(), StringComparer.Ordinal);
            var reserved = new[] { Text(asset.Definition, "identityField"), Text(asset.Definition, "actionField"), Text(asset.Definition, "parameterField"), Text(asset.Definition, "groupField") }
                .Where(value => value is not null).ToHashSet(StringComparer.Ordinal);
            foreach (JsonObject declared in declaredFields)
            {
                WorkbookPatchField field = BoundField(table, declared);
                string key = field.Key;
                if (Text(asset.Definition, "identityField") == key)
                    WriteValues(field, declared, [asset.Id.ToString(CultureInfo.InvariantCulture)], null, "Compiler");
                else if (asset.Definition["initialFields"]?[key] is JsonArray defaults)
                    WriteValues(field, declared, defaults.Select(value => value!.GetValue<string>()).ToArray(), null, "Default");
            }
            foreach ((string semanticName, JsonNode? input) in supplied)
            {
                JsonObject declared = declaredFields.SingleOrDefault(field => Text(field, "semanticName") == semanticName)
                    ?? throw new CompileFailure($"{asset.Namespace} 未声明语义字段 {semanticName}。");
                WorkbookPatchField field = BoundField(table, declared);
                Require(!reserved.Contains(field.Key), $"字段 {semanticName} 由编译器管理。");
                JsonObject value = input as JsonObject ?? throw new CompileFailure("字段必须使用带来源的 Value 对象。");
                string source = ReadSource(value);
                JsonNode? semantic = value["value"];
                List<string> raw = ConvertValue(semantic, Text(value, "unit"), field, declared, asset.Key);
                // Explicit input replaces a declared default, including its diff row.
                changes.RemoveAll(change => change.Namespace == asset.Namespace && change.Id == asset.Id && (change.Field == field.Key || change.Field.StartsWith(field.Key + "[", StringComparison.Ordinal)));
                WriteValues(field, declared, raw, value, source);
            }
            string? groupField = Text(asset.Definition, "groupField");
            if (groupField is not null)
            {
                JsonObject groupRef = asset.Plan["group"] as JsonObject ?? throw new CompileFailure($"{asset.Key} 必须声明所属分组。");
                string groupNamespace = RequiredText(asset.Definition, "groupNamespace");
                string groupId = ResolveReference(groupRef, groupNamespace, asset.Key, membership: true);
                JsonObject declared = declaredFields.Single(field => Text(field, "key") == groupField);
                WriteValues(BoundField(table, declared), declared, [groupId], null, "Compiler");
            }
            string? category = Text(asset.Definition, "actionCategory");
            if (category is not null)
            {
                string actionKey = RequiredText(asset.Plan, "actionKey");
                WorkbookPatchAction action = registry.Actions.SingleOrDefault(action => action.Category == category && action.Key == actionKey)
                    ?? throw new CompileFailure($"未声明动作 {category}:{actionKey}。");
                JsonObject actionDeclared = declaredFields.Single(field => Text(field, "key") == Text(asset.Definition, "actionField"));
                WorkbookPatchField actionField = BoundField(table, actionDeclared);
                GameDataOption? option = actionField.Options.FirstOrDefault(option => option.Code == action.Key || option.Value == action.Key || option.LegacyValue == action.LegacyValue);
                Require(option is not null, $"动作 {actionKey} 与工作簿枚举不一致。");
                WriteValues(actionField, actionDeclared, [option!.Value], null, "Compiler");
                JsonObject parameters = asset.Plan["parameters"] as JsonObject ?? new JsonObject();
                Require(parameters.All(pair => action.Parameters.Any(parameter => parameter.Key == pair.Key)), "存在未声明的动作参数。");
                string parameterField = RequiredText(asset.Definition, "parameterField");
                WorkbookPatchField storage = table.Fields.Single(field => field.Key == parameterField && field.RecordId is null);
                var parameterValues = new List<string>();
                foreach (WorkbookPatchActionParameter parameter in action.Parameters.OrderBy(parameter => parameter.Index))
                {

                    JsonObject? value = parameters[parameter.Key] as JsonObject;
                    if (value is null && parameter.DefaultValue is { } defaultValue)
                        value = new JsonObject { ["value"] = JsonNode.Parse(defaultValue.GetRawText()), ["source"] = "Default" };
                    Require(value is not null || !parameter.Required, $"缺少动作参数 {actionKey}.{parameter.Key}。");
                    if (value is null) continue;
                    JsonArray values = parameter.Repeating
                        ? value["value"] as JsonArray ?? throw new CompileFailure($"重复参数 {parameter.Key} 需要数组。")
                        : new JsonArray(value["value"]?.DeepClone());
                    Require(!parameter.Repeating || parameter.RepeatStep > 0 && (values.Count > 0 || !parameter.Required), "重复参数必须有合法步长和完整值。");
                    if (parameter.Repeating)
                    {
                        int[] lengths = action.Parameters.Where(peer => peer.Repeating).Select(peer =>
                            parameters[peer.Key]?["value"] is JsonArray array ? array.Count : -1).ToArray();
                        Require(lengths.All(length => length == values.Count), "成对重复参数必须同时提供长度相同的数组。");
                    }
                    for (int occurrence = 0; occurrence < values.Count; occurrence++)
                    {
                        int index = checked(parameter.Index + occurrence * parameter.RepeatStep);
                        Require(index < storage.ColumnCount || storage.Kind == GameDataFieldKind.DelimitedList, $"动作 {actionKey} 参数超出工作簿列数。");
                        while (parameterValues.Count <= index) parameterValues.Add("");
                        var projected = new WorkbookPatchField(
                            parameterField + "[" + index + "]", asset.Namespace + "." + parameter.Key,
                            parameter.Key, parameter.FieldKind, parameter.RawType, parameter.Required, parameter.Scale,
                            parameter.Unit, parameter.Minimum, parameter.Maximum, parameter.Options, parameter.ReferenceTarget,
                            ReferenceTarget: parameter.ReferenceTarget, AllowsMultipleEnumValues: parameter.AllowsMultipleEnumValues);
                        JsonObject parameterDeclared = new() { ["key"] = projected.Key, ["semanticName"] = parameter.Key,
                            ["referenceTarget"] = parameter.ReferenceTarget, ["blankAllowed"] = !parameter.Required };
                        string raw = ConvertValue(values[occurrence], Text(value, "unit"), projected, parameterDeclared, asset.Key).Single();
                        parameterValues[index] = raw;
                        AddChange(asset, projected.Key, parameter.Key, raw, value, ReadSource(value));
                    }
                }
                Require(parameterValues.All(value => value.Length > 0), "动作参数中间不能缺项。");
                rawFields[parameterField] = parameterValues;
            }
            else Require(asset.Plan["actionKey"] is null && asset.Plan["parameters"] is null && asset.Plan["group"] is null, "该实体没有动作或分组创建契约。");

            foreach (JsonObject declared in declaredFields)
            {
                WorkbookPatchField field = BoundField(table, declared);
                Require(declared["blankAllowed"]?.GetValue<bool>() == true || rawFields[field.Key].Count > 0, $"缺少字段 {field.Path}。");
            }
            if (asset.Key == root && asset.Definition["root"]?.GetValue<bool>() == true)
            {
                JsonArray executionFields = asset.Definition["executionFields"] as JsonArray ?? throw new CompileFailure("技能根未声明执行入口。");
                Require(executionFields.Any(key => rawFields.GetValueOrDefault(key!.GetValue<string>())?.Any(value => value is not ("" or "0" or "null")) == true), "技能必须有非空执行入口。");
            }
            var arguments = new JsonObject { ["fields"] = new JsonObject(rawFields.Select(pair =>
                new KeyValuePair<string, JsonNode?>(pair.Key, new JsonArray(pair.Value.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())))) };
            commands.Add(new(commands.Count, operationId, "CreateNode", new(asset.Namespace, asset.Id, LocalKey: asset.Key), arguments));

            void WriteValues(WorkbookPatchField field, JsonObject declared, IReadOnlyList<string> values, JsonObject? semanticValue, string source)
            {
                Require(values.Count <= field.ColumnCount || field.Kind == GameDataFieldKind.DelimitedList, $"字段 {field.Path} 超出列数。");
                rawFields[field.Key] = values.ToList();
                for (int index = 0; index < values.Count; index++)
                {
                    string raw = values[index];
                    if (semanticValue is null)
                        ValidateRaw(raw, ElementField(field), declared, asset.Key);
                    string address = IsList(field) || field.Kind == GameDataFieldKind.Map ? field.Key + "[" + index + "]" : field.Key;
                    AddChange(asset, address, field.SemanticName ?? field.Key, raw, semanticValue, source);
                }
            }
        }
        foreach (Asset group in assets.Values.Where(asset => asset.Table is null))
            Require(edges[group.Key].Count > 0, $"分组 {group.Key} 不能为空。");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        Visit(root);
        Require(visited.Count == assets.Count, "链路包含未连接到技能根的资产或分组。");
        if (extending)
        {
            string parentNamespace = RequiredText(parent!, "namespace");
            JsonObject? groupDefinition = definitions.SingleOrDefault(definition => Text(definition, "groupNamespace") == parentNamespace);
            if (groupDefinition is not null)
            {
                int groupId = parent!["groupKey"]?.GetValue<int>() ?? parent["id"]!.GetValue<int>();
                Require(rootAsset!.Table?.Namespace == Text(groupDefinition, "namespace")
                    && Text(rootAsset.Plan["group"]!.AsObject(), "binding") == "Existing"
                    && (rootAsset.Plan["group"]!["groupKey"]?.GetValue<int>() ?? rootAsset.Plan["group"]!["id"]?.GetValue<int>()) == groupId,
                    "添加分组成员的入口必须属于指定 Existing 父分组。");
                Require(operation["field"] is null, "分组成员扩展不使用 field。");
            }
            else
            {
                WorkbookPatchTable table = FindTable(workspace, parentNamespace);
                int parentId = parent!["id"]!.GetValue<int>();
                WorkbookPatchRecord record = table.Records.Single(record => record.Id == parentId);
                string name = RequiredText(operation, "field");
                JsonObject definition = definitions.Single(definition => Text(definition, "namespace") == parentNamespace);
                JsonObject declared = definition["fields"]!.AsArray().OfType<JsonObject>().Single(field => Text(field, "semanticName") == name);
                WorkbookPatchField field = BoundField(table, declared);
                Require(WorkbookPatchReference.TryResolveTarget(field, out string target, out _) && target == rootAsset!.Namespace,
                    "父字段引用目标与新链路入口不一致。");
                string raw = rootAsset!.Id.ToString(CultureInfo.InvariantCulture);
                string address = field.Key;
                string before = WorkbookPatchRecordValue.Read(record, field);
                if (IsList(field))
                {
                    int index = (record.Fields.GetValueOrDefault(field.Key) ?? []).Count;
                    Require(field.Kind == GameDataFieldKind.DelimitedList || index < field.ColumnCount, "父列表容量不足。");
                    address += "[" + index + "]";
                    before = "";
                }
                changes.Add(new(operationId, new(table.TableKey, parentId, address), table.Namespace, parentId, address, name,
                    JsonSerializer.SerializeToElement(new { binding = "Local", @namespace = rootAsset.Namespace, localKey = root }),
                    null, JsonSerializer.SerializeToElement(before), JsonSerializer.SerializeToElement(raw), "Compiler", ["CreationContract:ExtendAssetChain"]));
                commands.Add(new(commands.Count, operationId, "UpdateNode", new(table.Namespace, parentId),
                    new JsonObject { ["fields"] = new JsonArray(address) }));
            }
        }
        commands[0].Arguments["sourcePlan"] = plan.DeepClone();
        var identity = new WorkbookPatchBase(workspace.WorkspaceId, workspace.Revision, workspace.SourceHash,
            registry.CapabilityRegistryVersion, registry.DefaultValueContractVersion, registry.DefaultMechanismContractVersion);
        var patch = new WorkbookPatchDocument(0, new string('0', 64), WorkbookPatchJsonUtilities.ComputePlanHash(planJson), identity, allocations, commands, changes);
        patch = patch with { PatchId = WorkbookPatchJsonUtilities.ComputePatchId(patch) };
        return new("Compiled", patch, WorkbookPatchJson.Serialize(patch), []);

        void AddAsset(JsonObject item, JsonObject definition, WorkbookPatchTable? table, string ns, int maximum, string kind)
        {
            string key = RequiredText(item, "localKey");
            Require(!assets.ContainsKey(key), $"重复 localKey {key}。");
            int id = checked(Math.Max(maximum, usedIds.GetValueOrDefault(ns)) + 1);
            Require(id > 0, "分配 ID 必须为正数。");
            usedIds[ns] = id;
            assets.Add(key, new(key, ns, id, definition, item, table));
            edges.Add(key, []);
            allocations.Add(new(ns, key, id, kind, kind == "GroupId" ? id : null));
        }

        string ResolveReference(JsonObject value, string expected, string parentKey, bool membership = false)
        {
            string ns = RequiredText(value, "namespace");
            Require(WorkbookPatchReference.NamespaceMatches(ns, expected), $"引用目标应为 {expected}，收到 {ns}。");
            if (Text(value, "binding") == "Local")
            {
                string key = RequiredText(value, "localKey");
                Require(assets.TryGetValue(key, out Asset? target) && target.Namespace == ns, $"不存在本地引用 {ns}:{key}。");
                if (membership) edges[key].Add(parentKey); else edges[parentKey].Add(key);
                return target!.Id.ToString(CultureInfo.InvariantCulture);
            }
            Require(!membership || extending && parent is not null && Text(parent, "namespace") == ns
                && (parent["groupKey"]?.GetValue<int>() ?? parent["id"]?.GetValue<int>()) == (value["groupKey"]?.GetValue<int>() ?? value["id"]?.GetValue<int>()),
                "Existing 分组成员必须属于扩展操作明确指定的父分组。");
            Require(Text(value, "binding") == "Existing", "引用必须是 Existing 或 Local。");
            JsonObject? groupDefinition = definitions.SingleOrDefault(definition => Text(definition, "groupNamespace") == ns);
            if (groupDefinition is not null)
            {
                int groupId = value["groupKey"]?.GetValue<int>() ?? value["id"]?.GetValue<int>() ?? 0;
                WorkbookPatchTable table = FindTable(workspace, RequiredText(groupDefinition, "namespace"));
                Require(groupId > 0 && table.Records.Any(record => (record.Fields.GetValueOrDefault(RequiredText(groupDefinition, "groupField")) ?? []).Contains(groupId.ToString(CultureInfo.InvariantCulture))), $"分组 {ns}:{groupId} 不存在。");
                return groupId.ToString(CultureInfo.InvariantCulture);
            }
            int existingId = value["id"]?.GetValue<int>() ?? 0;
            Require(FindTable(workspace, ns).Records.Any(record => record.Id == existingId), $"引用资产 {ns}:{existingId} 不存在。");
            return existingId.ToString(CultureInfo.InvariantCulture);
        }

        List<string> ConvertValue(JsonNode? value, string? unit, WorkbookPatchField field, JsonObject declared, string parent)
        {
            if (field.Kind == GameDataFieldKind.Map)
                return new WorkbookSemanticValueCodec(registry, workspace).Map(value, field);
            if (IsList(field))
            {
                Require(value is JsonArray, $"{field.Path} 需要数组。");
                return value!.AsArray().Select(item => ConvertScalar(item, unit, ElementField(field), declared, parent)).ToList();
            }
            Require(field.Kind != GameDataFieldKind.Map, $"{field.Path} 尚未声明 map 创建编码。");
            return [ConvertScalar(value, unit, field, declared, parent)];
        }

        string ConvertScalar(JsonNode? value, string? unit, WorkbookPatchField field, JsonObject declared, string parent)
        {
            string? reference = Text(declared, "referenceTarget");
            if (reference is not null)
            {
                if (value is JsonObject target) return ResolveReference(target, reference, parent);
                throw new CompileFailure($"{field.Path} 需要带 namespace 的引用对象。");
            }
            JsonObject? nested = (policy["nestedTypes"] as JsonArray ?? []).OfType<JsonObject>()
                .SingleOrDefault(type => Text(type, "key") == field.RawType);
            if (nested is not null)
            {
                JsonObject input = value as JsonObject ?? throw new CompileFailure($"{field.Path} 需要 {field.RawType} 对象。");
                JsonObject encoding = nested["encoding"]!.AsObject();
                Require(Text(encoding, "kind") == "Delimited", "不支持的嵌套类型编码。");
                JsonObject[] members = nested["fields"]!.AsArray().OfType<JsonObject>().ToArray();
                Require(input.Count == members.Length && input.All(pair => members.Any(member => Text(member, "key") == pair.Key)), "嵌套类型字段不完整。");
                return string.Join(RequiredText(encoding, "separator"), encoding["fields"]!.AsArray().Select(key =>
                {
                    string name = key!.GetValue<string>();
                    JsonObject member = members.Single(member => Text(member, "key") == name);
                    string kind = RequiredText(member, "kind");
                    var memberField = new WorkbookPatchField(name, field.Path + "." + name, name,
                        kind == "Enum" ? GameDataFieldKind.Enum : GameDataFieldKind.Integer,
                        kind, true, member["scale"]?.GetValue<decimal>() ?? 1, null, null, null,
                        kind == "Enum" ? EnumOptions(Text(member, "enumName")) : [], null);
                    return ConvertScalar(input[name], null, memberField, new JsonObject(), parent);
                }));
            }
            JsonElement element = JsonSerializer.SerializeToElement(value);
            Require(WorkbookPatchValueConverter.TryConvert(element, unit, field, registry.ConversionRules, out string raw, out WorkbookPatchValueFailure? failure), failure?.Message ?? $"无法转换 {field.Path}。");
            ValidateRaw(raw, field, declared, parent);
            return raw;
        }

        void ValidateRaw(string raw, WorkbookPatchField field, JsonObject declared, string parent)
        {
            Require(WorkbookPatchValueConverter.IsRawValueCompatible(raw, field), $"{field.Path}={raw} 类型不合法。");
            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number))
                Require((field.Minimum is null || number >= field.Minimum) && (field.Maximum is null || number <= field.Maximum), $"{field.Path} 超出范围。");
            if (Text(declared, "referenceTarget") is { } ns)
            {
                Require((declared["emptyReferenceValues"] as JsonArray ?? []).Any(value => value!.GetValue<string>() == raw), $"默认引用 {field.Path} 不能隐式指向资产；必须使用引用对象。");
            }
        }

        void AddChange(Asset asset, string field, string semantic, string raw, JsonObject? value, string source)
        {
            changes.Add(new(operationId, new(asset.Table!.TableKey, asset.Id, field), asset.Namespace, asset.Id, field, semantic,
                value is null ? JsonSerializer.SerializeToElement(raw) : JsonSerializer.SerializeToElement(value["value"]),
                value is null ? null : Text(value, "unit"), JsonSerializer.SerializeToElement<object?>(null),
                JsonSerializer.SerializeToElement(raw), source, [$"CreationContract:{asset.Namespace}.{semantic}", $"LocalAsset:{asset.Key}"]));
        }

        void Visit(string key)
        {
            Require(!active.Contains(key), "创建链路包含循环引用。");
            if (!visited.Add(key)) return;
            active.Add(key);
            foreach (string child in edges[key]) Visit(child);
            active.Remove(key);
        }
    }

    private static WorkbookPatchField BoundField(WorkbookPatchTable table, JsonObject declared)
    {
        string key = RequiredText(declared, "key");
        WorkbookPatchField field = table.Fields.SingleOrDefault(field => field.Key == key && field.RecordId is null)
            ?? throw new CompileFailure($"工作区 {table.Namespace} 缺少字段 {key}。");
        Require(field.RawType == Text(declared, "rawType"), $"{table.Namespace}.{key} 工作簿类型与创建契约不一致。");
        string? target = Text(declared, "referenceTarget");
        Require(target is null || field.ReferenceNamespace is null || WorkbookPatchReference.NamespaceMatches(target, field.ReferenceNamespace), $"{key} 引用注解与创建契约不一致。");
        return field with { SemanticName = Text(declared, "semanticName"), ReferenceTarget = target, Required = declared["blankAllowed"]?.GetValue<bool>() != true };
    }
    private WorkbookPatchField ElementField(WorkbookPatchField field)
    {
        if (!IsList(field)) return field;
        string elementType = field.RawType.StartsWith("array,", StringComparison.OrdinalIgnoreCase)
            ? field.RawType[6..].Trim() : field.RawType.Split(',').Last().TrimEnd(')').Trim();
        GameDataFieldKind kind = field.ReferenceTarget is not null || elementType.Contains("#ref=", StringComparison.Ordinal) ? GameDataFieldKind.Reference
            : elementType.StartsWith('E') || field.Options.Count > 0 ? GameDataFieldKind.Enum
            : elementType.StartsWith("int", StringComparison.OrdinalIgnoreCase) ? GameDataFieldKind.Integer
            : elementType == "bool" ? GameDataFieldKind.Boolean : GameDataFieldKind.Text;
        Require(kind != GameDataFieldKind.Text || elementType.StartsWith("string", StringComparison.OrdinalIgnoreCase)
            || (registry.Creation?["nestedTypes"] as JsonArray ?? []).OfType<JsonObject>().Any(type => Text(type, "key") == elementType), $"嵌套类型 {elementType} 尚未声明创建编码。");
        return field with { Kind = kind, RawType = elementType, Required = true };
    }
    private IReadOnlyList<GameDataOption> EnumOptions(string? name)
    {
        JsonObject? definition = (registry.Creation?["enums"] as JsonArray ?? []).OfType<JsonObject>().SingleOrDefault(value => Text(value, "name") == name);
        Require(definition is not null, $"枚举 {name} 未声明。");
        return definition!["values"]!.AsArray().OfType<JsonObject>().Select(value =>
            new GameDataOption(value["value"]!.ToJsonString().Trim('"'), Text(value, "name") ?? "", Text(value, "name"))).ToArray();
    }
    private static bool IsList(WorkbookPatchField field) => field.Kind is GameDataFieldKind.List or GameDataFieldKind.DelimitedList;
    private static WorkbookPatchTable FindTable(WorkbookPatchWorkspace workspace, string ns) => workspace.Tables.SingleOrDefault(table => table.Namespace == ns)
        ?? throw new CompileFailure($"工作区缺少表 {ns}。");
    private static string ReadSource(JsonObject value)
    {
        string source = RequiredText(value, "source");
        Require(source is "UserEdited" or "ModelProposed" or "Default", "字段来源不合法。");
        Require(value.ContainsKey("value"), "Value 缺少 value。");
        return source;
    }
    private static string? Text(JsonObject value, string key) => value[key]?.GetValue<string>();
    private static string RequiredText(JsonObject value, string key) => Text(value, key) is { Length: > 0 } text ? text : throw new CompileFailure($"缺少 {key}。");
    private static void Require(bool condition, string message) { if (!condition) throw new CompileFailure(message); }
}
