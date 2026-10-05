using System.Globalization;
using TianshuDM.Application.GameData;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.HeroAuthoring;

namespace TianshuDM.Application.HeroAuthoring;

public sealed record HeroAuthoringAssetCategory(string Namespace, string Label, int Count);

public sealed record HeroAuthoringAsset(
    string Key,
    string Namespace,
    int LegacyId,
    string Label,
    string Summary,
    string Kind,
    string? TableKey,
    string WorkbookPath,
    string WorksheetName,
    int SourceRow,
    int IncomingReferenceCount,
    int OutgoingReferenceCount,
    int SourceUsageCount,
    int EffectiveReferenceCount,
    int OwnershipScopeCount,
    bool IsVirtual,
    bool IsMissing)
{
    public bool IsOrphan => IncomingReferenceCount == 0 && Namespace != "TbSkill";
    public bool IsShared => OwnershipScopeCount > 1;
}

public sealed record HeroAuthoringAssetCatalog(
    string Revision,
    int Total,
    IReadOnlyList<HeroAuthoringAssetCategory> Categories,
    IReadOnlyList<HeroAuthoringAsset> Items);

public sealed record HeroAuthoringPort(
    string Key,
    string Label,
    string Direction,
    string BindingKind,
    string Cardinality,
    bool Required,
    string? SourceField,
    int? ParameterIndex,
    IReadOnlyList<string> AcceptsNamespaces,
    IReadOnlyList<string> ConnectedAssetKeys);

public sealed record HeroAuthoringWorkspaceNode(
    HeroAuthoringAsset Asset,
    IReadOnlyList<HeroAuthoringPort> Ports);

public sealed record HeroAuthoringSkillWorkspace(
    string Revision,
    string FocusKey,
    IReadOnlyList<HeroAuthoringWorkspaceNode> Nodes,
    IReadOnlyList<HeroAuthoringGraphEdge> Edges);

public sealed class HeroAuthoringAssetCatalogService(
    IGameDataDraftStore store,
    HeroAuthoringGraphProjector projector,
    IHeroAuthoringSemanticSchemaSource schemaSource)
{
    private static readonly IReadOnlyList<(string Namespace, string Label)> AssetTypes =
    [
        ("TbSkill", "技能"),
        ("EffectGroup", "效果组(Effect Group)"),
        ("TbEffect", "效果(Effect)"),
        ("ConditionGroup", "条件组(Condition Group)"),
        ("TbCondition", "条件(Condition)"),
        ("TbBuff", "Buff"),
        ("TbSearch", "搜索配置"),
        ("TbBullet", "子弹"),
        ("TbTrap", "机关(Trap)"),
        ("TbDamagePipeline", "伤害管线"),
        ("TbSkillResource", "技能资源"),
    ];

    private static readonly Dictionary<string, (string Label, string Target, string Cardinality)> KnownFieldPorts =
        new Dictionary<string, (string Label, string Target, string Cardinality)>(StringComparer.Ordinal)
        {
            ["TbSkill:condition_id_array"] = ("释放条件", "TbCondition", "Many"),
            ["TbSkill:search_target"] = ("目标搜索", "TbSearch", "One"),
            ["TbSkill:pre_effect_group_id"] = ("释放前效果", "EffectGroup", "One"),
            ["TbSkill:effect_group_id"] = ("主要效果", "EffectGroup", "One"),
            ["TbSkill:post_effect_group_id"] = ("释放后效果", "EffectGroup", "One"),
            ["TbBuff:enter_effect"] = ("应用时效果", "EffectGroup", "One"),
            ["TbBuff:interval_effect"] = ("周期效果", "EffectGroup", "One"),
            ["TbBuff:finish_effect"] = ("结束时效果", "EffectGroup", "One"),
            ["TbBuff:finish_effect_id"] = ("结束时效果", "EffectGroup", "One"),
            ["TbBullet:effect_group_id"] = ("命中效果", "EffectGroup", "One"),
            ["TbBullet:finish_effect_id"] = ("结束时效果", "EffectGroup", "One"),
            ["TbTrap:buffs"] = ("Buff", "TbBuff", "Many"),
        };

    public HeroAuthoringAssetCatalog List(string? query, string? nodeNamespace, int limit = 100)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "资产结果数量必须在 1 到 200 之间。");
        }

        GameDataCatalog catalog = RequireCatalog();
        HeroAuthoringGraph graph = projector.ProjectCatalog(catalog);
        HeroAuthoringGraphEdge[] assetReferences = RealAssetReferences(graph.Edges);
        IReadOnlyDictionary<string, int> incoming = assetReferences
            .GroupBy(edge => edge.Target, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        IReadOnlyDictionary<string, int> outgoing = assetReferences
            .GroupBy(edge => edge.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        IReadOnlyDictionary<string, HeroAuthoringSourceUsageSummary> sourceUsage =
            HeroAuthoringSourceService.SummarizeSourceUsagesByNode(catalog, graph);
        HashSet<string> allowed = AssetTypes.Select(type => type.Namespace).ToHashSet(StringComparer.Ordinal);
        HeroAuthoringAsset[] allAssets = graph.Nodes
            .Where(node => allowed.Contains(node.Namespace) && !node.IsMissing)
            .Select(node => ToAsset(catalog, node, incoming, outgoing, sourceUsage))
            .ToArray();
        HeroAuthoringAssetCategory[] categories = AssetTypes
            .Select(type => new HeroAuthoringAssetCategory(
                type.Namespace,
                type.Label,
                allAssets.Count(asset => asset.Namespace == type.Namespace)))
            .Where(category => category.Count > 0)
            .ToArray();

        string term = query?.Trim() ?? string.Empty;
        HeroAuthoringAsset[] matches = allAssets
            .Where(asset => string.IsNullOrWhiteSpace(nodeNamespace)
                            || string.Equals(asset.Namespace, nodeNamespace, StringComparison.OrdinalIgnoreCase))
            .Where(asset => term.Length == 0
                            || asset.LegacyId.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase)
                            || asset.Label.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => AssetOrder(asset.Namespace))
            .ThenBy(asset => asset.LegacyId)
            .ToArray();
        return new HeroAuthoringAssetCatalog(
            HeroAuthoringCatalogRevision.Compute(catalog),
            matches.Length,
            categories,
            matches.Take(limit).ToArray());
    }

    public HeroAuthoringSkillWorkspace GetSkillWorkspace(int skillId, int depth = 12)
        => GetBehaviorWorkspace("TbSkill", skillId, depth);

    public HeroAuthoringSkillWorkspace GetBehaviorWorkspace(
        string rootNamespace,
        int rootId,
        int depth = 12)
    {
        GameDataCatalog catalog = RequireCatalog();
        HeroAuthoringGraph graph = projector.ProjectBehavior(catalog, rootNamespace, rootId, depth);
        HeroAuthoringGraph referenceGraph = projector.ProjectCatalog(catalog);
        HeroAuthoringSemanticSchema schema = schemaSource.Read();
        HeroAuthoringGraphEdge[] assetReferences = RealAssetReferences(referenceGraph.Edges);
        IReadOnlyDictionary<string, int> incoming = assetReferences
            .GroupBy(edge => edge.Target, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        IReadOnlyDictionary<string, int> outgoing = assetReferences
            .GroupBy(edge => edge.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        IReadOnlyDictionary<string, HeroAuthoringSourceUsageSummary> sourceUsage =
            HeroAuthoringSourceService.SummarizeSourceUsagesByNode(catalog, referenceGraph);
        HeroAuthoringWorkspaceNode[] nodes = graph.Nodes.Select(node =>
            new HeroAuthoringWorkspaceNode(
                ToAsset(catalog, node, incoming, outgoing, sourceUsage),
                BuildPorts(catalog, schema, node, graph.Edges))).ToArray();
        return new HeroAuthoringSkillWorkspace(
            HeroAuthoringCatalogRevision.Compute(catalog),
            graph.FocusKey,
            nodes,
            graph.Edges);
    }

    private static IReadOnlyList<HeroAuthoringPort> BuildPorts(
        GameDataCatalog catalog,
        HeroAuthoringSemanticSchema schema,
        HeroAuthoringGraphNode node,
        IReadOnlyList<HeroAuthoringGraphEdge> edges)
    {
        var ports = new List<HeroAuthoringPort>();
        if (node.Namespace is "EffectGroup" or "ConditionGroup")
        {
            string memberNamespace = node.Namespace == "EffectGroup" ? "TbEffect" : "TbCondition";
            string role = node.Namespace == "EffectGroup" ? "orderedEffect" : "orderedCondition";
            ports.Add(new HeroAuthoringPort(
                "members",
                node.Namespace == "EffectGroup" ? "有序效果" : "有序条件",
                "Output",
                "GroupMember",
                "Many",
                true,
                "group_id",
                null,
                [memberNamespace],
                edges.Where(edge => edge.Source == node.Key && edge.Role == role).Select(edge => edge.Target).ToArray()));
            return ports;
        }

        if (!HeroAuthoringGraphProjector.TryGetTableKey(node.Namespace, out string tableKey)) return ports;
        GameDataTable? table = catalog.Tables.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, tableKey, StringComparison.OrdinalIgnoreCase));
        if (table is null || node.IsMissing || node.IsVirtual) return ports;

        foreach (GameDataFieldDefinition field in table.Fields)
        {
            string descriptorKey = $"{node.Namespace}:{field.Key}";
            (string Label, string Target, string Cardinality)? descriptor =
                KnownFieldPorts.TryGetValue(descriptorKey, out var known)
                    ? known
                    : ResolveReferencePort(field);
            if (descriptor is null) continue;
            IReadOnlyList<string> connected = node.Fields.GetValueOrDefault(field.Key, [])
                .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 0)
                .Where(id => id > 0)
                .Select(id => $"{descriptor.Value.Target}:{id}")
                .ToArray();
            ports.Add(new HeroAuthoringPort(
                $"field:{field.Key}",
                descriptor.Value.Label,
                "Output",
                "Field",
                descriptor.Value.Cardinality,
                field.Required,
                field.Key,
                null,
                [descriptor.Value.Target],
                connected));
        }

        if (node.Namespace is "TbEffect" or "TbCondition")
        {
            IReadOnlyList<HeroAuthoringActionDefinition> actions = node.Namespace == "TbEffect"
                ? schema.Effects
                : schema.Conditions;
            HeroAuthoringActionDefinition? action = ResolveAction(table, node, actions);
            IReadOnlyList<string> parameters = node.Fields.GetValueOrDefault("action_param", []);
            if (action is not null)
            {
                foreach (HeroAuthoringParameterDefinition parameter in action.Parameters
                             .Where(parameter => parameter.Kind == HeroAuthoringParameterKind.Reference
                                                 && !string.IsNullOrWhiteSpace(parameter.ReferenceTarget)))
                {
                    int[] parameterIndexes = parameter.Repeating
                        ? ParameterIndexes(parameter, parameters.Count)
                            .Append(NextRepeatingParameterIndex(parameter, parameters.Count))
                            .Distinct()
                            .Order()
                            .ToArray()
                        : [parameter.Index];
                    foreach (int parameterIndex in parameterIndexes)
                    {
                        bool connected = parameterIndex < parameters.Count
                                         && int.TryParse(
                                             parameters[parameterIndex],
                                             NumberStyles.Integer,
                                             CultureInfo.InvariantCulture,
                                             out int id)
                                         && id > 0;
                        int sequence = ((parameterIndex - parameter.Index) / Math.Max(1, parameter.RepeatStep)) + 1;
                        ports.Add(new HeroAuthoringPort(
                            $"action:{action.Key}:{parameterIndex}",
                            parameter.Repeating ? $"{parameter.Label} · 第 {sequence} 项" : parameter.Label,
                            "Output",
                            "ActionParameter",
                            "One",
                            parameter.Required && parameterIndex == parameter.Index,
                            "action_param",
                            parameterIndex,
                            [parameter.ReferenceTarget!],
                            connected ? [$"{parameter.ReferenceTarget}:{parameters[parameterIndex]}"] : []));
                    }
                }
            }
        }

        return ports.DistinctBy(port => port.Key, StringComparer.Ordinal).ToArray();
    }

    private static (string Label, string Target, string Cardinality)? ResolveReferencePort(
        GameDataFieldDefinition field)
    {
        if (string.IsNullOrWhiteSpace(field.ReferenceTable)
            || !HeroAuthoringGraphProjector.TryGetNamespace(field.ReferenceTable, out string target))
        {
            return null;
        }
        string cardinality = field.Kind is GameDataFieldKind.List or GameDataFieldKind.DelimitedList ? "Many" : "One";
        return (field.Label, target, cardinality);
    }

    private static HeroAuthoringActionDefinition? ResolveAction(
        GameDataTable table,
        HeroAuthoringGraphNode node,
        IReadOnlyList<HeroAuthoringActionDefinition> actions)
    {
        IReadOnlyList<string> actionTypes = node.Fields.GetValueOrDefault("action_type", []);
        string? raw = actionTypes.Count > 0 ? actionTypes[0] : null;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        GameDataFieldDefinition? field = table.Fields.FirstOrDefault(candidate => candidate.Key == "action_type");
        GameDataOption? option = field?.Options.FirstOrDefault(candidate =>
            string.Equals(candidate.Value, raw, StringComparison.Ordinal)
            || string.Equals(candidate.Code, raw, StringComparison.Ordinal));
        return actions.FirstOrDefault(action =>
            string.Equals(action.Key, raw, StringComparison.Ordinal)
            || string.Equals(action.Label, raw, StringComparison.Ordinal)
            || action.LegacyValue == option?.LegacyValue
            || string.Equals(action.Key, option?.Code, StringComparison.Ordinal)
            || (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int legacy)
                && action.LegacyValue == legacy));
    }

    private static IEnumerable<int> ParameterIndexes(HeroAuthoringParameterDefinition parameter, int count) =>
        parameter.Repeating
            ? Enumerable.Range(parameter.Index, Math.Max(0, count - parameter.Index))
                .Where(index => (index - parameter.Index) % parameter.RepeatStep == 0)
            : [parameter.Index];

    private static int NextRepeatingParameterIndex(HeroAuthoringParameterDefinition parameter, int count)
    {
        int step = Math.Max(1, parameter.RepeatStep);
        int existingSlots = Math.Max(0, count - parameter.Index);
        int occupiedSteps = (existingSlots + step - 1) / step;
        return parameter.Index + occupiedSteps * step;
    }

    private static HeroAuthoringAsset ToAsset(
        GameDataCatalog catalog,
        HeroAuthoringGraphNode node,
        IReadOnlyDictionary<string, int> incoming,
        IReadOnlyDictionary<string, int> outgoing,
        IReadOnlyDictionary<string, HeroAuthoringSourceUsageSummary> sourceUsage)
    {
        string? tableKey = node.TableKey;
        if (string.IsNullOrWhiteSpace(tableKey))
        {
            tableKey = node.Namespace switch
            {
                "EffectGroup" => "effect",
                "ConditionGroup" => "condition",
                _ => null,
            };
        }
        GameDataTable? table = tableKey is null
            ? null
            : catalog.Tables.FirstOrDefault(candidate => string.Equals(candidate.Key, tableKey, StringComparison.OrdinalIgnoreCase));
        GameDataRecord? record = table?.Records.FirstOrDefault(candidate => candidate.Id == node.LegacyId);
        string summary = table is not null && record is not null
            ? HeroAuthoringGraphService.BuildSummary(table, record, node.Label)
            : node.Label;
        int incomingReferenceCount = incoming.GetValueOrDefault(node.Key);
        HeroAuthoringSourceUsageSummary usage =
            sourceUsage.GetValueOrDefault(node.Key) ?? new HeroAuthoringSourceUsageSummary(0, 0);
        int sourceUsageCount = usage.SourceUsageCount;
        int ownershipScopeCount = Math.Max(
            usage.OwnershipScopeCount,
            incomingReferenceCount > 0 ? 1 : 0);
        return new HeroAuthoringAsset(
            node.Key,
            node.Namespace,
            node.LegacyId,
            node.Label,
            summary,
            node.Kind,
            tableKey,
            table?.WorkbookPath ?? string.Empty,
            table?.WorksheetName ?? string.Empty,
            node.SourceRow,
            incomingReferenceCount,
            outgoing.GetValueOrDefault(node.Key),
            sourceUsageCount,
            Math.Max(incomingReferenceCount, sourceUsageCount),
            ownershipScopeCount,
            node.IsVirtual,
            node.IsMissing);
    }

    private GameDataCatalog RequireCatalog()
    {
        GameDataCatalog catalog = store.ReadGameDataCatalog()
                                  ?? throw new InvalidOperationException("请先选择并打开 Unity 工程。");
        if (catalog.Tables.All(table => table.Key != "skill"))
        {
            throw new InvalidOperationException("当前工作副本尚未包含英雄技能数据，请从表格重新导入后重试。");
        }
        return catalog;
    }

    private static int AssetOrder(string nodeNamespace)
    {
        int index = AssetTypes.Select(type => type.Namespace).ToList().IndexOf(nodeNamespace);
        return index < 0 ? int.MaxValue : index;
    }

    private static HeroAuthoringGraphEdge[] RealAssetReferences(IReadOnlyList<HeroAuthoringGraphEdge> edges) =>
        edges.Where(edge => !edge.Derived
                            && !edge.Source.StartsWith("SkillConditionGate:", StringComparison.Ordinal))
            .ToArray();
}
