using System.Globalization;
using TianshuDM.Application.GameData;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.HeroAuthoring;

namespace TianshuDM.Application.HeroAuthoring;

public sealed record HeroAuthoringHeroSummary(
    int Id,
    string Name,
    int BaseSkillCount,
    bool HasRuntimeUnusedActiveSkills);

public sealed record HeroAuthoringNodeSummary(
    string Key,
    string Namespace,
    int LegacyId,
    string Label,
    string Summary,
    string Kind,
    string TableKey);

public sealed class HeroAuthoringGraphService(
    IGameDataDraftStore store,
    HeroAuthoringGraphProjector projector)
{
    public IReadOnlyList<HeroAuthoringHeroSummary> ListHeroes()
    {
        GameDataCatalog catalog = RequireCatalog();
        GameDataTable heroes = catalog.Table("hero");
        return heroes.Records
            .Select(
                hero => new HeroAuthoringHeroSummary(
                    hero.Id,
                    Title(hero),
                    Values(hero, "normal_skills").Count(ValueIsConfigured),
                    Values(hero, "active_skills").Any(ValueIsConfigured)))
            .OrderBy(hero => hero.Id)
            .ToArray();
    }

    public HeroAuthoringGraph GetGraph(string focusType, int focusId, int depth = 1) =>
        projector.Project(RequireCatalog(), focusType, focusId, depth);

    public HeroAuthoringGraph GetSkillBehavior(int skillId, int depth = 12) =>
        GetBehavior("TbSkill", skillId, depth);

    public HeroAuthoringGraph GetBehavior(string rootNamespace, int rootId, int depth = 12) =>
        projector.ProjectBehavior(RequireCatalog(), rootNamespace, rootId, depth);

    public string CurrentRevision() => HeroAuthoringCatalogRevision.Compute(RequireCatalog());

    public IReadOnlyList<HeroAuthoringNodeSummary> SearchNodes(
        string? query,
        string? nodeNamespace = null,
        int limit = 50)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "搜索结果数量必须在 1 到 200 之间。");
        }

        string term = query?.Trim() ?? string.Empty;
        GameDataCatalog catalog = RequireCatalog();
        IEnumerable<HeroAuthoringNodeSummary> nodes = catalog.Tables
            .Where(table => HeroAuthoringGraphProjector.TryGetNamespace(table.Key, out _))
            .SelectMany(
                table =>
                {
                    _ = HeroAuthoringGraphProjector.TryGetNamespace(table.Key, out string resolvedNamespace);
                    return table.Records.Select(
                        record =>
                        {
                            string label = Title(table, record);
                            return new HeroAuthoringNodeSummary(
                                $"{resolvedNamespace}:{record.Id}",
                                resolvedNamespace,
                                record.Id,
                                label,
                                BuildSummary(table, record, label),
                                table.DisplayName,
                                table.Key);
                        });
                })
            .Concat(VirtualGroupNodes(catalog));
        return nodes
            .Where(node => string.IsNullOrWhiteSpace(nodeNamespace)
                           || string.Equals(node.Namespace, nodeNamespace, StringComparison.OrdinalIgnoreCase))
            .Where(node => term.Length == 0
                           || node.LegacyId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                               .Contains(term, StringComparison.OrdinalIgnoreCase)
                           || node.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || node.Summary.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.Namespace, StringComparer.Ordinal)
            .ThenBy(node => node.LegacyId)
            .Take(limit)
            .ToArray();
    }

    private static IEnumerable<HeroAuthoringNodeSummary> VirtualGroupNodes(GameDataCatalog catalog)
    {
        foreach ((string tableKey, string nodeNamespace, string label) in new[]
                 {
                     ("effect", "EffectGroup", "效果组"),
                     ("condition", "ConditionGroup", "条件组"),
                 })
        {
            GameDataTable? table = catalog.Tables.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, tableKey, StringComparison.OrdinalIgnoreCase));
            if (table is null) continue;
            foreach (int groupId in table.Records
                         .Select(record => Values(record, "group_id") is { Count: > 0 } values ? values[0] : null)
                         .Select(value => int.TryParse(value, out int parsed) ? parsed : 0)
                         .Where(id => id > 0)
                         .Distinct()
                         .Order())
            {
                string groupValue = groupId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                GameDataRecord[] members = table.Records
                    .Where(record => Values(record, "group_id").Contains(groupValue))
                    .OrderBy(record => record.Id)
                    .ToArray();
                string memberLabel = tableKey == "effect" ? "效果" : "条件";
                string[] memberSummaries = members
                    .Select(record => BuildSummary(table, record, Title(table, record)))
                    .Where(ValueIsConfigured)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                string preview = string.Join('、', memberSummaries.Take(3));
                string summary = $"{members.Length} 个{memberLabel}";
                if (preview.Length > 0)
                {
                    summary += $"：{preview}{(memberSummaries.Length > 3 ? "等" : string.Empty)}";
                }
                yield return new HeroAuthoringNodeSummary(
                    $"{nodeNamespace}:{groupId}",
                    nodeNamespace,
                    groupId,
                    $"{label} {groupId}",
                    summary,
                    label,
                    tableKey);
            }
        }
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

    private static string Title(GameDataRecord record)
    {
        foreach (string field in new[] { "name", "Name", "__remark_2", "remark", "desc", "Desc" })
        {
            string? value = Values(record, field).FirstOrDefault(ValueIsConfigured);
            if (value is not null) return value;
        }
        return $"英雄 {record.Id}";
    }

    private static string Title(GameDataTable table, GameDataRecord record)
    {
        foreach (string field in new[] { "name", "Name", "__remark_2", "remark", "desc", "Desc" })
        {
            string? value = Values(record, field).FirstOrDefault(ValueIsConfigured);
            if (value is not null) return value;
        }
        return $"{table.DisplayName} {record.Id}";
    }

    internal static string BuildSummary(GameDataTable table, GameDataRecord record, string label)
    {
        string tableKey = table.Key.ToLowerInvariant();
        if (tableKey == "condition")
        {
            return Values(record, "action_type").FirstOrDefault(ValueIsConfigured) ?? "未配置条件类型";
        }

        if (tableKey == "effect")
        {
            return Values(record, "action_type").FirstOrDefault(ValueIsConfigured) ?? "未配置效果类型";
        }

        if (tableKey == "buff")
        {
            return JoinSummary(
                label,
                FirstValue(record, "status_effect_type", "buff_type", "type"),
                MillisecondsSummary(FirstValue(record, "duration"), "持续"),
                PositiveIntegerSummary(FirstValue(record, "max_layer"), "最多", "层"));
        }

        if (tableKey == "bullet")
        {
            return JoinSummary(
                label,
                ScaledSummary(FirstValue(record, "hor_speed"), 10000, "速度"),
                MillisecondsSummary(FirstValue(record, "life"), "生存"),
                PositiveIntegerSummary(FirstValue(record, "effect_group_id"), "命中效果组", string.Empty));
        }

        if (tableKey == "search")
        {
            return JoinSummary(
                label,
                FirstValue(record, "shape_type", "shape"),
                FirstValue(record, "team", "team_type"),
                FirstValue(record, "type", "unit_type", "target_type"),
                PositiveIntegerSummary(FirstValue(record, "count", "max_count"), "最多", "个"),
                PrioritySummary(record));
        }

        if (tableKey == "damage-pipeline")
        {
            string[] stages = Values(record, "PipelineStage_param")
                .Concat(Values(record, "stages"))
                .Concat(Values(record, "stage"))
                .Concat(Values(record, "pipeline"))
                .Where(ValueIsConfigured)
                .ToArray();
            return stages.Length > 0 ? string.Join(" → ", stages) : label;
        }

        if (tableKey == "skill-resource")
        {
            return JoinSummary(
                label,
                PrefixedValue(FirstValue(record, "resource", "resource_id"), "资源"),
                PrefixedValue(FirstValue(record, "attach_point"), "挂点"));
        }

        return label;
    }

    private static string JoinSummary(string fallback, params string?[] parts)
    {
        string[] configured = parts
            .Where(part => ValueIsConfigured(part ?? string.Empty))
            .Select(part => part!)
            .ToArray();
        return configured.Length > 0 ? string.Join(" · ", configured) : fallback;
    }

    private static string? FirstValue(GameDataRecord record, params string[] keys) =>
        keys.SelectMany(key => Values(record, key)).FirstOrDefault(ValueIsConfigured);

    private static string? PrioritySummary(GameDataRecord record)
    {
        string[] values = Values(record, "prioritys")
            .Concat(Values(record, "priority"))
            .Concat(Values(record, "sort_type"))
            .Where(ValueIsConfigured)
            .ToArray();
        return values.Length > 0 ? string.Join(" → ", values) : null;
    }

    private static string? MillisecondsSummary(string? rawValue, string label) =>
        decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) && value > 0
            ? $"{label} {(value / 1000m).ToString("0.###", CultureInfo.InvariantCulture)} 秒"
            : null;

    private static string? ScaledSummary(string? rawValue, int scale, string label) =>
        decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) && value > 0
            ? $"{label} {(value / scale).ToString("0.###", CultureInfo.InvariantCulture)}"
            : null;

    private static string? PositiveIntegerSummary(string? rawValue, string prefix, string suffix) =>
        int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0
            ? $"{prefix} {value}{(suffix.Length > 0 ? $" {suffix}" : string.Empty)}"
            : null;

    private static string? PrefixedValue(string? value, string prefix) =>
        ValueIsConfigured(value ?? string.Empty) ? $"{prefix} {value}" : null;

    private static IReadOnlyList<string> Values(GameDataRecord record, string key) =>
        record.Fields.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value ?? [];

    private static bool ValueIsConfigured(string value) =>
        !string.IsNullOrWhiteSpace(value) && value != "0";
}
