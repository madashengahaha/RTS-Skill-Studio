using System.Text;
using System.Text.RegularExpressions;
using RtsSkillStudio.Agent;
using RtsSkillStudio.Agent.Patch;
using RtsSkillStudio.Agent.Workspaces;

namespace RtsSkillStudio.Api.Workspaces;

public sealed partial class SkillAgentContextBuilder(
    SkillWorkspaceService workspace,
    WorkbookPatchRegistry patchRegistry,
    ILogger<SkillAgentContextBuilder> logger
)
{
    private const int MaxHistoryNodes = 160;
    private const int MaxHistoryEdges = 320;
    private const int MaxFieldValues = 8;

    private static readonly IReadOnlyDictionary<string, string[]>
        NamespaceHints = new Dictionary<string, string[]>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["TbSkill"] = ["skill", "skills", "技能"],
            ["TbEffect"] = ["effect", "effects", "效果", "特效"],
            ["TbBuff"] = ["buff", "buffs"],
            ["TbBullet"] = ["bullet", "bullets", "子弹"],
            ["TbTrap"] = ["trap", "traps", "陷阱", "机关"],
            ["TbSearch"] = ["search", "搜索"],
            ["TbItem"] = ["item", "items", "物品", "道具"],
            ["TbEquipment"] = ["equipment", "装备"],
            ["TbCard"] = ["card", "cards", "卡牌"],
            ["TbHero"] = ["hero", "heroes", "英雄"],
            ["TbCondition"] = ["condition", "conditions", "条件"],
            ["TbDamagePipeline"] =
                ["damage pipeline", "伤害管线"],
            ["TbSkillResource"] =
                ["skill resource", "技能资源"],
            ["TbResource"] = ["resource", "resources", "资源"]
        };

    public async Task<AgentWorkspaceContext> BuildAsync(
        StudioAssetRef? selectedAsset,
        string userMessage,
        CancellationToken cancellationToken
    )
    {
        return await BuildAsync(
            selectedAsset,
            userMessage,
            includeGraphContext: true,
            cancellationToken
        );
    }

    public async Task<AgentWorkspaceContext> BuildBootstrapAsync(
        StudioAssetRef? selectedAsset,
        string userMessage,
        CancellationToken cancellationToken
    )
    {
        return await BuildAsync(
            selectedAsset,
            userMessage,
            includeGraphContext: false,
            cancellationToken
        );
    }

    private async Task<AgentWorkspaceContext> BuildAsync(
        StudioAssetRef? selectedAsset,
        string userMessage,
        bool includeGraphContext,
        CancellationToken cancellationToken
    )
    {
        try
        {
            SkillWorkspaceStatus status = await workspace.GetStatusAsync(
                cancellationToken
            );
            var builder = new StringBuilder();
            builder.AppendLine($"工作区 revision: {status.Revision ?? "unknown"}");
            builder.AppendLine($"工作区 ID: {status.WorkspaceId}");
            builder.AppendLine(
                $"源数据根 sourceHash: {status.SourceHash ?? "unknown"}"
            );
            builder.AppendLine(
                $"工作区规模: {status.TableCount} 张表, {status.NodeCount} 个节点, "
                    + $"{status.EdgeCount} 条边, {status.SkillCount} 个技能"
            );

            if (!status.Configured)
            {
                builder.AppendLine(
                    "工作区当前不可用: "
                        + string.Join("；", status.Errors)
                );
                return new AgentWorkspaceContext(
                    builder.ToString(),
                    null,
                    [],
                    [],
                    false
                );
            }

            AssetResolution resolution = await ResolveAssetAsync(
                selectedAsset,
                userMessage,
                cancellationToken
            );
            if (resolution.Asset is null)
            {
                if (resolution.MissingAssetIds is { Count: > 0 })
                {
                    builder.AppendLine(
                        "未找到资产 ID: "
                            + string.Join(", ", resolution.MissingAssetIds)
                    );
                }
                if (resolution.RequiresClarification)
                {
                    builder.AppendLine(
                        "当前请求没有自动选择资产根。必须明确告知用户未绑定操作目标，不能沿用旧资产。"
                    );
                    if (resolution.ValidCandidates.Count > 0)
                    {
                        builder.AppendLine(
                            "有效候选资产: "
                                + string.Join(
                                    ", ",
                                    resolution.ValidCandidates.Select(FormatAsset)
                                )
                        );
                        await AppendCandidateSummariesAsync(
                            builder,
                            resolution.ValidCandidates,
                            cancellationToken
                        );
                    }
                    if (resolution.InvalidCandidates.Count > 0)
                    {
                        builder.AppendLine(
                            "无效或未找到的资产: "
                                + string.Join(
                                    ", ",
                                    resolution.InvalidCandidates.Select(FormatAsset)
                                )
                        );
                    }
                }
                else
                {
                    builder.AppendLine(
                        "当前没有选择资产根。涉及具体对象的问题必须先确定 namespace + id。"
                    );
                }
                return new AgentWorkspaceContext(
                    builder.ToString(),
                    null,
                    resolution.ValidCandidates,
                    resolution.InvalidCandidates,
                    resolution.RequiresClarification,
                    resolution.MissingAssetIds
                );
            }

            StudioAssetRef asset = resolution.Asset;
            builder.AppendLine($"已绑定行为根: {FormatAsset(asset)}");
            if (!includeGraphContext)
            {
                return new AgentWorkspaceContext(
                    builder.ToString(),
                    asset,
                    [],
                    [],
                    false
                );
            }

            SkillChainSnapshot chain = await workspace.GetExecutionChainAsync(
                asset,
                depth: 32,
                cancellationToken
            );
            SkillChainNode? focus = chain.Nodes.FirstOrDefault(
                node => node.IsFocus
            );
            builder.AppendLine($"已选择资产根: {chain.RootKey}");

            if (focus is not null)
            {
                builder.AppendLine("资产根记录字段:");
                AppendFields(builder, focus.Fields, "  ");
            }
            if (
                string.Equals(
                    asset.Namespace,
                    "TbSkill",
                    StringComparison.Ordinal
                )
            )
            {
                AppendSkillSemantics(builder);
            }

            builder.AppendLine("下游节点:");
            foreach (SkillChainNode node in chain.Nodes.Take(MaxHistoryNodes))
            {
                builder.AppendLine(
                    $"- {node.Key} | {node.Kind} | {node.Label}"
                        + (node.IsMissing ? " | MISSING" : "")
                );
            }

            if (chain.Nodes.Count > MaxHistoryNodes)
            {
                builder.AppendLine(
                    $"- 其余 {chain.Nodes.Count - MaxHistoryNodes} 个节点已截断"
                );
            }

            builder.AppendLine("已验证关系:");
            foreach (SkillChainEdge edge in chain.Edges.Take(MaxHistoryEdges))
            {
                builder.AppendLine(
                    $"- {edge.Source} -> {edge.Target} | {edge.Label}"
                        + (string.IsNullOrWhiteSpace(edge.SourceField)
                            ? ""
                            : $" | sourceField={edge.SourceField}")
                        + (edge.ParameterIndex is null
                            ? ""
                            : $" | param[{edge.ParameterIndex}]")
                        + $" | derived={edge.Derived.ToString().ToLowerInvariant()}"
                );
            }

            if (chain.Edges.Count > MaxHistoryEdges)
            {
                builder.AppendLine(
                    $"- 其余 {chain.Edges.Count - MaxHistoryEdges} 条关系已截断"
                );
            }

            SkillInboundReferenceSet incoming =
                await workspace.GetIncomingReferencesAsync(
                    asset,
                    40,
                    cancellationToken
                );
            builder.AppendLine("入向引用（哪些来源使用当前资产）:");
            if (incoming.References.Count == 0)
            {
                builder.AppendLine("- 当前图中没有入向引用。");
            }
            else
            {
                foreach (SkillInboundReference reference in incoming.References)
                {
                    builder.AppendLine(
                        $"- {reference.Source} | {reference.SourceKind} | {reference.SourceLabel}"
                            + $" -> {chain.RootKey}"
                            + $" | {reference.Relationship}"
                            + (string.IsNullOrWhiteSpace(reference.SourceField)
                                ? ""
                                : $" | sourceField={reference.SourceField}")
                            + $" | derived={reference.Derived.ToString().ToLowerInvariant()}"
                            + (string.IsNullOrWhiteSpace(reference.Detail)
                                ? ""
                                : $" | detail={reference.Detail}")
                    );
                    AppendFields(builder, reference.SourceFields, "    ");
                }
                if (incoming.Truncated)
                {
                    builder.AppendLine(
                        $"- 入向引用已截断：显示 {incoming.References.Count}/{incoming.TotalCount}"
                    );
                }
            }

            return new AgentWorkspaceContext(
                builder.ToString(),
                asset,
                [],
                [],
                false
            );
        }
        catch (KeyNotFoundException)
        {
            return new AgentWorkspaceContext(
                $"未找到资产根 {FormatAsset(selectedAsset)}。",
                null,
                [],
                [],
                false
            );
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to build Agent workspace context for {Asset}.",
                FormatAsset(selectedAsset)
            );
            return new AgentWorkspaceContext(
                "当前无法读取工作区资产上下文，必须明确告知用户证据不可用。",
                null,
                [],
                [],
                false
            );
        }
    }

    private async Task<AssetResolution> ResolveAssetAsync(
        StudioAssetRef? selectedAsset,
        string userMessage,
        CancellationToken cancellationToken
    )
    {
        bool parameterContext = ParameterContextRegex().IsMatch(userMessage);
        IReadOnlySet<string> namespaceConstraints =
            ResolveNamespaceConstraints(userMessage);
        var explicitCandidates = new List<StudioAssetRef>();
        foreach (Match match in ExplicitAssetRegex().Matches(userMessage))
        {
            if (!int.TryParse(match.Groups["id"].Value, out int id))
            {
                continue;
            }

            string rawNamespace = match.Groups["namespace"].Success
                ? match.Groups["namespace"].Value
                : NamespaceFromAlias(match.Groups["alias"].Value);
            explicitCandidates.Add(
                new StudioAssetRef(
                    SkillWorkspaceService.NormalizeAssetNamespace(
                        rawNamespace
                    ),
                    id
                )
            );
        }
        foreach (Match match in SuffixAssetRegex().Matches(userMessage))
        {
            if (!int.TryParse(match.Groups["id"].Value, out int id))
            {
                continue;
            }

            explicitCandidates.Add(
                new StudioAssetRef(
                    SkillWorkspaceService.NormalizeAssetNamespace(
                        NamespaceFromAlias(match.Groups["alias"].Value)
                    ),
                    id
                )
            );
        }

        var candidates = explicitCandidates.Distinct().ToList();
        bool numericMode = false;
        var missingAssetIds = new List<string>();
        if (candidates.Count == 0)
        {
            HashSet<int> modificationValueIndexes = ModificationValueRegex()
                .Matches(userMessage)
                .Select(match => match.Groups["id"].Index)
                .ToHashSet();
            foreach (
                Match match in BareAssetRegex().Matches(userMessage)
                    .Where(
                        match =>
                            !parameterContext
                            || !IsInsideSquareBrackets(
                                userMessage,
                                match.Index
                            )
                    )
                    .Where(
                        match =>
                            !modificationValueIndexes.Contains(match.Index)
                    )
            )
            {
                if (!int.TryParse(match.Groups["id"].Value, out int id))
                {
                    continue;
                }

                numericMode = true;
                IReadOnlyList<AssetSearchResult> idMatches =
                    await workspace.SearchConversationCandidatesByNumericIdAsync(
                        id.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                        100,
                        cancellationToken
                    );
                if (namespaceConstraints.Count > 0)
                {
                    idMatches = idMatches
                        .Where(
                            match =>
                                namespaceConstraints.Contains(
                                    match.Ref.Namespace
                                )
                        )
                        .ToArray();
                }
                foreach (AssetSearchResult idMatch in idMatches)
                {
                    candidates.Add(idMatch.Ref);
                }
                if (idMatches.Count == 0)
                {
                    missingAssetIds.Add(
                        id.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        )
                    );
                }
            }
        }

        if (
            candidates.Count == 0
            && !numericMode
            && ShouldResolveAssetMentions(userMessage)
        )
        {
            IReadOnlyList<(
                AssetSearchResult Result,
                int Score,
                bool IsExactName
            )> namedMatches =
                await workspace.FindAssetsByMentionAsync(
                    userMessage,
                    20,
                    namespaceConstraints,
                    cancellationToken
                );
            IReadOnlyList<StudioAssetRef> namedCandidates = namedMatches
                .Select(match => match.Result.Ref)
                .Distinct()
                .ToArray();
            if (namedCandidates.Count > 0)
            {
                if (
                    selectedAsset is not null
                    && namedCandidates.Contains(selectedAsset)
                )
                {
                    return new AssetResolution(
                        selectedAsset,
                        [],
                        [],
                        false
                    );
                }

                return new AssetResolution(
                    null,
                    namedCandidates,
                    [],
                    true
                );
            }
        }

        if (
            candidates.Count == 0
            && !numericMode
            && LooksLikeExplicitTargetMention(userMessage)
        )
        {
            return new AssetResolution(null, [], [], true);
        }

        bool explicitMode =
            explicitCandidates.Count > 0
            || numericMode
            || namespaceConstraints.Count > 0;
        candidates = candidates.Distinct().ToList();
        if (candidates.Count == 0)
        {
            if (explicitMode)
            {
                return new AssetResolution(
                    null,
                    [],
                    [],
                    true,
                    missingAssetIds
                );
            }

            return selectedAsset is null
                ? new AssetResolution(null, [], [], false)
                : new AssetResolution(selectedAsset, [], [], false);
        }

        var validCandidates = new List<StudioAssetRef>();
        var invalidCandidates = new List<StudioAssetRef>();
        foreach (StudioAssetRef candidate in candidates)
        {
            try
            {
                await workspace.GetAssetChainAsync(
                    candidate,
                    depth: 1,
                    cancellationToken
                );
                validCandidates.Add(candidate);
            }
            catch (KeyNotFoundException)
            {
                invalidCandidates.Add(candidate);
            }
            catch (ArgumentException)
            {
                invalidCandidates.Add(candidate);
            }
        }

        validCandidates = validCandidates.Distinct().ToList();
        invalidCandidates = invalidCandidates.Distinct().ToList();
        if (!explicitMode)
        {
            if (selectedAsset is not null)
            {
                StudioAssetRef? contextualCandidate =
                    validCandidates.FirstOrDefault(
                        candidate =>
                            string.Equals(
                                candidate.Namespace,
                                selectedAsset.Namespace,
                                StringComparison.OrdinalIgnoreCase
                            )
                    );
                if (contextualCandidate is not null)
                {
                    return new AssetResolution(
                        contextualCandidate,
                        [],
                        [],
                        false
                    );
                }
            }

            return new AssetResolution(
                null,
                validCandidates,
                invalidCandidates,
                validCandidates.Count > 0 || invalidCandidates.Count > 0
            );
        }

        if (
            validCandidates.Count > 1
            || (explicitMode && invalidCandidates.Count > 0)
        )
        {
            return new AssetResolution(
                null,
                validCandidates,
                invalidCandidates,
                true
            );
        }

        if (validCandidates.Count == 1)
        {
            return new AssetResolution(
                validCandidates[0],
                [],
                [],
                false
            );
        }

        return selectedAsset is null
            ? new AssetResolution(null, [], invalidCandidates, false)
            : new AssetResolution(
                selectedAsset,
                [],
                invalidCandidates,
                false
            );
    }

    private async Task AppendCandidateSummariesAsync(
        StringBuilder builder,
        IReadOnlyList<StudioAssetRef> candidates,
        CancellationToken cancellationToken
    )
    {
        builder.AppendLine("候选资产摘要:");
        foreach (StudioAssetRef candidate in candidates)
        {
            SkillChainSnapshot chain = await workspace.GetAssetChainAsync(
                candidate,
                depth: 1,
                cancellationToken
            );
            SkillChainNode? focus = chain.Nodes.FirstOrDefault(
                node => node.IsFocus
            );
            builder.AppendLine($"- {FormatAsset(candidate)}");
            if (focus is not null)
            {
                AppendFields(builder, focus.Fields, "    ");
            }
        }
    }

    private static string NamespaceFromAlias(string alias)
    {
        return alias.Trim().ToLowerInvariant() switch
        {
            "skill" or "技能" => "TbSkill",
            "item" or "道具" or "物品" => "TbItem",
            "effect" or "效果" => "TbEffect",
            "effectgroup" or "效果组" => "EffectGroup",
            "buff" => "TbBuff",
            "bullet" or "子弹" => "TbBullet",
            "search" or "搜索" => "TbSearch",
            "trap" or "机关" => "TbTrap",
            "equipment" or "装备" => "TbEquipment",
            _ => alias
        };
    }

    private static IReadOnlySet<string> ResolveNamespaceConstraints(
        string message
    )
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            KeyValuePair<string, string[]> hint in NamespaceHints
        )
        {
            if (
                hint.Value.Any(
                    alias => ContainsNamespaceHint(message, alias)
                )
            )
            {
                namespaces.Add(hint.Key);
            }
        }

        return namespaces;
    }

    private static bool ContainsNamespaceHint(
        string message,
        string alias
    )
    {
        if (
            alias.All(
                character =>
                    char.IsAsciiLetterOrDigit(character)
                    || character == ' '
            )
        )
        {
            return Regex.IsMatch(
                message,
                $@"(?i)(?<![a-z0-9]){Regex.Escape(alias)}(?![a-z0-9])"
            );
        }

        return message.Contains(alias, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExplicitTargetMention(string message)
    {
        return HyphenatedTargetRegex().IsMatch(message)
            || QuotedTargetRegex().IsMatch(message);
    }

    private static bool IsInsideSquareBrackets(string text, int index)
    {
        int open = text.LastIndexOf('[', index);
        int close = text.LastIndexOf(']', index);
        return open > close;
    }

    private static Regex ModificationValueRegex() =>
        ModificationValueRegexHolder.Value;

    private static bool ShouldResolveAssetMentions(string message)
    {
        return !SelfIntroductionRegex().IsMatch(message);
    }

    private static Regex SelfIntroductionRegex() =>
        SelfIntroductionRegexHolder.Value;

    private static readonly Lazy<Regex> SelfIntroductionRegexHolder = new(
        () => new Regex(
            @"(?i)(?:介绍|说说|说明|解释).{0,8}(?:你自己|你自己是谁|你自己做什么|你是什么|你能做什么|你的能力|系统能力|产品能力)",
            RegexOptions.Compiled
        )
    );

    private static readonly Lazy<Regex> ModificationValueRegexHolder = new(
        () => new Regex(
            @"(?i)(?:改成|改为|设为|设置成|调整到|调到|固定(?:为)?)\s*(?<id>\d{1,12})\s*(?:点|毫秒|秒|%|雷电|雷|火|冰|毒|暗|光|神圣|混乱|攻城)?",
            RegexOptions.Compiled
        )
    );

    private static void AppendFields(
        StringBuilder builder,
        IReadOnlyDictionary<string, IReadOnlyList<string>> fields,
        string indent
    )
    {
        foreach (
            KeyValuePair<string, IReadOnlyList<string>> field in fields
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        )
        {
            if (SkillAgentInstructions.IsHiddenLegacyField(field.Key))
            {
                continue;
            }

            IReadOnlyList<string> configuredValues = field
                .Value.Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            IReadOnlyList<string> values = configuredValues
                .Take(MaxFieldValues)
                .ToArray();
            if (values.Count == 0)
            {
                continue;
            }

            builder.AppendLine(
                $"{indent}{field.Key} = [{string.Join(", ", values)}]"
                    + (configuredValues.Count > MaxFieldValues
                        ? " ... (truncated)"
                        : "")
            );
        }
    }

    private void AppendSkillSemantics(StringBuilder builder)
    {
        builder.AppendLine("Skill 字段语义:");
        builder.AppendLine("- first_cd_time、cd_time、duration_pre、duration、duration_after、trigger_array 的单位为毫秒。");
        builder.AppendLine("- duration 表示 Skill 自身持续时间；trigger_array 的元素表示主要效果组的触发时点。");
        builder.AppendLine("- probability 表示技能触发概率，10000 = 100%；它不是命中率。");
        builder.AppendLine("- search_real_time 是布尔字段：0 = 非实时搜索，1 = 实时搜索。");
        builder.AppendLine("- skill_type 的值来自 ESkillType，必须按该枚举解释。");
        builder.AppendLine("SkillConfigPlan 可用技能字段:");
        foreach (
            WorkbookPatchRegistryField field in patchRegistry.EntityFields
                .Where(
                    item =>
                        item.SemanticName is { Length: > 0 }
                        && item.Path.StartsWith(
                            "Skill.",
                            StringComparison.OrdinalIgnoreCase
                        )
                )
                .OrderBy(item => item.Path, StringComparer.Ordinal)
        )
        {
            builder.AppendLine(
                $"- semanticName={field.SemanticName} | path={field.Path}"
                    + $" | kind={field.Kind}"
                    + (string.IsNullOrWhiteSpace(field.Unit)
                        ? ""
                        : $" | targetUnit={field.Unit}")
                    + (field.Scale is null
                        ? ""
                        : $" | scale={field.Scale}")
                    + (field.Aliases.Count == 0
                        ? ""
                        : $" | aliases={string.Join(",", field.Aliases)}")
            );
        }
        builder.AppendLine(
            "- ModifySkill.fields 的 key 必须使用上面的 semanticName；时间值必须显式填写 unit，由编译器转换为目标单位。"
        );
        builder.AppendLine(
            "- ModifySkill 只修改 Skill 根表字段；不得把技能根字段需求改写成 Effect、DamagePipeline 或 action_param 修改，除非用户明确要求修改这些子节点。"
        );
    }

    private static string FormatAsset(StudioAssetRef? asset)
    {
        return asset is null ? "<none>" : $"{asset.Namespace}:{asset.Id}";
    }

    [GeneratedRegex(
        @"(?i)(?:(?<namespace>Tb[A-Za-z]+|EffectGroup|ConditionGroup)\s*[:：]\s*|(?<alias>技能|道具|物品|效果组|机关|装备|子弹|搜索|Buff|Effect|Item|Skill|Equipment|Trap)\s*(?:ID|id|编号)?\s*[:：]?\s*)\[?\s*(?<id>\d{1,8})\s*\]?"
    )]
    private static partial Regex ExplicitAssetRegex();

    [GeneratedRegex(
        @"(?i)(?<id>\d{1,8})\s*(?:这个|该|此)?\s*(?<alias>技能|道具|物品|效果组|机关|装备|子弹|搜索|Buff|Effect|Item|Skill|Equipment|Trap)"
    )]
    private static partial Regex SuffixAssetRegex();

    [GeneratedRegex(@"(?<!\d)(?<id>\d{5,12})(?!\d)")]
    private static partial Regex BareAssetRegex();

    [GeneratedRegex(@"(?i)(?:action_param|param|参数|参数槽)")]
    private static partial Regex ParameterContextRegex();

    [GeneratedRegex(
        @"[\p{IsCJKUnifiedIdeographs}A-Za-z0-9_·]+(?:[-—][\p{IsCJKUnifiedIdeographs}A-Za-z0-9_·]+){1,}"
    )]
    private static partial Regex HyphenatedTargetRegex();

    [GeneratedRegex(
        @"[`“”](?<target>[^`“”\r\n]{2,80})[`“”]"
    )]
    private static partial Regex QuotedTargetRegex();
}

public sealed record AgentWorkspaceContext(
    string Text,
    StudioAssetRef? Asset,
    IReadOnlyList<StudioAssetRef> ValidCandidates,
    IReadOnlyList<StudioAssetRef> InvalidCandidates,
    bool RequiresClarification,
    IReadOnlyList<string>? MissingAssetIds = null
);

internal sealed record AssetResolution(
    StudioAssetRef? Asset,
    IReadOnlyList<StudioAssetRef> ValidCandidates,
    IReadOnlyList<StudioAssetRef> InvalidCandidates,
    bool RequiresClarification,
    IReadOnlyList<string>? MissingAssetIds = null
);
