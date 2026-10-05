using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.HeroAuthoring;

namespace TianshuDM.Application.HeroAuthoring;

public sealed class HeroAuthoringGraphProjector(IHeroAuthoringSemanticSchemaSource schemaSource)
{
    private static readonly Dictionary<string, string> Namespaces =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hero"] = "TbHero",
            ["hero-skin"] = "TbHeroSkin",
            ["hero-upgrade"] = "TbHeroUpgrade",
            ["hero-skill-description"] = "TbHeroSkillDes",
            ["skill"] = "TbSkill",
            ["effect"] = "TbEffect",
            ["buff"] = "TbBuff",
            ["condition"] = "TbCondition",
            ["search"] = "TbSearch",
            ["bullet"] = "TbBullet",
            ["damage-pipeline"] = "TbDamagePipeline",
            ["skill-resource"] = "TbSkillResource",
            ["resource"] = "TbResource",
            ["equipment"] = "TbEquipment",
            ["equipment-upgrade"] = "TbEquipmentUpgrade",
            ["random-bag"] = "TbRandomBag",
            ["random-set"] = "TbRandomSet",
            ["random-card"] = "TbRandomCard",
            ["view-function-component"] = "TbViewFunctionComponent",
            ["item"] = "TbItem",
            ["battle-hero-shop"] = "TbBattleHeroShop",
            ["battle-soldier-level-up"] = "TbBattleSoldierLevelUp",
            ["soldier-upgrade"] = "TbSoldierUpgrade",
            ["card"] = "TbCard",
            ["trap"] = "TbTrap",
            ["soldier"] = "TbSoldier",
            ["building"] = "TbBuilding",
            ["block"] = "TbBlock",
        };

    public static bool TryGetNamespace(string tableKey, out string nodeNamespace) =>
        Namespaces.TryGetValue(tableKey, out nodeNamespace!);

    public static bool TryGetTableKey(string nodeNamespace, out string tableKey)
    {
        KeyValuePair<string, string> match = Namespaces.FirstOrDefault(
            pair => string.Equals(pair.Value, nodeNamespace, StringComparison.OrdinalIgnoreCase));
        tableKey = match.Key;
        return !string.IsNullOrWhiteSpace(tableKey);
    }

    public HeroAuthoringGraph Project(
        GameDataCatalog catalog,
        string focusNamespace,
        int focusId,
        int depth = 1)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (depth is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "图谱层级必须在 0 到 5 之间。");
        }

        (Dictionary<string, HeroAuthoringGraphNode> nodes, List<HeroAuthoringGraphEdge> edges) = BuildGraph(catalog);
        string focusKey = Key(focusNamespace, focusId);
        return SelectGraph(focusKey, nodes, edges, Neighborhood(focusKey, depth, edges));
    }

    public HeroAuthoringGraph ProjectSkillBehavior(
        GameDataCatalog catalog,
        int skillId,
        int depth = 12) =>
        ProjectBehavior(catalog, "TbSkill", skillId, depth);

    public HeroAuthoringGraph ProjectBehavior(
        GameDataCatalog catalog,
        string rootNamespace,
        int rootId,
        int depth = 12)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!HeroAuthoringRootProfiles.TryNormalize(rootNamespace, out _))
        {
            throw new ArgumentException(
                $"行为根只支持 {HeroAuthoringRootProfiles.SupportedNames}。",
                nameof(rootNamespace));
        }
        if (depth is < 0 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "行为根层级必须在 0 到 12 之间。");
        }

        (Dictionary<string, HeroAuthoringGraphNode> nodes, List<HeroAuthoringGraphEdge> edges) = BuildGraph(catalog);
        string focusKey = Key(rootNamespace, rootId);
        return SelectGraph(focusKey, nodes, edges, DownstreamNeighborhood(focusKey, depth, edges));
    }

    public HeroAuthoringGraph ProjectCatalog(GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        (Dictionary<string, HeroAuthoringGraphNode> nodes, List<HeroAuthoringGraphEdge> edges) = BuildGraph(catalog);
        return new HeroAuthoringGraph(
            string.Empty,
            nodes.Values.OrderBy(node => node.Namespace, StringComparer.Ordinal).ThenBy(node => node.LegacyId).ToArray(),
            edges.DistinctBy(edge => edge.Id, StringComparer.Ordinal).ToArray());
    }

    private (Dictionary<string, HeroAuthoringGraphNode> Nodes, List<HeroAuthoringGraphEdge> Edges) BuildGraph(
        GameDataCatalog catalog)
    {
        HeroAuthoringSemanticSchema schema = schemaSource.Read();
        var nodes = BuildRecordNodes(catalog);
        var edges = new List<HeroAuthoringGraphEdge>();
        AddExplicitReferences(catalog, nodes, edges);
        AddVirtualGroups(catalog, nodes, edges);
        AddTypedActionReferences(catalog, schema, nodes, edges, "effect", schema.Effects);
        AddTypedActionReferences(catalog, schema, nodes, edges, "condition", schema.Conditions);
        AddSkillConditionGates(catalog, nodes, edges);
        AddDerivedHeroSkills(catalog, schema, nodes, edges);
        return (nodes, edges);
    }

    private static HeroAuthoringGraph SelectGraph(
        string focusKey,
        Dictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyList<HeroAuthoringGraphEdge> edges,
        HashSet<string> included)
    {
        if (!nodes.ContainsKey(focusKey))
        {
            throw new KeyNotFoundException($"英雄配置图谱中不存在节点 {focusKey}。");
        }
        HeroAuthoringGraphNode[] selectedNodes = included
            .Select(key => nodes[key] with { IsFocus = key == focusKey })
            .OrderByDescending(node => node.IsFocus)
            .ThenBy(node => node.Namespace, StringComparer.Ordinal)
            .ThenBy(node => node.LegacyId)
            .ToArray();
        HeroAuthoringGraphEdge[] selectedEdges = edges
            .Where(edge => included.Contains(edge.Source) && included.Contains(edge.Target))
            .DistinctBy(edge => edge.Id, StringComparer.Ordinal)
            .ToArray();
        return new HeroAuthoringGraph(focusKey, selectedNodes, selectedEdges);
    }

    private static Dictionary<string, HeroAuthoringGraphNode> BuildRecordNodes(GameDataCatalog catalog)
    {
        var nodes = new Dictionary<string, HeroAuthoringGraphNode>(StringComparer.Ordinal);
        foreach (GameDataTable table in catalog.Tables.Where(table => Namespaces.ContainsKey(table.Key)))
        {
            string nodeNamespace = Namespaces[table.Key];
            foreach (GameDataRecord record in table.Records)
            {
                string key = Key(nodeNamespace, record.Id);
                nodes[key] = new HeroAuthoringGraphNode(
                    key,
                    nodeNamespace,
                    record.Id,
                    Title(table, record),
                    table.DisplayName,
                    table.Key,
                    false,
                    false,
                    false,
                    false,
                    record.Fields,
                    record.SourceRow);
            }
        }

        return nodes;
    }

    private static void AddExplicitReferences(
        GameDataCatalog catalog,
        IDictionary<string, HeroAuthoringGraphNode> nodes,
        List<HeroAuthoringGraphEdge> edges)
    {
        foreach (GameDataTable table in catalog.Tables.Where(table => Namespaces.ContainsKey(table.Key)))
        {
            string sourceNamespace = Namespaces[table.Key];
            foreach (GameDataRecord record in table.Records)
            {
                string source = Key(sourceNamespace, record.Id);
                foreach (GameDataFieldDefinition field in table.Fields.Where(field => field.ReferenceTable is not null))
                {
                    if (!Namespaces.TryGetValue(field.ReferenceTable!, out string? targetNamespace))
                    {
                        continue;
                    }

                    IReadOnlyList<string> values = Values(record, field.Key);
                    for (int index = 0; index < values.Count; index++)
                    {
                        if (!TryPositiveId(values[index], out int targetId))
                        {
                            continue;
                        }

                        string role = Role(table.Key, field.Key);
                        AddEdge(
                            nodes,
                            edges,
                            source,
                            targetNamespace,
                            targetId,
                            role,
                            RoleLabel(role, field.Label),
                            values.Count > 1 ? $"第 {index + 1} 项" : null,
                            sourceField: field.Key);
                    }
                }
            }
        }
    }

    private static void AddVirtualGroups(
        GameDataCatalog catalog,
        IDictionary<string, HeroAuthoringGraphNode> nodes,
        ICollection<HeroAuthoringGraphEdge> edges)
    {
        if (TryTable(catalog, "effect", out GameDataTable? effects))
        {
            foreach (IGrouping<int, GameDataRecord> group in effects.Records
                         .Select(record => (Record: record, Group: FirstId(record, "group_id")))
                         .Where(value => value.Group > 0)
                         .GroupBy(value => value.Group, value => value.Record))
            {
                EnsureVirtualNode(nodes, "EffectGroup", group.Key, $"效果组 {group.Key}", "效果组");
                int order = 0;
                foreach (GameDataRecord effect in group)
                {
                    order++;
                    AddEdge(nodes, edges, Key("EffectGroup", group.Key), "TbEffect", effect.Id, "orderedEffect", "有序效果", $"第 {order} 项");
                }
            }
        }

        if (TryTable(catalog, "condition", out GameDataTable? conditions))
        {
            foreach (IGrouping<int, GameDataRecord> group in conditions.Records
                         .Select(record => (Record: record, Group: FirstId(record, "group_id")))
                         .Where(value => value.Group > 0)
                         .GroupBy(value => value.Group, value => value.Record))
            {
                EnsureVirtualNode(nodes, "ConditionGroup", group.Key, $"条件组 {group.Key}", "条件组");
                int order = 0;
                foreach (GameDataRecord condition in group)
                {
                    order++;
                    AddEdge(nodes, edges, Key("ConditionGroup", group.Key), "TbCondition", condition.Id, "orderedCondition", "有序条件", $"第 {order} 项");
                }
            }
        }

        foreach (GameDataTable table in catalog.Tables.Where(table => Namespaces.ContainsKey(table.Key)))
        {
            foreach (GameDataRecord record in table.Records)
            {
                AddGroupFields(table, record, nodes, edges);
            }
        }
    }

    private static void AddGroupFields(
        GameDataTable table,
        GameDataRecord record,
        IDictionary<string, HeroAuthoringGraphNode> nodes,
        ICollection<HeroAuthoringGraphEdge> edges)
    {
        (string Field, string Role, string Label)[] effectFields =
        [
            ("pre_effect_group_id", "preEffect", "前置效果"),
            ("effect_group_id", "mainEffect", "主要效果"),
            ("effectGroupId", "mainEffect", "主要效果"),
            ("EffectGroupId", "mainEffect", "主要效果"),
            ("post_effect_group_id", "postEffect", "后置效果"),
            ("enter_effect", "enterEffect", "进入效果"),
            ("interval_effect", "intervalEffect", "周期效果"),
            ("finish_effect", "finishEffect", "结束效果"),
            ("finish_effect_id", "finishEffect", "结束效果"),
        ];
        string source = Key(Namespaces[table.Key], record.Id);
        foreach ((string field, string role, string label) in effectFields)
        {
            foreach (string value in Values(record, field))
            {
                if (!TryPositiveId(value, out int groupId)) continue;
                EnsureVirtualNode(nodes, "EffectGroup", groupId, $"效果组 {groupId}", "效果组");
                AddEdge(nodes, edges, source, "EffectGroup", groupId, role, label, sourceField: field);
            }
        }

    }

    private static void AddTypedActionReferences(
        GameDataCatalog catalog,
        HeroAuthoringSemanticSchema schema,
        IDictionary<string, HeroAuthoringGraphNode> nodes,
        List<HeroAuthoringGraphEdge> edges,
        string tableKey,
        IReadOnlyList<HeroAuthoringActionDefinition> definitions)
    {
        if (!TryTable(catalog, tableKey, out GameDataTable? table)) return;
        GameDataFieldDefinition? typeField = table.Fields.FirstOrDefault(
            field => string.Equals(field.Key, "action_type", StringComparison.OrdinalIgnoreCase));
        if (typeField is null) return;
        foreach (GameDataRecord record in table.Records)
        {
            string? rawType = FirstValue(Values(record, "action_type"));
            HeroAuthoringActionDefinition? definition = ResolveAction(definitions, typeField, rawType);
            if (definition is null) continue;
            string source = Key(Namespaces[tableKey], record.Id);
            HeroAuthoringGraphNode sourceNode = nodes[source];
            var fields = sourceNode.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            fields["__action"] = [definition.Label];
            fields["__executor"] = [$"{definition.Label} · {definition.Key} · 枚举值 {definition.LegacyValue}"];
            IReadOnlyList<string> parameters = Values(record, "action_param");
            if (tableKey == "effect" && definition.Key is "ConditionBranch" or "ConditionGroupBranch")
            {
                fields["__branch_mode"] = [definition.Key == "ConditionBranch"
                    ? "单条件"
                    : GroupCompletionLabel(Parameter(parameters, 1), true)];
                int failureParameterIndex = definition.Key == "ConditionBranch" ? 2 : 3;
                fields["__branch_failure"] = [ParameterId(parameters, failureParameterIndex) > 0
                    ? "已配置失败分支"
                    : "未配置，失败后结束"];
            }
            nodes[source] = sourceNode with { Fields = fields };
            foreach (HeroAuthoringParameterDefinition parameter in definition.Parameters
                         .Where(parameter => parameter.Kind == HeroAuthoringParameterKind.Reference))
            {
                IEnumerable<int> indexes = parameter.Repeating
                    ? Enumerable.Range(parameter.Index, Math.Max(0, parameters.Count - parameter.Index))
                        .Where(index => (index - parameter.Index) % parameter.RepeatStep == 0)
                    : [parameter.Index];
                foreach (int index in indexes)
                {
                    if (index >= parameters.Count || !TryPositiveId(parameters[index], out int targetId)) continue;
                    (string role, string label) = TypedReferencePresentation(definition, parameter);
                    if (parameter.ReferenceTarget is "EffectGroup" or "ConditionGroup")
                    {
                        string groupLabel = parameter.ReferenceTarget == "EffectGroup"
                            ? $"效果组 {targetId}"
                            : $"条件组 {targetId}";
                        string groupKind = parameter.ReferenceTarget == "EffectGroup" ? "效果组" : "条件组";
                        EnsureVirtualNode(nodes, parameter.ReferenceTarget, targetId, groupLabel, groupKind);
                    }
                    AddEdge(
                        nodes,
                        edges,
                        source,
                        parameter.ReferenceTarget!,
                        targetId,
                        role,
                        label,
                        parameter.Repeating ? $"参数 {index}" : null,
                        sourceField: "action_param",
                        parameterIndex: index);
                }
            }
        }
    }

    private static (string Role, string Label) TypedReferencePresentation(
        HeroAuthoringActionDefinition definition,
        HeroAuthoringParameterDefinition parameter)
    {
        if (definition.Key is not ("ConditionBranch" or "ConditionGroupBranch"))
        {
            return (parameter.Key, parameter.Label);
        }

        return parameter.Key switch
        {
            "conditionId" or "conditionGroupId" => ("branchCondition", "判定条件"),
            "successEffect" => ("branchSuccess", "成立"),
            "failureEffect" => ("branchFailure", "不成立"),
            _ => (parameter.Key, parameter.Label),
        };
    }

    private static void AddSkillConditionGates(
        GameDataCatalog catalog,
        IDictionary<string, HeroAuthoringGraphNode> nodes,
        ICollection<HeroAuthoringGraphEdge> edges)
    {
        if (!TryTable(catalog, "skill", out GameDataTable? skills)) return;
        foreach (GameDataRecord skill in skills.Records)
        {
            int[] conditionIds = Values(skill, "condition_id_array")
                .Select(value => TryPositiveId(value, out int id) ? id : 0)
                .Where(id => id > 0)
                .ToArray();
            string mode = GroupCompletionLabel(FirstValue(Values(skill, "release_condition_type")), conditionIds.Length > 0);
            string gateKey = $"SkillConditionGate:{skill.Id}";
            var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["__condition_mode"] = [mode],
                ["__condition_count"] = [conditionIds.Length.ToString(CultureInfo.InvariantCulture)],
            };
            if (conditionIds.Length == 0) fields["__condition_state"] = ["未配置条件，直接通过"];
            nodes[gateKey] = new HeroAuthoringGraphNode(
                gateKey,
                "SkillConditionGate",
                skill.Id,
                $"释放条件 · {mode}",
                "条件门",
                "skill",
                true,
                false,
                false,
                false,
                fields,
                skill.SourceRow);
            AddEdgeByKey(edges, Key("TbSkill", skill.Id), gateKey, "releaseConditionGate", "释放前判定");

            for (int index = 0; index < conditionIds.Length; index++)
            {
                AddEdge(
                    nodes,
                    edges,
                    gateKey,
                    "TbCondition",
                    conditionIds[index],
                    "releaseCondition",
                    $"条件 {index + 1}",
                    sourceField: "condition_id_array",
                    parameterIndex: index);
            }

            foreach ((string field, string role, string label) in new[]
                     {
                         ("pre_effect_group_id", "preEffect", "通过后：前置效果"),
                         ("effect_group_id", "mainEffect", "通过后：主要效果"),
                         ("post_effect_group_id", "postEffect", "通过后：后置效果"),
                     })
            {
                foreach (string value in Values(skill, field))
                {
                    if (!TryPositiveId(value, out int groupId)) continue;
                    AddEdge(nodes, edges, gateKey, "EffectGroup", groupId, role, label, sourceField: field);
                }
            }
        }
    }

    private static string GroupCompletionLabel(string? rawValue, bool hasConditions)
    {
        if (!hasConditions) return "无条件";
        return rawValue?.Trim() switch
        {
            "1" or "All" or "所有" => "全部满足",
            "2" or "Any" or "任一" => "任一满足",
            "0" or "None" or "无" => "无条件",
            { Length: > 0 } value => value,
            _ => "未指定",
        };
    }

    private static string? Parameter(IReadOnlyList<string> parameters, int index) =>
        index >= 0 && index < parameters.Count ? parameters[index] : null;

    private static int ParameterId(IReadOnlyList<string> parameters, int index) =>
        TryPositiveId(Parameter(parameters, index), out int id) ? id : 0;

    private static void AddDerivedHeroSkills(
        GameDataCatalog catalog,
        HeroAuthoringSemanticSchema schema,
        IDictionary<string, HeroAuthoringGraphNode> nodes,
        ICollection<HeroAuthoringGraphEdge> edges)
    {
        if (TryTable(catalog, "hero-upgrade", out GameDataTable? upgrades)
            && TryTable(catalog, "item", out GameDataTable? items)
            && TryTable(catalog, "effect", out GameDataTable? effects))
        {
            HeroAuthoringActionDefinition? addSkill = schema.Effects.FirstOrDefault(
                definition => definition.Key == "AddSkill");
            GameDataFieldDefinition? actionType = effects.Fields.FirstOrDefault(field => field.Key == "action_type");
            foreach (GameDataRecord upgrade in upgrades.Records)
            {
                int heroId = FirstId(upgrade, "group_id");
                int itemId = FirstId(upgrade, "UnlockItem");
                GameDataRecord? item = items.Records.FirstOrDefault(record => record.Id == itemId);
                int groupId = item is null ? 0 : FirstId(item, "effect_group_id");
                if (heroId <= 0 || groupId <= 0 || actionType is null || addSkill is null) continue;
                string level = FirstValue(Values(upgrade, "level")) ?? "未知";
                foreach (GameDataRecord effect in effects.Records.Where(record => FirstId(record, "group_id") == groupId))
                {
                    HeroAuthoringActionDefinition? definition = ResolveAction(
                        schema.Effects,
                        actionType,
                        FirstValue(Values(effect, "action_type")));
                    if (definition?.Key != "AddSkill") continue;
                    foreach (string value in Values(effect, "action_param"))
                    {
                        if (!TryPositiveId(value, out int skillId)) continue;
                        AddEdge(nodes, edges, Key("TbHero", heroId), "TbSkill", skillId, "upgradeSkill", "升级解锁技能", $"{level} 级解锁", true);
                    }
                }
            }
        }

        int playerHeroId = schema.Rules?.PlayerHeroId ?? 0;
        if (playerHeroId > 0 && TryTable(catalog, "equipment", out GameDataTable? equipment))
        {
            foreach (GameDataRecord record in equipment.Records)
            {
                foreach (string value in Values(record, "skills"))
                {
                    if (!TryPositiveId(value, out int skillId)) continue;
                    AddEdge(nodes, edges, Key("TbHero", playerHeroId), "TbSkill", skillId, "equipmentSkill", "装备授予技能", $"装备 {record.Id}", true);
                }
            }
        }
    }

    private static HeroAuthoringActionDefinition? ResolveAction(
        IReadOnlyList<HeroAuthoringActionDefinition> definitions,
        GameDataFieldDefinition typeField,
        string? rawType)
    {
        if (string.IsNullOrWhiteSpace(rawType)) return null;
        GameDataOption? option = typeField.Options.FirstOrDefault(
            candidate => string.Equals(candidate.Value, rawType, StringComparison.Ordinal)
                         || string.Equals(candidate.Code, rawType, StringComparison.Ordinal));
        if (option?.LegacyValue is { } legacy)
        {
            return definitions.FirstOrDefault(definition => definition.LegacyValue == legacy);
        }

        if (int.TryParse(rawType, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
        {
            return definitions.FirstOrDefault(definition => definition.LegacyValue == numeric);
        }

        return definitions.FirstOrDefault(
            definition => string.Equals(definition.Key, rawType, StringComparison.Ordinal)
                          || string.Equals(definition.Label, rawType, StringComparison.Ordinal));
    }

    private static HashSet<string> Neighborhood(
        string focus,
        int depth,
        IReadOnlyList<HeroAuthoringGraphEdge> edges)
    {
        var included = new HashSet<string>(StringComparer.Ordinal) { focus };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { focus };
        for (int level = 0; level < depth; level++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (HeroAuthoringGraphEdge edge in edges)
            {
                if (frontier.Contains(edge.Source)
                    && !IsCodeTerminal(edge.Source)
                    && included.Add(edge.Target))
                {
                    next.Add(edge.Target);
                }
                if (frontier.Contains(edge.Target)
                    && !IsCodeTerminal(edge.Target)
                    && included.Add(edge.Source))
                {
                    next.Add(edge.Source);
                }
            }
            frontier = next;
            if (frontier.Count == 0) break;
        }
        return included;
    }

    private static HashSet<string> DownstreamNeighborhood(
        string focus,
        int depth,
        IReadOnlyList<HeroAuthoringGraphEdge> edges)
    {
        var included = new HashSet<string>(StringComparer.Ordinal) { focus };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { focus };
        for (int level = 0; level < depth; level++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (HeroAuthoringGraphEdge edge in edges)
            {
                if (frontier.Contains(edge.Source)
                    && !IsCodeTerminal(edge.Source)
                    && included.Add(edge.Target))
                {
                    next.Add(edge.Target);
                }
            }
            frontier = next;
            if (frontier.Count == 0) break;
        }
        return included;
    }

    private static bool IsCodeTerminal(string key) =>
        key.StartsWith("CodeEffectExecutor:", StringComparison.Ordinal)
        || key.StartsWith("CodeConditionHandler:", StringComparison.Ordinal);

    private static void AddEdge(
        IDictionary<string, HeroAuthoringGraphNode> nodes,
        ICollection<HeroAuthoringGraphEdge> edges,
        string source,
        string targetNamespace,
        int targetId,
        string role,
        string label,
        string? detail = null,
        bool derived = false,
        string? sourceField = null,
        int? parameterIndex = null)
    {
        string target = Key(targetNamespace, targetId);
        EnsureMissingNode(nodes, source);
        EnsureMissingNode(nodes, target, targetNamespace, targetId);
        string id = $"{source}|{role}|{target}|{detail}";
        if (edges.Any(edge => edge.Id == id)) return;
        edges.Add(new HeroAuthoringGraphEdge(id, source, target, role, label, detail, derived, sourceField, parameterIndex));
    }

    private static void AddEdgeByKey(
        ICollection<HeroAuthoringGraphEdge> edges,
        string source,
        string target,
        string role,
        string label)
    {
        string id = $"{source}|{role}|{target}|";
        if (edges.Any(edge => edge.Id == id)) return;
        edges.Add(new HeroAuthoringGraphEdge(id, source, target, role, label));
    }

    private static void EnsureVirtualNode(
        IDictionary<string, HeroAuthoringGraphNode> nodes,
        string nodeNamespace,
        int id,
        string label,
        string kind)
    {
        string key = Key(nodeNamespace, id);
        if (nodes.ContainsKey(key)) return;
        nodes[key] = new HeroAuthoringGraphNode(key, nodeNamespace, id, label, kind, null, true, false, false, false, new Dictionary<string, IReadOnlyList<string>>());
    }

    private static void EnsureMissingNode(
        IDictionary<string, HeroAuthoringGraphNode> nodes,
        string key,
        string? nodeNamespace = null,
        int id = 0)
    {
        if (nodes.ContainsKey(key)) return;
        string resolvedNamespace = nodeNamespace ?? key.Split(':', 2)[0];
        int resolvedId = id > 0 ? id : int.TryParse(key.Split(':', 2).ElementAtOrDefault(1), out int parsed) ? parsed : 0;
        nodes[key] = new HeroAuthoringGraphNode(key, resolvedNamespace, resolvedId, $"缺失配置 {resolvedId}", "缺失配置", null, false, false, true, false, new Dictionary<string, IReadOnlyList<string>>());
    }

    private static string Role(string tableKey, string fieldKey)
    {
        if (tableKey == "hero" && fieldKey == "normal_skills") return "baseSkill";
        if (tableKey == "hero" && fieldKey == "active_skills") return "inactiveActiveSkill";
        if (fieldKey == "search_target") return "targetSearch";
        if (fieldKey == "skin") return "defaultSkin";
        return fieldKey;
    }

    private static string RoleLabel(string role, string fallback) => role switch
    {
        "baseSkill" => "基础技能",
        "inactiveActiveSkill" => "主动技能（运行时未使用）",
        "targetSearch" => "目标搜索",
        "defaultSkin" => "默认皮肤",
        _ => fallback,
    };

    private static string Title(GameDataTable table, GameDataRecord record)
    {
        foreach (string field in new[] { "name", "Name", "__remark_2", "remark", "desc", "Desc", "description" })
        {
            string? value = Values(record, field).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (value is not null) return value;
        }
        return $"{table.DisplayName} {record.Id}";
    }

    private static IReadOnlyList<string> Values(GameDataRecord record, string key) =>
        record.Fields.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value ?? [];

    private static int FirstId(GameDataRecord record, string key) =>
        TryPositiveId(FirstValue(Values(record, key)), out int id) ? id : 0;

    private static string? FirstValue(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : values[0];

    private static bool TryPositiveId(string? value, out int id) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;

    private static bool TryTable(
        GameDataCatalog catalog,
        string key,
        [NotNullWhen(true)] out GameDataTable? table)
    {
        table = catalog.Tables.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        return table is not null;
    }

    private static string Key(string nodeNamespace, int id) => $"{nodeNamespace}:{id}";
}
