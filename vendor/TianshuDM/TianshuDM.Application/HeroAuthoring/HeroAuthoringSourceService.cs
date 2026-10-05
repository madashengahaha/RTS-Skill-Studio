using System.Globalization;
using TianshuDM.Application.GameData;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.HeroAuthoring;

namespace TianshuDM.Application.HeroAuthoring;

public sealed record HeroAuthoringSourceType(
    string Key,
    string Label,
    int ObjectCount);

public sealed record HeroAuthoringSourceObject(
    string Key,
    int LegacyId,
    string Label,
    string Summary);

public sealed record HeroAuthoringBehaviorRoot(
    string Key,
    string Namespace,
    int LegacyId,
    string Label,
    string Summary,
    string Category,
    string CategoryLabel,
    int IncomingReferenceCount,
    bool IsUnreferenced);

public sealed record HeroAuthoringSourceRoots(
    string Revision,
    string SourceType,
    string SourceTypeLabel,
    string SourceObjectKey,
    string SourceObjectLabel,
    IReadOnlyList<HeroAuthoringBehaviorRoot> Items,
    IReadOnlyList<string> Warnings);

public sealed record HeroAuthoringDirectReference(
    string SourceKey,
    string SourceNamespace,
    int SourceId,
    string SourceLabel,
    string Role,
    string Label,
    string? SourceField,
    int? ParameterIndex);

public sealed record HeroAuthoringReferenceSourceUsage(
    string SourceType,
    string SourceTypeLabel,
    int SourceObjectId,
    string SourceObjectKey,
    string SourceObjectLabel,
    IReadOnlyList<string> RootKeys);

public sealed record HeroAuthoringOwnershipScope(
    string ScopeKey,
    string ScopeType,
    string ScopeTypeLabel,
    int ScopeId,
    string ScopeLabel);

public sealed record HeroAuthoringReferenceReport(
    string Revision,
    string TargetKey,
    IReadOnlyList<HeroAuthoringDirectReference> DirectReferences,
    IReadOnlyList<HeroAuthoringReferenceSourceUsage> SourceUsages,
    IReadOnlyList<HeroAuthoringOwnershipScope> OwnershipScopes);

public sealed record HeroAuthoringSourceUsageSummary(
    int SourceUsageCount,
    int OwnershipScopeCount);

public sealed class HeroAuthoringSourceService(
    IGameDataDraftStore store,
    HeroAuthoringGraphProjector projector)
{
    private static readonly (string Key, string Label, string? TableKey)[] SourceTypes =
    [
        ("hero", "英雄", "hero"),
        ("soldier", "小兵", "soldier"),
        ("building", "建筑", "building"),
        ("equipment", "装备", "equipment"),
        ("trap", "机关", "trap"),
        ("item", "道具", "item"),
        ("unreferenced", "未引用资产", null),
    ];

    public IReadOnlyList<HeroAuthoringSourceType> ListSourceTypes()
    {
        GameDataCatalog catalog = RequireCatalog();
        HeroAuthoringGraph graph = projector.ProjectCatalog(catalog);
        IReadOnlyDictionary<string, int> incoming = RealIncomingReferences(graph);
        return SourceTypes.Select(source =>
        {
            int count = source.TableKey is null
                ? UnreferencedRoots(graph, incoming).Count()
                : FindTable(catalog, source.TableKey)?.Records.Count ?? 0;
            return new HeroAuthoringSourceType(source.Key, source.Label, count);
        }).ToArray();
    }

    public string CurrentRevision() => HeroAuthoringCatalogRevision.Compute(RequireCatalog());

    public IReadOnlyList<HeroAuthoringSourceObject> ListSourceObjects(
        string sourceType,
        string? query = null,
        int limit = 200)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "来源对象数量必须在 1 到 500 之间。");
        }

        (string key, string label, string? tableKey) = ResolveSourceType(sourceType);
        if (tableKey is null)
        {
            return [new HeroAuthoringSourceObject("unreferenced:all", 0, label, "全部未引用 Skill 和 EffectGroup")];
        }

        GameDataCatalog catalog = RequireCatalog();
        GameDataTable table = RequireTable(catalog, tableKey);
        string term = query?.Trim() ?? string.Empty;
        return table.Records
            .Select(record => new HeroAuthoringSourceObject(
                $"{key}:{record.Id}",
                record.Id,
                Title(table, record),
                ObjectSummary(table, record)))
            .Where(item => term.Length == 0
                           || item.LegacyId.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase)
                           || item.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || item.Summary.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.LegacyId)
            .Take(limit)
            .ToArray();
    }

    public HeroAuthoringSourceRoots ListSourceRoots(string sourceType, int sourceObjectId)
    {
        (string key, string label, string? tableKey) = ResolveSourceType(sourceType);
        GameDataCatalog catalog = RequireCatalog();
        HeroAuthoringGraph graph = projector.ProjectCatalog(catalog);
        IReadOnlyDictionary<string, int> incoming = RealIncomingReferences(graph);
        Dictionary<string, HeroAuthoringGraphNode> nodes = graph.Nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        var roots = new Dictionary<string, RootAccumulator>(StringComparer.Ordinal);
        var warnings = new List<string>();
        string objectLabel;

        if (tableKey is null)
        {
            objectLabel = label;
            foreach (HeroAuthoringGraphNode node in UnreferencedRoots(graph, incoming))
            {
                AddRoot(roots, nodes, incoming, "unreferenced", "未引用资产", node.Namespace, node.LegacyId);
            }
        }
        else
        {
            GameDataTable table = RequireTable(catalog, tableKey);
            GameDataRecord source = table.Records.FirstOrDefault(record => record.Id == sourceObjectId)
                                    ?? throw new KeyNotFoundException($"{label} {sourceObjectId} 不存在。");
            objectLabel = Title(table, source);
            AddSourceRoots(catalog, graph, nodes, incoming, roots, warnings, key, source);
        }

        HeroAuthoringBehaviorRoot[] items = roots.Values
            .OrderBy(root => CategoryOrder(root.Category))
            .ThenBy(root => root.Namespace, StringComparer.Ordinal)
            .ThenBy(root => root.LegacyId)
            .Select(root => BuildRoot(graph, nodes, incoming, root))
            .ToArray();
        return new HeroAuthoringSourceRoots(
            HeroAuthoringCatalogRevision.Compute(catalog),
            key,
            label,
            tableKey is null ? "unreferenced:all" : $"{key}:{sourceObjectId}",
            objectLabel,
            items,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public HeroAuthoringReferenceReport GetReferenceReport(string targetNamespace, int targetId)
    {
        GameDataCatalog catalog = RequireCatalog();
        HeroAuthoringGraph graph = projector.ProjectCatalog(catalog);
        Dictionary<string, HeroAuthoringGraphNode> nodes = graph.Nodes.ToDictionary(
            node => node.Key,
            StringComparer.Ordinal);
        string targetKey = $"{targetNamespace}:{targetId}";
        if (!nodes.ContainsKey(targetKey))
        {
            throw new KeyNotFoundException($"英雄配置图谱中不存在节点 {targetKey}。");
        }

        HeroAuthoringDirectReference[] directReferences = graph.Edges
            .Where(edge => edge.Target == targetKey
                           && !edge.Derived
                           && !edge.Source.StartsWith("SkillConditionGate:", StringComparison.Ordinal))
            .DistinctBy(
                edge => string.Join(
                    '|',
                    edge.Source,
                    edge.Role,
                    edge.SourceField,
                    edge.ParameterIndex),
                StringComparer.Ordinal)
            .Select(edge =>
            {
                HeroAuthoringGraphNode source = nodes[edge.Source];
                return new HeroAuthoringDirectReference(
                    edge.Source,
                    source.Namespace,
                    source.LegacyId,
                    source.Label,
                    edge.Role,
                    edge.Label,
                    edge.SourceField,
                    edge.ParameterIndex);
            })
            .OrderBy(reference => reference.SourceKey, StringComparer.Ordinal)
            .ToArray();

        HashSet<string> ancestors = UpstreamNodeKeys(targetKey, graph.Edges);
        IReadOnlyDictionary<string, int> incoming = RealIncomingReferences(graph);
        var warnings = new List<string>();
        SourceObjectDescriptor[] sourceObjects = BuildSourceObjectDescriptors(
            catalog,
            graph,
            nodes,
            incoming,
            warnings);
        var ownershipResolver = new OwnershipResolver(catalog, graph, sourceObjects);
        var usages = new List<HeroAuthoringReferenceSourceUsage>();
        var ownershipScopeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (SourceObjectDescriptor sourceObject in sourceObjects)
        {
            string[] matchedRoots = sourceObject.RootKeys
                .Where(ancestors.Contains)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (matchedRoots.Length == 0) continue;
            ownershipScopeKeys.UnionWith(ownershipResolver.Resolve(sourceObject));
            usages.Add(new HeroAuthoringReferenceSourceUsage(
                sourceObject.SourceType,
                sourceObject.SourceTypeLabel,
                sourceObject.SourceObjectId,
                sourceObject.SourceKey,
                sourceObject.SourceObjectLabel,
                matchedRoots));
        }

        return new HeroAuthoringReferenceReport(
            HeroAuthoringCatalogRevision.Compute(catalog),
            targetKey,
            directReferences,
            usages
                .OrderBy(usage => usage.SourceType, StringComparer.Ordinal)
                .ThenBy(usage => usage.SourceObjectId)
                .ToArray(),
            ownershipScopeKeys
                .Select(scopeKey => ResolveOwnershipScope(catalog, scopeKey))
                .OrderBy(scope => scope.ScopeType, StringComparer.Ordinal)
                .ThenBy(scope => scope.ScopeId)
                .ToArray());
    }

    public static IReadOnlyDictionary<string, HeroAuthoringSourceUsageSummary> SummarizeSourceUsagesByNode(
        GameDataCatalog catalog,
        HeroAuthoringGraph graph)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(graph);

        Dictionary<string, HeroAuthoringGraphNode> nodes = graph.Nodes.ToDictionary(
            node => node.Key,
            StringComparer.Ordinal);
        IReadOnlyDictionary<string, int> incoming = RealIncomingReferences(graph);
        IReadOnlyDictionary<string, HeroAuthoringGraphEdge[]> outgoing = graph.Edges
            .GroupBy(edge => edge.Source, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var usagesByNode = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var ownershipScopesByNode = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var warnings = new List<string>();
        SourceObjectDescriptor[] sourceObjects = BuildSourceObjectDescriptors(
            catalog,
            graph,
            nodes,
            incoming,
            warnings);
        var ownershipResolver = new OwnershipResolver(catalog, graph, sourceObjects);

        foreach (SourceObjectDescriptor sourceObject in sourceObjects)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(sourceObject.RootKeys.Where(nodes.ContainsKey));
            string sourceObjectKey = sourceObject.SourceKey;
            IReadOnlyList<string> ownershipScopes = ownershipResolver.Resolve(sourceObject);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (!visited.Add(current)) continue;

                if (!usagesByNode.TryGetValue(current, out HashSet<string>? usages))
                {
                    usages = new HashSet<string>(StringComparer.Ordinal);
                    usagesByNode[current] = usages;
                }
                usages.Add(sourceObjectKey);
                if (!ownershipScopesByNode.TryGetValue(current, out HashSet<string>? ownershipScopesForNode))
                {
                    ownershipScopesForNode = new HashSet<string>(StringComparer.Ordinal);
                    ownershipScopesByNode[current] = ownershipScopesForNode;
                }
                ownershipScopesForNode.UnionWith(ownershipScopes);

                foreach (HeroAuthoringGraphEdge edge in outgoing.GetValueOrDefault(current, []))
                {
                    queue.Enqueue(edge.Target);
                }
            }
        }

        return usagesByNode.ToDictionary(
            pair => pair.Key,
            pair => new HeroAuthoringSourceUsageSummary(
                pair.Value.Count,
                ownershipScopesByNode.GetValueOrDefault(pair.Key)?.Count ?? 0),
            StringComparer.Ordinal);
    }

    private static SourceObjectDescriptor[] BuildSourceObjectDescriptors(
        GameDataCatalog catalog,
        HeroAuthoringGraph graph,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        List<string> warnings)
    {
        var descriptors = new List<SourceObjectDescriptor>();
        foreach ((string sourceType, string sourceTypeLabel, string? tableKey) in SourceTypes)
        {
            if (tableKey is null) continue;
            GameDataTable? table = FindTable(catalog, tableKey);
            if (table is null) continue;

            foreach (GameDataRecord sourceObject in table.Records)
            {
                var roots = new Dictionary<string, RootAccumulator>(StringComparer.Ordinal);
                AddSourceRoots(
                    catalog,
                    graph,
                    nodes,
                    incoming,
                    roots,
                    warnings,
                    sourceType,
                    sourceObject);
                string[] rootKeys = roots.Values
                    .Select(root => $"{root.Namespace}:{root.LegacyId}")
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                descriptors.Add(new SourceObjectDescriptor(
                    sourceType,
                    sourceTypeLabel,
                    $"{sourceType}:{sourceObject.Id}",
                    sourceObject.Id,
                    Title(table, sourceObject),
                    rootKeys));
            }
        }

        return descriptors.ToArray();
    }

    private static HeroAuthoringOwnershipScope ResolveOwnershipScope(
        GameDataCatalog catalog,
        string scopeKey)
    {
        int separator = scopeKey.IndexOf(':');
        string scopeType = separator > 0 ? scopeKey[..separator] : scopeKey;
        int scopeId = separator > 0 ? IdFromKey(scopeKey) : 0;
        (string Key, string Label, string? TableKey) sourceType = SourceTypes.FirstOrDefault(
            candidate => string.Equals(candidate.Key, scopeType, StringComparison.Ordinal));
        string scopeTypeLabel = string.IsNullOrWhiteSpace(sourceType.Label)
            ? scopeType
            : sourceType.Label;
        if (sourceType.TableKey is not null
            && FindTable(catalog, sourceType.TableKey) is { } table
            && table.Records.FirstOrDefault(record => record.Id == scopeId) is { } record)
        {
            return new HeroAuthoringOwnershipScope(
                scopeKey,
                scopeType,
                scopeTypeLabel,
                scopeId,
                Title(table, record));
        }

        return new HeroAuthoringOwnershipScope(
            scopeKey,
            scopeType,
            scopeTypeLabel,
            scopeId,
            scopeKey);
    }

    private static HashSet<string> UpstreamNodeKeys(
        string targetKey,
        IReadOnlyList<HeroAuthoringGraphEdge> edges)
    {
        IReadOnlyDictionary<string, HeroAuthoringGraphEdge[]> incoming = edges
            .GroupBy(edge => edge.Target, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var ancestors = new HashSet<string>(StringComparer.Ordinal) { targetKey };
        var queue = new Queue<string>();
        queue.Enqueue(targetKey);
        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            foreach (HeroAuthoringGraphEdge edge in incoming.GetValueOrDefault(current, []))
            {
                if (ancestors.Add(edge.Source)) queue.Enqueue(edge.Source);
            }
        }

        return ancestors;
    }

    private static void AddSourceRoots(
        GameDataCatalog catalog,
        HeroAuthoringGraph graph,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        IDictionary<string, RootAccumulator> roots,
        List<string> warnings,
        string sourceType,
        GameDataRecord source)
    {
        switch (sourceType)
        {
            case "hero":
                AddDirectSkills(roots, nodes, incoming, source, ["normal_skills"], "base", "基础技能");
                AddDirectSkills(roots, nodes, incoming, source, ["active_skills"], "active", "主动技能字段");
                AddGraphSkills(graph, roots, nodes, incoming, $"TbHero:{source.Id}", "upgradeSkill", "升级解锁技能");
                AddGraphSkills(graph, roots, nodes, incoming, $"TbHero:{source.Id}", "equipmentSkill", "装备授予技能");
                AddShopSkills(catalog, roots, nodes, incoming, "battle-hero-shop", "shop", "商店解锁技能");
                break;
            case "soldier":
                AddDirectSkills(roots, nodes, incoming, source, ["skills"], "base", "基础技能");
                AddSoldierUpgradeSkills(catalog, graph, roots, nodes, incoming, source, "soldier-upgrade", "upgrade", "升级解锁技能");
                AddSoldierLevelUpSkills(catalog, roots, nodes, incoming, source, "shop", "战斗升级解锁技能");
                break;
            case "building":
                AddDirectSkills(roots, nodes, incoming, source, ["skills"], "base", "建筑技能");
                AddDirectSkills(roots, nodes, incoming, source, ["productSkill"], "product", "生产技能");
                break;
            case "equipment":
                AddDirectSkills(roots, nodes, incoming, source, ["skills"], "base", "装备授予技能");
                AddEquipmentUpgradeSkills(catalog, graph, roots, nodes, incoming, source, "upgrade", "升级解锁技能");
                break;
            case "trap":
                AddDirectSkills(roots, nodes, incoming, source, ["skills"], "trapSkill", "机关技能");
                AddDirectBuffs(roots, nodes, incoming, source, ["buffs"], "trapBuff", "机关 Buff");
                break;
            case "item":
                AddDirectGroups(
                    roots,
                    nodes,
                    incoming,
                    source,
                    ["effect_group_id", "effectGroupId", "EffectGroupId"],
                    "effect",
                    "直接执行效果组");
                break;
        }

        if (HasCardReferences(catalog))
        {
            warnings.Add("HeroUpgrade 或 Item 链中存在 Card 分支；第一阶段暂不纳入 Card 根来源。");
        }
    }

    private static void AddDirectSkills(
        IDictionary<string, RootAccumulator> roots,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        GameDataRecord record,
        IReadOnlyList<string> fields,
        string category,
        string categoryLabel)
    {
        foreach (string field in fields)
        {
            foreach (int id in Ids(record, field))
            {
                AddRoot(roots, nodes, incoming, category, categoryLabel, "TbSkill", id);
            }
        }
    }

    private static void AddDirectGroups(
        IDictionary<string, RootAccumulator> roots,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        GameDataRecord record,
        IReadOnlyList<string> fields,
        string category,
        string categoryLabel)
    {
        foreach (string field in fields)
        {
            foreach (int id in Ids(record, field))
            {
                AddRoot(roots, nodes, incoming, category, categoryLabel, "EffectGroup", id);
            }
        }
    }

    private static void AddDirectBuffs(
        IDictionary<string, RootAccumulator> roots,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        GameDataRecord record,
        IReadOnlyList<string> fields,
        string category,
        string categoryLabel)
    {
        foreach (string field in fields)
        {
            foreach (int id in Ids(record, field))
            {
                AddRoot(roots, nodes, incoming, category, categoryLabel, "TbBuff", id);
            }
        }
    }

    private static void AddGraphSkills(
        HeroAuthoringGraph graph,
        IDictionary<string, RootAccumulator> roots,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        string sourceKey,
        string role,
        string categoryLabel)
    {
        foreach (HeroAuthoringGraphEdge edge in graph.Edges.Where(edge =>
                     edge.Source == sourceKey
                     && edge.Role == role
                     && edge.Target.StartsWith("TbSkill:", StringComparison.Ordinal)))
        {
            AddRoot(roots, nodes, incoming, role, categoryLabel, "TbSkill", IdFromKey(edge.Target));
        }
    }

    private static void AddShopSkills(
        GameDataCatalog catalog,
        IDictionary<string, RootAccumulator> roots,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        string tableKey,
        string category,
        string categoryLabel)
    {
        GameDataTable? table = FindTable(catalog, tableKey);
        if (table is null) return;
        foreach (GameDataRecord record in table.Records)
        {
            foreach (int id in Ids(record, "UnlockSkillId"))
            {
                AddRoot(roots, nodes, incoming, category, categoryLabel, "TbSkill", id);
            }
        }
    }

    private static void AddSoldierLevelUpSkills(
        GameDataCatalog catalog,
        IDictionary<string, RootAccumulator> roots,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        GameDataRecord soldier,
        string category,
        string categoryLabel)
    {
        GameDataTable? table = FindTable(catalog, "battle-soldier-level-up");
        if (table is null) return;
        foreach (GameDataRecord record in table.Records.Where(record => FirstId(record, "GroupId") == soldier.Id))
        {
            foreach (int id in Ids(record, "UnlockSkillId"))
            {
                AddRoot(roots, nodes, incoming, category, categoryLabel, "TbSkill", id);
            }
        }
    }

    private static void AddSoldierUpgradeSkills(
        GameDataCatalog catalog,
        HeroAuthoringGraph graph,
        IDictionary<string, RootAccumulator> roots,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        GameDataRecord soldier,
        string tableKey,
        string category,
        string categoryLabel)
    {
        GameDataTable? table = FindTable(catalog, tableKey);
        if (table is null) return;
        foreach (int itemId in table.Records
                     .Where(record => FirstId(record, "GroupId") == soldier.Id)
                     .Select(record => FirstId(record, "UnlockItem"))
                     .Where(id => id > 0)
                     .Distinct())
        {
            AddUnlockItemSkills(graph, roots, nodes, incoming, itemId, category, categoryLabel);
        }
    }

    private static void AddEquipmentUpgradeSkills(
        GameDataCatalog catalog,
        HeroAuthoringGraph graph,
        IDictionary<string, RootAccumulator> roots,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        GameDataRecord equipment,
        string category,
        string categoryLabel)
    {
        GameDataTable? table = FindTable(catalog, "equipment-upgrade");
        if (table is null) return;
        string slot = FirstValue(equipment, "slot") ?? string.Empty;
        foreach (int itemId in table.Records
                     .Where(record => string.Equals(FirstValue(record, "slot") ?? string.Empty, slot, StringComparison.Ordinal))
                     .Select(record => FirstId(record, "UnlockItem"))
                     .Where(id => id > 0)
                     .Distinct())
        {
            AddUnlockItemSkills(graph, roots, nodes, incoming, itemId, category, categoryLabel);
        }
    }

    private static void AddUnlockItemSkills(
        HeroAuthoringGraph graph,
        IDictionary<string, RootAccumulator> roots,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        int itemId,
        string category,
        string categoryLabel)
    {
        string itemKey = $"TbItem:{itemId}";
        HeroAuthoringGraphEdge[] groupEdges = graph.Edges.Where(edge =>
            edge.Source == itemKey
            && edge.Target.StartsWith("EffectGroup:", StringComparison.Ordinal)).ToArray();
        HashSet<string> groupKeys = groupEdges.Select(edge => edge.Target).ToHashSet(StringComparer.Ordinal);
        HashSet<string> effectKeys = graph.Edges
            .Where(edge => groupKeys.Contains(edge.Source) && edge.Role == "orderedEffect")
            .Select(edge => edge.Target)
            .ToHashSet(StringComparer.Ordinal);
        foreach (HeroAuthoringGraphEdge edge in graph.Edges.Where(edge =>
                     effectKeys.Contains(edge.Source)
                     && edge.Target.StartsWith("TbSkill:", StringComparison.Ordinal)
                     && IsAddSkillEffect(nodes.GetValueOrDefault(edge.Source))))
        {
            AddRoot(roots, nodes, incoming, category, categoryLabel, "TbSkill", IdFromKey(edge.Target));
        }
    }

    private static bool IsAddSkillEffect(HeroAuthoringGraphNode? effect)
    {
        if (effect is null) return false;
        IReadOnlyList<string> actionValues = effect.Fields.GetValueOrDefault("action_type", []);
        IReadOnlyList<string> labelValues = effect.Fields.GetValueOrDefault("__action", []);
        string action = actionValues.Count > 0 ? actionValues[0] : string.Empty;
        string label = labelValues.Count > 0 ? labelValues[0] : string.Empty;
        return action is "37" or "AddSkill" or "添加技能"
               || label is "添加技能" or "AddSkill";
    }

    private static IEnumerable<HeroAuthoringGraphNode> UnreferencedRoots(
        HeroAuthoringGraph graph,
        IReadOnlyDictionary<string, int> incoming)
    {
        HashSet<string> groupsWithMembers = graph.Edges
            .Where(edge => edge.Role == "orderedEffect" && !edge.Derived)
            .Select(edge => edge.Source)
            .ToHashSet(StringComparer.Ordinal);
        return graph.Nodes.Where(node =>
            incoming.GetValueOrDefault(node.Key) == 0
            && (node.Namespace == "TbSkill"
                || node.Namespace == "EffectGroup" && groupsWithMembers.Contains(node.Key)));
    }

    private static Dictionary<string, int> RealIncomingReferences(HeroAuthoringGraph graph) =>
        graph.Edges
            .Where(edge => !edge.Derived
                           && !edge.Source.StartsWith("SkillConditionGate:", StringComparison.Ordinal)
                           && !(edge.Source.StartsWith("EffectGroup:", StringComparison.Ordinal)
                                && edge.Role == "orderedEffect")
                           && !(edge.Source.StartsWith("ConditionGroup:", StringComparison.Ordinal)
                                && edge.Role == "orderedCondition"))
            .GroupBy(edge => edge.Target, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static void AddRoot(
        IDictionary<string, RootAccumulator> roots,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        string category,
        string categoryLabel,
        string nodeNamespace,
        int id)
    {
        if (id <= 0) return;
        string key = $"{category}:{nodeNamespace}:{id}";
        if (roots.ContainsKey(key)) return;
        if (!nodes.ContainsKey($"{nodeNamespace}:{id}")) return;
        roots[key] = new RootAccumulator(
            nodeNamespace,
            id,
            category,
            categoryLabel,
            incoming.GetValueOrDefault($"{nodeNamespace}:{id}"));
    }

    private static HeroAuthoringBehaviorRoot BuildRoot(
        HeroAuthoringGraph graph,
        IReadOnlyDictionary<string, HeroAuthoringGraphNode> nodes,
        IReadOnlyDictionary<string, int> incoming,
        RootAccumulator root)
    {
        HeroAuthoringGraphNode node = nodes[$"{root.Namespace}:{root.LegacyId}"];
        string summary = root.Namespace switch
        {
            "EffectGroup" => $"{graph.Edges.Count(edge => edge.Source == node.Key && edge.Role == "orderedEffect")} 个效果",
            "TbBuff" => BuffRootSummary(graph, node),
            _ => node.Label,
        };
        if (root.IncomingReferenceCount > 0)
        {
            summary += $" · {root.IncomingReferenceCount} 处引用";
        }
        else
        {
            summary += " · 未引用";
        }

        return new HeroAuthoringBehaviorRoot(
            node.Key,
            node.Namespace,
            node.LegacyId,
            node.Label,
            summary,
            root.Category,
            root.CategoryLabel,
            incoming.GetValueOrDefault(node.Key),
            incoming.GetValueOrDefault(node.Key) == 0);
    }

    private static (string Key, string Label, string? TableKey) ResolveSourceType(string sourceType)
    {
        string normalized = sourceType?.Trim().ToLowerInvariant() ?? string.Empty;
        (string Key, string Label, string? TableKey) match = SourceTypes.FirstOrDefault(
            source => string.Equals(source.Key, normalized, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(match.Key))
        {
            throw new ArgumentException($"未知来源类型 {sourceType}。", nameof(sourceType));
        }
        return match;
    }

    private GameDataCatalog RequireCatalog() =>
        store.ReadGameDataCatalog()
        ?? throw new InvalidOperationException("请先选择并打开 Unity 工程。");

    private static GameDataTable RequireTable(GameDataCatalog catalog, string tableKey) =>
        FindTable(catalog, tableKey)
        ?? throw new InvalidOperationException($"当前工作副本缺少来源数据表 {tableKey}。");

    private static GameDataTable? FindTable(GameDataCatalog catalog, string tableKey) =>
        catalog.Tables.FirstOrDefault(table =>
            string.Equals(table.Key, tableKey, StringComparison.OrdinalIgnoreCase));

    private static string Title(GameDataTable table, GameDataRecord record)
    {
        foreach (string field in new[] { "name", "Name", "__remark_2", "remark", "Remark", "desc", "Desc" })
        {
            string? value = FirstValue(record, field);
            if (!string.IsNullOrWhiteSpace(value) && value != "0") return value;
        }
        return $"{table.DisplayName} {record.Id}";
    }

    private static string ObjectSummary(GameDataTable table, GameDataRecord record)
    {
        int skillCount = Ids(record, "normal_skills")
            .Concat(Ids(record, "active_skills"))
            .Concat(Ids(record, "skills"))
            .Concat(Ids(record, "Skill1", "Skill2", "Skill3"))
            .Distinct()
            .Count();
        int buffCount = Ids(record, "buffs").Distinct().Count();
        if (table.Key.Equals("trap", StringComparison.OrdinalIgnoreCase)
            && (skillCount > 0 || buffCount > 0))
        {
            return $"{skillCount} 个技能 · {buffCount} 个 Buff";
        }
        if (skillCount > 0) return $"{skillCount} 个直接技能";
        int groupCount = Ids(record, "effect_group_id", "effectGroupId", "EffectGroupId").Distinct().Count();
        if (groupCount > 0) return $"{groupCount} 个直接效果组";
        return table.DisplayName;
    }

    private static int[] Ids(GameDataRecord record, params string[] fields) =>
        fields
            .SelectMany(field => Values(record, field))
            .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

    private static int FirstId(GameDataRecord record, string field)
    {
        int[] ids = Ids(record, field);
        return ids.Length > 0 ? ids[0] : 0;
    }

    private static string? FirstValue(GameDataRecord record, string field) =>
        Values(record, field).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static IReadOnlyList<string> Values(GameDataRecord record, string field) =>
        record.Fields.FirstOrDefault(pair =>
            string.Equals(pair.Key, field, StringComparison.OrdinalIgnoreCase)).Value ?? [];

    private static int IdFromKey(string key) =>
        int.TryParse(key[(key.IndexOf(':') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            ? id
            : 0;

    private static int CategoryOrder(string category) => category switch
    {
        "trapSkill" => 0,
        "trapBuff" => 1,
        "base" => 0,
        "active" => 1,
        "upgrade" => 2,
        "equipment" => 3,
        "shop" => 4,
        "product" => 5,
        "effect" => 6,
        "default" => 7,
        "unreferenced" => 8,
        _ => 99,
    };

    private static string BuffRootSummary(
        HeroAuthoringGraph graph,
        HeroAuthoringGraphNode node)
    {
        int effectCount = graph.Edges.Count(edge =>
            edge.Source == node.Key
            && edge.Target.StartsWith("EffectGroup:", StringComparison.Ordinal)
            && edge.Role is "enterEffect" or "intervalEffect" or "finishEffect");
        return effectCount > 0 ? $"{effectCount} 个生命周期效果" : "无生命周期效果";
    }

    private static bool HasCardReferences(GameDataCatalog catalog) =>
        catalog.Tables.Any(table =>
            table.Key is "hero-upgrade" or "soldier-upgrade" or "item"
            && table.Records.Any(record =>
                record.Fields.Any(pair =>
                    pair.Key.Contains("card", StringComparison.OrdinalIgnoreCase)
                    && pair.Value.Any(value => value is not ("" or "0")))));

    private static bool IsOwnershipTraversalEdge(HeroAuthoringGraphEdge edge) =>
        !edge.Derived
        && !edge.Source.StartsWith("SkillConditionGate:", StringComparison.Ordinal);

    private sealed record SourceObjectDescriptor(
        string SourceType,
        string SourceTypeLabel,
        string SourceKey,
        int SourceObjectId,
        string SourceObjectLabel,
        IReadOnlyList<string> RootKeys);

    private sealed class OwnershipResolver
    {
        private readonly GameDataCatalog _catalog;
        private readonly Dictionary<string, HeroAuthoringGraphEdge[]> _incomingByNode;
        private readonly Dictionary<string, HeroAuthoringGraphEdge[]> _outgoingByNode;
        private readonly Dictionary<string, SourceObjectDescriptor[]> _sourceObjectsByRoot;
        private readonly Dictionary<string, string[]> _sourceScopeCache = new(StringComparer.Ordinal);
        private readonly HashSet<string> _resolvingSources = new(StringComparer.Ordinal);
        private readonly HashSet<string> _resolvingNodes = new(StringComparer.Ordinal);

        public OwnershipResolver(
            GameDataCatalog catalog,
            HeroAuthoringGraph graph,
            IReadOnlyList<SourceObjectDescriptor> sourceObjects)
        {
            _catalog = catalog;
            _incomingByNode = graph.Edges
                .Where(IsOwnershipTraversalEdge)
                .GroupBy(edge => edge.Target, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToArray(),
                    StringComparer.Ordinal);
            _outgoingByNode = graph.Edges
                .Where(IsOwnershipTraversalEdge)
                .GroupBy(edge => edge.Source, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToArray(),
                    StringComparer.Ordinal);
            _sourceObjectsByRoot = sourceObjects
                .SelectMany(source => source.RootKeys.Select(rootKey => (rootKey, source)))
                .GroupBy(item => item.rootKey, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.source).ToArray(),
                    StringComparer.Ordinal);
        }

        public string[] Resolve(SourceObjectDescriptor source)
        {
            if (_sourceScopeCache.TryGetValue(source.SourceKey, out string[]? cached))
            {
                return cached;
            }
            if (!_resolvingSources.Add(source.SourceKey))
            {
                return [];
            }

            string[] owners;
            try
            {
                owners = source.SourceType switch
                {
                    "hero" or "soldier" or "building" or "equipment" =>
                        [source.SourceKey],
                    "item" => ResolveItemOwners(source),
                    "trap" => ResolveDependentOwners(source),
                    _ => [source.SourceKey],
                };
                if (owners.Length == 0)
                {
                    owners = [source.SourceKey];
                }
            }
            finally
            {
                _resolvingSources.Remove(source.SourceKey);
            }

            _sourceScopeCache[source.SourceKey] = owners;
            return owners;
        }

        private string[] ResolveItemOwners(SourceObjectDescriptor source)
        {
            var owners = new SortedSet<string>(StringComparer.Ordinal);
            if (FindTable(_catalog, "hero-upgrade") is { } heroUpgrades)
            {
                foreach (GameDataRecord upgrade in heroUpgrades.Records.Where(record =>
                             FirstId(record, "UnlockItem") == source.SourceObjectId))
                {
                    int heroId = FirstId(upgrade, "group_id");
                    if (heroId > 0) owners.Add($"hero:{heroId}");
                }
            }

            if (FindTable(_catalog, "soldier-upgrade") is { } soldierUpgrades)
            {
                foreach (GameDataRecord upgrade in soldierUpgrades.Records.Where(record =>
                             FirstId(record, "UnlockItem") == source.SourceObjectId))
                {
                    int soldierId = FirstId(upgrade, "GroupId");
                    if (soldierId > 0) owners.Add($"soldier:{soldierId}");
                }
            }

            if (FindTable(_catalog, "equipment-upgrade") is { } equipmentUpgrades
                && FindTable(_catalog, "equipment") is { } equipment)
            {
                foreach (GameDataRecord upgrade in equipmentUpgrades.Records.Where(record =>
                             FirstId(record, "UnlockItem") == source.SourceObjectId))
                {
                    string? slot = FirstValue(upgrade, "slot");
                    if (string.IsNullOrWhiteSpace(slot)) continue;
                    foreach (GameDataRecord candidate in equipment.Records.Where(record =>
                                 string.Equals(FirstValue(record, "slot"), slot, StringComparison.Ordinal)))
                    {
                        owners.Add($"equipment:{candidate.Id}");
                    }
                }
            }

            return owners.Count > 0 ? owners.ToArray() : ResolveReferencedOwners(source);
        }

        private string[] ResolveReferencedOwners(SourceObjectDescriptor source)
        {
            var owners = new SortedSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(source.RootKeys);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (!visited.Add(current)) continue;

                if (_sourceObjectsByRoot.TryGetValue(current, out SourceObjectDescriptor[]? sources))
                {
                    foreach (SourceObjectDescriptor candidate in sources.Where(candidate =>
                                 !string.Equals(candidate.SourceKey, source.SourceKey, StringComparison.Ordinal)))
                    {
                        foreach (string owner in Resolve(candidate))
                        {
                            if (IsPrimaryOwnerSourceType(candidate.SourceType)
                                || !string.Equals(owner, candidate.SourceKey, StringComparison.Ordinal))
                            {
                                owners.Add(owner);
                            }
                        }
                    }
                }

                foreach (HeroAuthoringGraphEdge edge in _outgoingByNode.GetValueOrDefault(current, []))
                {
                    queue.Enqueue(edge.Target);
                }
            }

            return owners.ToArray();
        }

        private static bool IsPrimaryOwnerSourceType(string sourceType) =>
            sourceType is "hero" or "soldier" or "building" or "equipment";

        private string[] ResolveDependentOwners(SourceObjectDescriptor source)
        {
            // Dependent assets inherit every owner reachable through their incoming runtime paths.
            string sourceNodeKey = source.SourceType switch
            {
                "trap" => $"TbTrap:{source.SourceObjectId}",
                _ => $"{source.SourceType}:{source.SourceObjectId}",
            };
            var owners = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string parentKey in _incomingByNode.GetValueOrDefault(sourceNodeKey, [])
                         .Select(edge => edge.Source)
                         .Distinct(StringComparer.Ordinal))
            {
                owners.UnionWith(ResolveNodeOwners(parentKey));
            }

            return owners.ToArray();
        }

        private string[] ResolveNodeOwners(string nodeKey)
        {
            if (!_resolvingNodes.Add(nodeKey))
            {
                return [];
            }

            var owners = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                if (_sourceObjectsByRoot.TryGetValue(nodeKey, out SourceObjectDescriptor[]? sources))
                {
                    foreach (SourceObjectDescriptor source in sources)
                    {
                        owners.UnionWith(Resolve(source));
                    }
                }

                foreach (HeroAuthoringGraphEdge edge in _incomingByNode.GetValueOrDefault(nodeKey, []))
                {
                    owners.UnionWith(ResolveNodeOwners(edge.Source));
                }
            }
            finally
            {
                _resolvingNodes.Remove(nodeKey);
            }

            return owners.ToArray();
        }
    }

    private sealed record RootAccumulator(
        string Namespace,
        int LegacyId,
        string Category,
        string CategoryLabel,
        int IncomingReferenceCount);
}
