using System.Security.Cryptography;
using System.Text;
using RtsSkillStudio.Agent.Patch;
using RtsSkillStudio.Agent.Workspaces;
using TianshuDM.Application.HeroAuthoring;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.HeroAuthoring;
using TianshuDM.Infrastructure.Excel;
using TianshuDM.Infrastructure.Excel.HeroAuthoring;

namespace RtsSkillStudio.Api.Workspaces;

public sealed class SkillWorkspaceService(
    SkillWorkspaceOptions options,
    IHostEnvironment environment,
    ExecutionChainProjectionPolicy executionProjectionPolicy,
    ILogger<SkillWorkspaceService> logger
)
{
    private const int MaxMentionCandidatesPerNamespace = 5;

    private static readonly HashSet<string> GenericMentionTokens =
    [
        "自己",
        "介绍",
        "一下",
        "这个",
        "那个",
        "技能",
        "物品",
        "道具",
        "效果",
        "伤害",
        "条件",
        "属性",
        "目标",
        "搜索",
        "修改",
        "调整",
        "设置"
    ];

    private static readonly (string TableKey, string Namespace)[] SearchRoots =
    [
        // Priority 1: current skill-authoring roots.
        ("skill", "TbSkill"),
        ("effect", "TbEffect"),
        ("item", "TbItem"),
        ("buff", "TbBuff"),
        ("bullet", "TbBullet"),
        ("search", "TbSearch"),
        ("trap", "TbTrap"),
        ("equipment", "TbEquipment"),
        // Priority 2: direct behavior dependencies.
        ("condition", "TbCondition"),
        ("damage-pipeline", "TbDamagePipeline"),
        ("skill-resource", "TbSkillResource"),
        ("resource", "TbResource"),
        // Priority 3: remaining Agent-associated authoring tables.
        ("hero", "TbHero"),
        ("hero-skin", "TbHeroSkin"),
        ("hero-upgrade", "TbHeroUpgrade"),
        ("hero-skill-description", "TbHeroSkillDes"),
        ("equipment-upgrade", "TbEquipmentUpgrade"),
        ("random-bag", "TbRandomBag"),
        ("random-set", "TbRandomSet"),
        ("random-card", "TbRandomCard"),
        ("view-function-component", "TbViewFunctionComponent"),
        ("battle-hero-shop", "TbBattleHeroShop"),
        ("battle-soldier-level-up", "TbBattleSoldierLevelUp"),
        ("soldier-upgrade", "TbSoldierUpgrade"),
        ("card", "TbCard"),
        ("soldier", "TbSoldier"),
        ("building", "TbBuilding"),
        ("block", "TbBlock")
    ];

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private WorkspaceSnapshot? _snapshot;

    public void InvalidateSnapshot()
    {
        _loadGate.Wait();
        try
        {
            _snapshot = null;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task<SkillWorkspaceStatus> GetStatusAsync(
        CancellationToken cancellationToken
    )
    {
        var errors = new List<string>();
        bool rootExists = Directory.Exists(options.ExcelDataRoot);
        bool schemaExists = File.Exists(options.HeroAuthoringSchemaPath);

        if (!rootExists)
        {
            errors.Add($"Excel 数据目录不存在：{options.ExcelDataRoot}");
        }

        if (!schemaExists)
        {
            errors.Add($"语义 Schema 不存在：{options.HeroAuthoringSchemaPath}");
        }

        if (errors.Count > 0)
        {
            return new SkillWorkspaceStatus(
                false,
                options.WorkspaceId,
                options.ExcelDataRoot,
                options.HeroAuthoringSchemaPath,
                rootExists,
                schemaExists,
                null,
                null,
                0,
                0,
                0,
                0,
                errors
            );
        }

        try
        {
            var snapshot = await LoadAsync(cancellationToken);
            return new SkillWorkspaceStatus(
                true,
                options.WorkspaceId,
                options.ExcelDataRoot,
                options.HeroAuthoringSchemaPath,
                true,
                true,
                snapshot.Revision,
                snapshot.SourceHash,
                snapshot.Catalog.Tables.Count,
                snapshot.Graph.Nodes.Count,
                snapshot.Graph.Edges.Count,
                snapshot.Catalog.Table("skill").Records.Count,
                []
            );
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load skill workspace.");
            return new SkillWorkspaceStatus(
                false,
                options.WorkspaceId,
                options.ExcelDataRoot,
                options.HeroAuthoringSchemaPath,
                true,
                true,
                null,
                null,
                0,
                0,
                0,
                0,
                [exception.Message]
            );
        }
    }

    public async Task<IReadOnlyList<SkillSummary>> ListSkillsAsync(
        string? query,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "技能数量必须在 1 到 500 之间。"
            );
        }

        var snapshot = await LoadAsync(cancellationToken);
        GameDataTable skills = snapshot.Catalog.Table("skill");
        Dictionary<string, int> incoming = snapshot
            .Graph.Edges.GroupBy(edge => edge.Target, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Dictionary<string, int> outgoing = snapshot
            .Graph.Edges.GroupBy(edge => edge.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        string term = query?.Trim() ?? "";

        return skills
            .Records.Select(record =>
            {
                string key = $"TbSkill:{record.Id}";
                string label = Title(skills, record);
                return new SkillSummary(
                    record.Id,
                    label,
                    BuildSkillSummary(record),
                    record.SourceRow,
                    incoming.GetValueOrDefault(key),
                    outgoing.GetValueOrDefault(key)
                );
            })
            .Where(item =>
                term.Length == 0
                || item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    .Contains(term, StringComparison.OrdinalIgnoreCase)
                || item.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
                || item.Summary.Contains(term, StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(item => item.Id)
            .Take(limit)
            .ToArray();
    }

    public async Task<IReadOnlyList<AssetSearchResult>> SearchAssetsAsync(
        string? query,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "资产检索数量必须在 1 到 50 之间。"
            );
        }

        string term = query?.Trim() ?? "";
        if (term.Length == 0)
        {
            return [];
        }

        var snapshot = await LoadAsync(cancellationToken);
        var results = new List<AssetSearchResult>();
        foreach ((string tableKey, string nodeNamespace) in SearchRoots)
        {
            GameDataTable? table = snapshot.Catalog.Tables.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Key,
                    tableKey,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (table is null)
            {
                continue;
            }

            foreach (GameDataRecord record in table.Records)
            {
                string label = Title(table, record);
                string searchableText = string.Join(
                    " ",
                    new[] { label, record.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                        .Concat(
                            record.Fields.Values.SelectMany(values => values)
                        )
                );
                if (
                    !searchableText.Contains(
                        term,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    continue;
                }

                results.Add(
                    new AssetSearchResult(
                        new StudioAssetRef(nodeNamespace, record.Id),
                        label,
                        BuildAssetSearchSummary(table, record),
                        table.DisplayName,
                        record.SourceRow
                    )
                );
            }
        }

        AddEffectGroupSearchResults(snapshot.Catalog, term, results);

        return results
            .GroupBy(
                result => result.Ref,
                EqualityComparer<StudioAssetRef>.Default
            )
            .Select(group => group.First())
            .OrderByDescending(
                result => result.Ref.Id.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                ) == term
            )
            .ThenBy(result => result.Ref.Namespace, StringComparer.Ordinal)
            .ThenBy(result => result.Ref.Id)
            .Take(limit)
            .ToArray();
    }

    public async Task<IReadOnlyList<AssetSearchResult>> SearchAssetIdsAsync(
        string? query,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "资产ID检索数量必须在 1 到 50 之间。"
            );
        }

        string term = query?.Trim() ?? "";
        if (
            !int.TryParse(
                term,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int assetId
            )
        )
        {
            return [];
        }

        var snapshot = await LoadAsync(cancellationToken);
        var results = new List<AssetSearchResult>();
        foreach ((string tableKey, string nodeNamespace) in SearchRoots)
        {
            GameDataTable? table = snapshot.Catalog.Tables.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Key,
                    tableKey,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (table is null)
            {
                continue;
            }

            GameDataRecord? record = table.Records.FirstOrDefault(
                candidate => candidate.Id == assetId
            );
            if (record is null)
            {
                continue;
            }

            results.Add(
                new AssetSearchResult(
                    new StudioAssetRef(nodeNamespace, record.Id),
                    Title(table, record),
                    BuildAssetSearchSummary(table, record),
                    table.DisplayName,
                    record.SourceRow
                )
            );
            if (results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<AssetSearchResult>> SearchConversationCandidatesByNumericIdAsync(
        string? query,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "对话资产候选数量必须在 1 到 100 之间。"
            );
        }

        string term = query?.Trim() ?? "";
        if (
            !int.TryParse(
                term,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int assetId
            )
        )
        {
            return [];
        }

        var snapshot = await LoadAsync(cancellationToken);
        var results = new List<AssetSearchResult>();
        foreach ((string tableKey, string nodeNamespace) in SearchRoots)
        {
            GameDataTable? table = snapshot.Catalog.Tables.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Key,
                    tableKey,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (table is null)
            {
                continue;
            }

            GameDataRecord? record = table.Records.FirstOrDefault(
                candidate => candidate.Id == assetId
            );
            if (record is null)
            {
                continue;
            }

            results.Add(
                new AssetSearchResult(
                    new StudioAssetRef(nodeNamespace, record.Id),
                    Title(table, record),
                    BuildAssetSearchSummary(table, record),
                    table.DisplayName,
                    record.SourceRow
                )
            );
        }

        AddGroupSearchResults(
            snapshot.Catalog,
            "effect",
            "TbEffect",
            "EffectGroup",
            "EffectGroup",
            assetId,
            results
        );
        AddGroupSearchResults(
            snapshot.Catalog,
            "condition",
            "TbCondition",
            "ConditionGroup",
            "ConditionGroup",
            assetId,
            results
        );

        return results
            .Where(result => result.Ref.Id == assetId)
            .GroupBy(
                result => result.Ref,
                EqualityComparer<StudioAssetRef>.Default
            )
            .Select(group => group.First())
            .Take(limit)
            .ToArray();
    }

    public async Task<IReadOnlyList<AssetTableFieldSummary>> GetTableFieldsAsync(
        IReadOnlyDictionary<string, string> entityNamespaces,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await LoadAsync(cancellationToken);
        var fields = new List<AssetTableFieldSummary>();
        foreach (
            KeyValuePair<string, string> entity in entityNamespaces.OrderBy(
                item => item.Key,
                StringComparer.Ordinal
            )
        )
        {
            string normalizedNamespace = NormalizeAssetNamespace(
                entity.Value
            );
            if (
                !HeroAuthoringGraphProjector.TryGetTableKey(
                    normalizedNamespace,
                    out string tableKey
                )
            )
            {
                continue;
            }

            GameDataTable? table = snapshot.Catalog.Tables.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Key,
                    tableKey,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (table is null)
            {
                continue;
            }

            foreach (GameDataFieldDefinition field in table.Fields)
            {
                string? referenceTarget = null;
                if (!string.IsNullOrWhiteSpace(field.ReferenceTable))
                {
                    referenceTarget =
                        HeroAuthoringGraphProjector.TryGetNamespace(
                            field.ReferenceTable,
                            out string targetNamespace
                        )
                            ? targetNamespace
                            : field.ReferenceTable;
                }

                fields.Add(
                    new AssetTableFieldSummary(
                        entity.Key,
                        normalizedNamespace,
                        table.Key,
                        field.Key,
                        field.Label,
                        $"{entity.Key}.{field.Key}",
                        field.Kind.ToString(),
                        field.RawType,
                        field.Required,
                        referenceTarget,
                        null,
                        field.Options
                            .Select(
                                option => new AssetTableFieldOption(
                                    option.Value,
                                    option.Label,
                                    option.Code,
                                    option.LegacyValue
                                )
                            )
                            .ToArray()
                    )
                );
            }
        }

        return fields;
    }

    public async Task<
        IReadOnlyList<(
            AssetSearchResult Result,
            int Score,
            bool IsExactName
        )>
    > FindAssetsByMentionAsync(
        string message,
        int limit,
        IReadOnlySet<string>? namespaceFilter,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(message) || limit is < 1 or > 50)
        {
            return [];
        }

        var snapshot = await LoadAsync(cancellationToken);
        var matches = new List<(
            AssetSearchResult Result,
            int Score,
            bool IsExactName
        )>();
        foreach ((string tableKey, string nodeNamespace) in SearchRoots)
        {
            if (
                namespaceFilter is { Count: > 0 }
                && !namespaceFilter.Contains(nodeNamespace)
            )
            {
                continue;
            }

            GameDataTable? table = snapshot.Catalog.Tables.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Key,
                    tableKey,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (table is null)
            {
                continue;
            }

            foreach (GameDataRecord record in table.Records)
            {
                string label = Title(table, record);
                string summary = BuildAssetSearchSummary(table, record);
                int score =
                    AssetLabelMentionScore(message, label)
                    + AssetFieldMentionScore(message, record.Fields);
                if (score <= 0)
                {
                    continue;
                }
                score += NamespaceIntentScore(message, nodeNamespace);
                bool isExactName = IsExactMentionName(
                    message,
                    label
                );

                matches.Add(
                    (
                        new AssetSearchResult(
                            new StudioAssetRef(nodeNamespace, record.Id),
                            label,
                            summary,
                            table.DisplayName,
                            record.SourceRow
                        ),
                        score,
                        isExactName
                    )
                );
            }
        }

        IReadOnlyList<(
            AssetSearchResult Result,
            int Score,
            bool IsExactName
        )> ranked = matches
            .OrderByDescending(match => match.IsExactName)
            .ThenByDescending(match => match.Score)
            .ThenBy(
                match => match.Result.Ref.Namespace == "TbSkill" ? 0 : 1
            )
            .ThenBy(match => match.Result.Ref.Id)
            .ToArray();
        var selected = new List<(
            AssetSearchResult Result,
            int Score,
            bool IsExactName
        )>();
        var namespaceCounts = new Dictionary<string, int>(
            StringComparer.Ordinal
        );
        var seen = new HashSet<StudioAssetRef>();
        foreach (
            (AssetSearchResult result, int score, bool isExactName) in ranked
        )
        {
            if (!seen.Add(result.Ref))
            {
                continue;
            }

            int namespaceCount = namespaceCounts.GetValueOrDefault(
                result.Ref.Namespace
            );
            if (namespaceCount >= MaxMentionCandidatesPerNamespace)
            {
                continue;
            }

            namespaceCounts[result.Ref.Namespace] = namespaceCount + 1;
            selected.Add((result, score, isExactName));
            if (selected.Count >= limit)
            {
                break;
            }
        }

        return selected;
    }

    private static int AssetLabelMentionScore(string message, string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return 0;
        }

        if (
            message.Contains(label, StringComparison.OrdinalIgnoreCase)
        )
        {
            return 2000 + label.Length;
        }

        if (
            message.Length >= 2
            && label.Contains(message, StringComparison.OrdinalIgnoreCase)
        )
        {
            return 1800 + message.Length;
        }

        int score = 0;
        foreach (string token in MentionTokens(label))
        {
            if (
                token.Length >= 2
                && !GenericMentionTokens.Contains(token)
                && message.Contains(
                    token,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                score = Math.Max(score, 1000 + token.Length);
            }
        }

        return score;
    }

    private static int AssetFieldMentionScore(
        string message,
        IReadOnlyDictionary<string, IReadOnlyList<string>> fields
    )
    {
        int score = 0;
        foreach (
            KeyValuePair<string, IReadOnlyList<string>> field in fields
        )
        {
            foreach (string value in field.Value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (
                    string.Equals(
                        message,
                        value,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    score = Math.Max(score, 900 + value.Length);
                    continue;
                }

                if (
                    message.Length >= 2
                    && value.Contains(
                        message,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    score = Math.Max(score, 700 + message.Length);
                    continue;
                }

                foreach (string token in MentionTokens(value))
                {
                    if (
                        token.Length >= 2
                        && !GenericMentionTokens.Contains(token)
                        && message.Contains(
                            token,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        score = Math.Max(score, 100 + token.Length);
                    }
                }
            }
        }

        return score;
    }

    private static bool IsExactMentionName(
        string message,
        string label
    )
    {
        return !string.IsNullOrWhiteSpace(label)
            && message.Contains(label, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> MentionTokens(string text)
    {
        return text.Split(
            [
                '-',
                '_',
                '/',
                ' ',
                '·',
                ',',
                '.',
                ':',
                ';',
                '，',
                '。',
                '：',
                '；'
            ],
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries
        );
    }

    private static int NamespaceIntentScore(
        string message,
        string nodeNamespace
    )
    {
        if (
            (
                message.Contains("技能", StringComparison.Ordinal)
                || message.Contains("伤害", StringComparison.Ordinal)
                || message.Contains("效果", StringComparison.Ordinal)
                || message.Contains("冷却", StringComparison.Ordinal)
                || message.Contains("属性", StringComparison.Ordinal)
                || message.Contains("skill", StringComparison.OrdinalIgnoreCase)
            )
            && string.Equals(
                nodeNamespace,
                "TbSkill",
                StringComparison.Ordinal
            )
        )
        {
            return 100;
        }

        if (
            (message.Contains("物品", StringComparison.Ordinal)
                || message.Contains("道具", StringComparison.Ordinal))
            && string.Equals(
                nodeNamespace,
                "TbItem",
                StringComparison.Ordinal
            )
        )
        {
            return 50;
        }

        return 0;
    }

    private static void AddEffectGroupSearchResults(
        GameDataCatalog catalog,
        string term,
        ICollection<AssetSearchResult> results
    )
    {
        GameDataTable? effects = catalog.Tables.FirstOrDefault(
            table => string.Equals(
                table.Key,
                "effect",
                StringComparison.OrdinalIgnoreCase
            )
        );
        if (effects is null)
        {
            return;
        }

        foreach (
            IGrouping<string, GameDataRecord> group in effects.Records.GroupBy(
                record =>
                    Values(record, "group_id").FirstOrDefault(Configured)
                    ?? ""
            )
        )
        {
            if (
                group.Key.Length == 0
                || !group.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (!int.TryParse(group.Key, out int groupId))
            {
                continue;
            }

            results.Add(
                new AssetSearchResult(
                    new StudioAssetRef("EffectGroup", groupId),
                    $"EffectGroup {groupId}",
                    $"{group.Count()} 个有序 Effect",
                    "Effect Group",
                    group.Min(record => record.SourceRow)
                )
            );
        }
    }

    private static void AddGroupSearchResults(
        GameDataCatalog catalog,
        string tableKey,
        string memberNamespace,
        string namespaceName,
        string displayName,
        int groupId,
        ICollection<AssetSearchResult> results
    )
    {
        GameDataTable? table = catalog.Tables.FirstOrDefault(
            candidate => string.Equals(
                candidate.Key,
                tableKey,
                StringComparison.OrdinalIgnoreCase
            )
        );
        if (table is null)
        {
            return;
        }

        GameDataRecord[] members = table
            .Records.Where(
                record =>
                    Values(record, "group_id").Any(
                        value =>
                            int.TryParse(value, out int memberGroupId)
                            && memberGroupId == groupId
                    )
            )
            .OrderBy(record => record.SourceOrder)
            .ToArray();
        if (members.Length == 0)
        {
            return;
        }

        results.Add(
            new AssetSearchResult(
                new StudioAssetRef(namespaceName, groupId),
                $"{displayName} {groupId}",
                $"{members.Length} 个成员",
                displayName,
                members.Min(record => record.SourceRow)
            )
        );
        foreach (GameDataRecord member in members)
        {
            results.Add(
                new AssetSearchResult(
                    new StudioAssetRef(memberNamespace, member.Id),
                    Title(table, member),
                    BuildAssetSearchSummary(table, member),
                    table.DisplayName,
                    member.SourceRow
                )
            );
        }
    }

    private static string BuildAssetSearchSummary(
        GameDataTable table,
        GameDataRecord record
    )
    {
        string? text = Values(
                record,
                "desc",
                "description",
                "remark",
                "__remark_2",
                "name"
            )
            .FirstOrDefault(Configured);
        return string.IsNullOrWhiteSpace(text)
            ? $"{table.DisplayName} · 行 {record.SourceRow}"
            : text;
    }

    public async Task<SkillChainSnapshot> GetSkillChainAsync(
        int skillId,
        int depth,
        CancellationToken cancellationToken
    )
    {
        return await GetAssetChainAsync(
            new StudioAssetRef("TbSkill", skillId),
            depth,
            cancellationToken
        );
    }

    public async Task<SkillChainSnapshot> GetAssetChainAsync(
        StudioAssetRef root,
        int depth,
        CancellationToken cancellationToken
    )
    {
        return await GetAssetChainAsync(
            root,
            depth,
            "out",
            cancellationToken
        );
    }

    public async Task<SkillChainSnapshot> GetExecutionChainAsync(
        StudioAssetRef root,
        int depth,
        CancellationToken cancellationToken
    )
    {
        return await GetExecutionChainAsync(
            root,
            depth,
            "out",
            cancellationToken
        );
    }

    public async Task<SkillChainSnapshot> GetExecutionChainAsync(
        StudioAssetRef root,
        int depth,
        string direction,
        CancellationToken cancellationToken
    )
    {
        SkillChainSnapshot chain = await GetAssetChainAsync(
            root,
            depth,
            direction,
            cancellationToken
        );
        return executionProjectionPolicy.Project(chain);
    }

    public async Task<SkillChainSnapshot> GetAssetChainAsync(
        StudioAssetRef root,
        int depth,
        string direction,
        CancellationToken cancellationToken
    )
    {
        if (depth is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(depth),
                "链路深度必须在 0 到 32 之间。"
            );
        }

        string rootNamespace = NormalizeAssetNamespace(root.Namespace);
        string normalizedDirection = NormalizeDirection(direction);
        var snapshot = await LoadAsync(cancellationToken);
        string rootKey = $"{rootNamespace}:{root.Id}";
        HeroAuthoringGraph graph;
        if (normalizedDirection == "out")
        {
            graph = HeroAuthoringRootProfiles.TryNormalize(
                rootNamespace,
                out _
            )
                ? snapshot.Projector.ProjectBehavior(
                    snapshot.Catalog,
                    rootNamespace,
                    root.Id,
                    depth
                )
                : snapshot.Projector.Project(
                    snapshot.Catalog,
                    rootNamespace,
                    root.Id,
                    Math.Min(depth, 5)
                );
            if (
                !HeroAuthoringRootProfiles.TryNormalize(
                    rootNamespace,
                    out _
                )
            )
            {
                graph = SelectDownstream(graph, graph.FocusKey);
            }
        }
        else
        {
            graph = SelectDirectional(
                snapshot.Graph,
                rootKey,
                depth,
                normalizedDirection
            );
        }
        SkillInboundReferenceSet incoming =
            await GetIncomingReferencesAsync(
                new StudioAssetRef(rootNamespace, root.Id),
                40,
                cancellationToken
            );

        return new SkillChainSnapshot(
            snapshot.Revision,
            graph.FocusKey,
            rootNamespace,
            root.Id,
            graph.FocusKey,
            graph.Nodes.Select(node => new SkillChainNode(
                node.Key,
                node.Namespace,
                node.LegacyId,
                node.Label,
                node.Kind,
                node.TableKey,
                node.IsVirtual,
                node.IsCode,
                node.IsMissing,
                node.IsFocus,
                node.SourceRow,
                node.Fields
            )).ToArray(),
            graph.Edges.Select(edge => new SkillChainEdge(
                edge.Id,
                edge.Source,
                edge.Target,
                edge.Role,
                edge.Label,
                edge.Detail,
                edge.Derived,
                edge.SourceField,
                edge.ParameterIndex
            )).ToArray(),
            incoming.References,
            incoming.Truncated
        );
    }

    private static string NormalizeDirection(string direction)
    {
        return direction.Trim().ToLowerInvariant() switch
        {
            "out" => "out",
            "in" => "in",
            "both" => "both",
            _ => throw new ArgumentException(
                "direction 只能是 out、in 或 both。",
                nameof(direction)
            )
        };
    }

    private static HeroAuthoringGraph SelectDirectional(
        HeroAuthoringGraph graph,
        string focusKey,
        int depth,
        string direction
    )
    {
        if (
            !graph.Nodes.Any(
                node => string.Equals(
                    node.Key,
                    focusKey,
                    StringComparison.Ordinal
                )
            )
        )
        {
            throw new KeyNotFoundException(
                $"英雄配置图谱中不存在节点 {focusKey}。"
            );
        }

        bool includeOutgoing =
            direction is "out" or "both";
        bool includeIncoming =
            direction is "in" or "both";
        var included = new HashSet<string>(
            StringComparer.Ordinal
        )
        {
            focusKey
        };
        var frontier = new HashSet<string>(
            StringComparer.Ordinal
        )
        {
            focusKey
        };

        for (int level = 0; level < depth; level++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (HeroAuthoringGraphEdge edge in graph.Edges)
            {
                if (
                    includeOutgoing
                    && frontier.Contains(edge.Source)
                    && !IsCodeTerminal(edge.Source)
                    && included.Add(edge.Target)
                )
                {
                    next.Add(edge.Target);
                }

                if (
                    includeIncoming
                    && frontier.Contains(edge.Target)
                    && !IsCodeTerminal(edge.Target)
                    && included.Add(edge.Source)
                )
                {
                    next.Add(edge.Source);
                }
            }

            frontier = next;
            if (frontier.Count == 0)
            {
                break;
            }
        }

        HeroAuthoringGraphNode[] nodes = graph
            .Nodes.Where(node => included.Contains(node.Key))
            .Select(
                node => node with
                {
                    IsFocus = string.Equals(
                        node.Key,
                        focusKey,
                        StringComparison.Ordinal
                    )
                }
            )
            .OrderByDescending(node => node.IsFocus)
            .ThenBy(node => node.Namespace, StringComparer.Ordinal)
            .ThenBy(node => node.LegacyId)
            .ToArray();
        HeroAuthoringGraphEdge[] edges = graph
            .Edges.Where(
                edge =>
                    included.Contains(edge.Source)
                    && included.Contains(edge.Target)
            )
            .DistinctBy(edge => edge.Id, StringComparer.Ordinal)
            .ToArray();
        return new HeroAuthoringGraph(focusKey, nodes, edges);
    }

    private static bool IsCodeTerminal(string key) =>
        key.StartsWith(
            "CodeEffectExecutor:",
            StringComparison.Ordinal
        )
        || key.StartsWith(
            "CodeConditionHandler:",
            StringComparison.Ordinal
        );

    public async Task<SkillInboundReferenceSet> GetIncomingReferencesAsync(
        int skillId,
        int limit,
        CancellationToken cancellationToken
    )
    {
        return await GetIncomingReferencesAsync(
            new StudioAssetRef("TbSkill", skillId),
            limit,
            cancellationToken
        );
    }

    public async Task<SkillInboundReferenceSet> GetIncomingReferencesAsync(
        StudioAssetRef root,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "引用数量必须在 1 到 200 之间。"
            );
        }

        var snapshot = await LoadAsync(cancellationToken);
        string rootNamespace = NormalizeAssetNamespace(root.Namespace);
        string target = $"{rootNamespace}:{root.Id}";
        Dictionary<string, TianshuDM.Domain.HeroAuthoring.HeroAuthoringGraphNode> nodes =
            snapshot.Graph.Nodes.ToDictionary(
                node => node.Key,
                StringComparer.Ordinal
            );

        IReadOnlyList<SkillInboundReference> references = snapshot
            .Graph.Edges.Where(
                edge => string.Equals(
                    edge.Target,
                    target,
                    StringComparison.Ordinal
                )
            )
            .Select(edge =>
            {
                nodes.TryGetValue(edge.Source, out var source);
                return new SkillInboundReference(
                    edge.Source,
                    source?.Kind ?? "",
                    source?.Label ?? edge.Source,
                    edge.Label,
                    edge.SourceField,
                    edge.ParameterIndex,
                    edge.Derived,
                    edge.Detail,
                    source?.Fields
                        ?? new Dictionary<string, IReadOnlyList<string>>()
                );
            })
            .GroupBy(
                reference => (
                    reference.Source,
                    reference.Relationship,
                    reference.SourceField,
                    reference.ParameterIndex
                )
            )
            .Select(group => group.First())
            .OrderBy(reference => reference.Source, StringComparer.Ordinal)
            .ThenBy(
                reference => reference.SourceField,
                StringComparer.Ordinal
            )
            .ToArray();
        return new SkillInboundReferenceSet(
            references.Take(limit).ToArray(),
            references.Count,
            references.Count > limit
        );
    }

    public static string NormalizeAssetNamespace(string value)
    {
        string normalized = value.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "skill" or "tbskill" => "TbSkill",
            "item" or "tb item" or "tbitem" => "TbItem",
            "effect" or "tbeffect" => "TbEffect",
            "effectgroup" or "effect-group" or "effect group" => "EffectGroup",
            "conditiongroup" or "condition-group" or "condition group" =>
                "ConditionGroup",
            "buff" or "tbbuff" => "TbBuff",
            "bullet" or "tbbullet" => "TbBullet",
            "search" or "tbsearch" => "TbSearch",
            "trap" or "tbtrap" => "TbTrap",
            "equipment" or "tbequipment" => "TbEquipment",
            "condition" or "tbcondition" => "TbCondition",
            "damagepipeline" or "damage-pipeline" or "damage pipeline" =>
                "TbDamagePipeline",
            "skillresource" or "skill-resource" or "skill resource" =>
                "TbSkillResource",
            "resource" or "tbresource" => "TbResource",
            "soldier" or "tbsoldier" => "TbSoldier",
            "building" or "tbbuilding" => "TbBuilding",
            _ => normalized
        };
    }

    private static HeroAuthoringGraph SelectDownstream(
        HeroAuthoringGraph graph,
        string focusKey
    )
    {
        var included = new HashSet<string>(StringComparer.Ordinal)
        {
            focusKey
        };
        var queue = new Queue<string>();
        queue.Enqueue(focusKey);
        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            foreach (
                HeroAuthoringGraphEdge edge in graph.Edges.Where(
                    edge => string.Equals(
                        edge.Source,
                        current,
                        StringComparison.Ordinal
                    )
                )
            )
            {
                if (included.Add(edge.Target))
                {
                    queue.Enqueue(edge.Target);
                }
            }
        }

        return new HeroAuthoringGraph(
            focusKey,
            graph.Nodes.Where(node => included.Contains(node.Key))
                .Select(node => node with { IsFocus = node.Key == focusKey })
                .ToArray(),
            graph.Edges.Where(
                edge =>
                    included.Contains(edge.Source)
                    && included.Contains(edge.Target)
            ).ToArray()
        );
    }

    public async Task<WriteSmokeTestResult> RunWriteSmokeTestAsync(
        CancellationToken cancellationToken
    )
    {
        string sourceDataRoot = Path.GetFullPath(options.ExcelDataRoot);
        string outputRoot = Path.Combine(
            ResolveContentRootPath(options.WriteTestRoot),
            $"write-smoke-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
        );
        string outputDataRoot = Path.Combine(
            outputRoot,
            "Unity",
            "Assets",
            "Config",
            "Excel",
            "Datas"
        );

        WriteSmokeTestResult result = await Task.Run(
            () =>
            {
                Directory.CreateDirectory(outputDataRoot);
                CopyTree(sourceDataRoot, outputDataRoot);

                var workbookReader = new GameDataWorkbookReader();
                var catalogReader = new UnitGameDataCatalogReader(workbookReader);
                GameDataCatalog originalCatalog = catalogReader.Read(outputDataRoot);
                GameDataTable originalSkills = originalCatalog.Table("skill");
                var writer = new GameDataWorkbookWriter();
                writer.Write(originalSkills.WorkbookPath, originalSkills);

                GameDataTable rereadSkills = workbookReader.Read(
                    new GameDataTableSource(
                        originalSkills.Key,
                        originalSkills.DisplayName,
                        originalSkills.Category,
                        "Skill/Skill.xlsx",
                        originalSkills.WorkbookPath
                    )
                );
                bool fieldsMatch = TablesMatch(originalSkills, rereadSkills);

                logger.LogInformation(
                    "Write smoke test completed for {Table}: {Records} records, fieldsMatch={FieldsMatch}, output={Output}.",
                    originalSkills.Key,
                    originalSkills.Records.Count,
                    fieldsMatch,
                    originalSkills.WorkbookPath
                );

                return new WriteSmokeTestResult(
                    "completed",
                    originalSkills.Key,
                    originalSkills.WorkbookPath,
                    originalSkills.Records.Count,
                    rereadSkills.Records.Count,
                    fieldsMatch,
                    originalSkills.SourceHash,
                    rereadSkills.SourceHash
                );
            },
            cancellationToken
        );
        PruneWriteTestRuns(Path.GetDirectoryName(outputRoot)!);
        return result;
    }

    public async Task<WorkbookPatchWorkspaceSnapshot> GetPatchWorkspaceSnapshotAsync(
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(options.ExcelDataRoot))
            throw new InvalidOperationException("尚未配置 Excel 工作区。");
        WorkspaceSnapshot snapshot = await LoadAsync(cancellationToken);
        return new WorkbookPatchWorkspaceSnapshot(
            options.WorkspaceId,
            snapshot.Revision,
            snapshot.SourceHash,
            snapshot.Catalog
        );
    }

    private async Task<WorkspaceSnapshot> LoadAsync(
        CancellationToken cancellationToken
    )
    {
        WorkspaceSnapshot? current = _snapshot;
        if (current is not null)
        {
            return current;
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            current = _snapshot;
            if (current is not null)
            {
                return current;
            }

            current = await Task.Run(
                () =>
                {
                    return CreateSnapshotFromSharedCopy();
                },
                cancellationToken
            );
            _snapshot = current;
            logger.LogInformation(
                "Loaded skill workspace: {Tables} tables, {Nodes} nodes, {Edges} edges, revision {Revision}.",
                current.Catalog.Tables.Count,
                current.Graph.Nodes.Count,
                current.Graph.Edges.Count,
                current.Revision
            );
            return current;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private WorkspaceSnapshot CreateSnapshotFromSharedCopy()
    {
        string sourceDataRoot = Path.GetFullPath(options.ExcelDataRoot);
        DirectoryInfo? excelRoot = Directory.GetParent(sourceDataRoot);
        DirectoryInfo? configRoot = excelRoot?.Parent;
        DirectoryInfo? assetsRoot = configRoot?.Parent;
        if (
            excelRoot is null
            || configRoot is null
            || assetsRoot is null
        )
        {
            throw new InvalidOperationException(
                $"无法推导 Excel 工作区父目录：{sourceDataRoot}"
            );
        }

        string snapshotRoot = Path.Combine(
            Path.GetTempPath(),
            "RtsSkillStudio",
            Guid.NewGuid().ToString("N")
        );
        string snapshotAssetsRoot = Path.Combine(snapshotRoot, "Unity", "Assets");
        string snapshotConfigRoot = Path.Combine(snapshotAssetsRoot, "Config");
        string snapshotExcelRoot = Path.Combine(snapshotConfigRoot, "Excel");
        string snapshotDataRoot = Path.Combine(snapshotExcelRoot, "Datas");
        string sourceHash = ComputeSourceTreeHash(sourceDataRoot);

        try
        {
            CopyTree(sourceDataRoot, snapshotDataRoot);

            string sourceBuiltin = Path.Combine(excelRoot.FullName, "Defines");
            if (Directory.Exists(sourceBuiltin))
            {
                CopyTree(
                    sourceBuiltin,
                    Path.Combine(snapshotExcelRoot, "Defines")
                );
            }

            string sourceGeneratedEnumRoot = Path.Combine(
                assetsRoot.FullName,
                "Scripts",
                "Model",
                "Generate",
                "Client",
                "Config"
            );
            if (Directory.Exists(sourceGeneratedEnumRoot))
            {
                CopyTree(
                    sourceGeneratedEnumRoot,
                    Path.Combine(
                        snapshotAssetsRoot,
                        "Scripts",
                        "Model",
                        "Generate",
                        "Client",
                        "Config"
                    )
                );
            }

            var workbookReader = new GameDataWorkbookReader();
            var catalogReader = new UnitGameDataCatalogReader(workbookReader);
            GameDataCatalog catalog = catalogReader.Read(snapshotDataRoot);
            var schemaSource = new HeroAuthoringSchemaJsonReader(
                options.HeroAuthoringSchemaPath
            );
            var projector = new HeroAuthoringGraphProjector(schemaSource);
            HeroAuthoringGraph graph = projector.ProjectCatalog(catalog);
            string revision = HeroAuthoringCatalogRevision.Compute(catalog);
            return new WorkspaceSnapshot(
                catalog,
                revision,
                projector,
                graph,
                sourceHash
            );
        }
        finally
        {
            DeleteTemporaryDirectory(snapshotRoot);
        }
    }

    internal static void CopyTree(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (string sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            string destinationPath = Path.Combine(destinationRoot, relativePath);
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            );
            source.CopyTo(destination);
        }
    }

    public static string ComputeSourceTreeHash(string sourceRoot)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        foreach (
            string sourcePath in Directory
                .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(
                    path =>
                        !Path.GetFileName(path).StartsWith(
                            "~$",
                            StringComparison.OrdinalIgnoreCase
                        )
                )
                .OrderBy(
                    path => Path.GetRelativePath(sourceRoot, path),
                    StringComparer.Ordinal
                )
        )
        {
            string relativePath = Path.GetRelativePath(
                    sourceRoot,
                    sourcePath
                )
                .Replace('\\', '/');
            using FileStream stream = File.Open(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            hash.AppendData(
                Encoding.UTF8.GetBytes(
                    $"{relativePath.Length}:{relativePath}\n{stream.Length}\n"
                )
            );
            byte[] buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private void PruneWriteTestRuns(string writeTestRoot)
    {
        if (options.WriteTestRetentionCount <= 0)
        {
            return;
        }

        string root = Path.GetFullPath(writeTestRoot);
        if (!Directory.Exists(root))
        {
            return;
        }

        string rootPrefix =
            root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
        foreach (
            string directory in Directory
                .EnumerateDirectories(root, "write-smoke-*")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .Skip(options.WriteTestRetentionCount)
        )
        {
            string fullPath = Path.GetFullPath(directory);
            if (
                !fullPath.StartsWith(
                    rootPrefix,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            try
            {
                Directory.Delete(fullPath, true);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    exception,
                    "Failed to prune old write smoke directory {Directory}.",
                    fullPath
                );
            }
        }
    }

    internal static void DeleteTemporaryDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (
            !fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !fullPath.Contains(
                $"{Path.DirectorySeparatorChar}RtsSkillStudio{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidOperationException(
                $"拒绝清理非 Studio 临时目录：{fullPath}"
            );
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, true);
        }
    }

    private static bool TablesMatch(GameDataTable left, GameDataTable right)
    {
        if (left.Records.Count != right.Records.Count)
        {
            return false;
        }

        Dictionary<int, GameDataRecord> rightById = right.Records.ToDictionary(
            record => record.Id
        );
        string[] comparedFields = left
            .Fields.Select(field => field.Key)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (GameDataRecord leftRecord in left.Records)
        {
            if (!rightById.TryGetValue(leftRecord.Id, out GameDataRecord? rightRecord))
            {
                return false;
            }

            foreach (string field in comparedFields)
            {
                IReadOnlyList<string> leftValues =
                    leftRecord.Fields.GetValueOrDefault(field) ?? [];
                IReadOnlyList<string> rightValues =
                    rightRecord.Fields.GetValueOrDefault(field) ?? [];
                if (!leftValues.SequenceEqual(rightValues, StringComparer.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string Title(GameDataTable table, GameDataRecord record)
    {
        foreach (string key in new[] { "name", "Name", "remark", "desc", "Desc" })
        {
            string? value = Values(record, key).FirstOrDefault(Configured);
            if (value is not null)
            {
                return value;
            }
        }

        return $"{table.DisplayName} {record.Id}";
    }

    private static string BuildSkillSummary(GameDataRecord record)
    {
        var parts = new List<string>();
        AddPrefixedInteger(parts, Values(record, "effect_group_id"), "主要效果组");
        AddPrefixedInteger(parts, Values(record, "search_target"), "目标搜索");
        AddPrefixedMilliseconds(parts, Values(record, "cooldown", "cooldown_ms", "cd"), "冷却");
        AddPrefixedMilliseconds(parts, Values(record, "duration", "duration_ms"), "持续");
        return parts.Count > 0 ? string.Join(" · ", parts) : "未提取到摘要字段";
    }

    private static void AddPrefixedInteger(
        ICollection<string> parts,
        IReadOnlyList<string> values,
        string label
    )
    {
        string? value = values.FirstOrDefault(Configured);
        if (value is not null)
        {
            parts.Add($"{label} {value}");
        }
    }

    private static void AddPrefixedMilliseconds(
        ICollection<string> parts,
        IReadOnlyList<string> values,
        string label
    )
    {
        string? raw = values.FirstOrDefault(Configured);
        if (raw is null)
        {
            return;
        }

        if (
            decimal.TryParse(
                raw,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out decimal milliseconds
            )
        )
        {
            parts.Add($"{label} {milliseconds / 1000m:0.###} 秒");
        }
    }

    private static IReadOnlyList<string> Values(
        GameDataRecord record,
        params string[] keys
    )
    {
        foreach (string key in keys)
        {
            IReadOnlyList<string> values =
                record.Fields.FirstOrDefault(
                    pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                ).Value ?? [];
            if (values.Any(Configured))
            {
                return values;
            }
        }

        return [];
    }

    private static bool Configured(string value) =>
        !string.IsNullOrWhiteSpace(value) && value != "0";

    private string ResolveContentRootPath(string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
    }

    private sealed record WorkspaceSnapshot(
        GameDataCatalog Catalog,
        string Revision,
        HeroAuthoringGraphProjector Projector,
        HeroAuthoringGraph Graph,
        string SourceHash
    );
}
