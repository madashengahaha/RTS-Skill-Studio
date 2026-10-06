using System.Text;
using System.Text.RegularExpressions;
using RtsSkillStudio.Agent.Workspaces;

namespace RtsSkillStudio.Api.Workspaces;

public sealed partial class SkillAgentContextBuilder(
    SkillWorkspaceService workspace,
    ILogger<SkillAgentContextBuilder> logger
)
{
    private const int MaxHistoryNodes = 36;
    private const int MaxHistoryEdges = 60;
    private const int MaxFieldValues = 8;

    public async Task<AgentWorkspaceContext> BuildAsync(
        int? skillId,
        string userMessage,
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

            SkillResolution resolution = await ResolveSkillIdAsync(
                skillId,
                userMessage,
                cancellationToken
            );
            int? resolvedSkillId = resolution.SkillId;
            if (resolvedSkillId is null)
            {
                if (resolution.RequiresClarification)
                {
                    builder.AppendLine(
                        "当前请求没有自动选择技能。必须明确告知用户未绑定操作目标，不能沿用旧技能。"
                    );
                    if (resolution.ValidCandidates.Count > 0)
                    {
                        builder.AppendLine(
                            "有效候选技能: "
                                + string.Join(", ", resolution.ValidCandidates)
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
                            "无效或未找到的技能 ID: "
                                + string.Join(
                                    ", ",
                                    resolution.InvalidCandidates
                                )
                        );
                    }
                }
                else
                {
                    builder.AppendLine(
                        "当前没有选择技能。涉及具体技能的问题必须先确定目标技能 ID。"
                    );
                }
                return new AgentWorkspaceContext(
                    builder.ToString(),
                    null,
                    resolution.ValidCandidates,
                    resolution.InvalidCandidates,
                    resolution.RequiresClarification
                );
            }

            SkillChainSnapshot chain = await workspace.GetSkillChainAsync(
                resolvedSkillId.Value,
                depth: 5,
                cancellationToken
            );
            SkillChainNode? focus = chain.Nodes.FirstOrDefault(
                node => node.IsFocus
            );
            builder.AppendLine($"已选择技能: TbSkill:{chain.SkillId}");

            if (focus is not null)
            {
                builder.AppendLine("技能主记录字段:");
                AppendFields(builder, focus.Fields, "  ");
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
                    resolvedSkillId.Value,
                    40,
                    cancellationToken
                );
            builder.AppendLine("入向引用（哪些来源使用当前技能）:");
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
                            + $" -> TbSkill:{resolvedSkillId.Value}"
                            + $" | {reference.Relationship}"
                            + (string.IsNullOrWhiteSpace(reference.SourceField)
                                ? ""
                                : $" | sourceField={reference.SourceField}")
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
                resolvedSkillId,
                [],
                [],
                false
            );
        }
        catch (KeyNotFoundException)
        {
            return new AgentWorkspaceContext(
                $"未找到技能 TbSkill:{skillId}。",
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
                "Failed to build Agent workspace context for skill {SkillId}.",
                skillId
            );
            return new AgentWorkspaceContext(
                "当前无法读取技能工作区上下文，必须明确告知用户证据不可用。",
                null,
                [],
                [],
                false
            );
        }
    }

    private async Task<SkillResolution> ResolveSkillIdAsync(
        int? selectedSkillId,
        string userMessage,
        CancellationToken cancellationToken
    )
    {
        bool parameterContext = ParameterContextRegex().IsMatch(userMessage);
        var candidates = new List<int>();
        candidates.AddRange(
            ToCandidates(ExplicitSkillIdRegex().Matches(userMessage))
        );

        if (candidates.Count == 0)
        {
            candidates.AddRange(
                ToCandidates(
                    BareSkillIdRegex().Matches(userMessage)
                        .Where(
                            match =>
                                !parameterContext
                                || !IsInsideSquareBrackets(
                                    userMessage,
                                    match.Index
                                )
                        )
                )
            );
        }

        candidates = candidates.Distinct().ToList();
        if (candidates.Count == 0)
        {
            return selectedSkillId is null
                ? new SkillResolution(null, [], [], false)
                : new SkillResolution(
                    selectedSkillId,
                    [],
                    [],
                    false
                );
        }

        var validCandidates = new List<int>();
        var invalidCandidates = new List<int>();
        foreach (int candidate in candidates)
        {
            try
            {
                await workspace.GetSkillChainAsync(
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
        }

        validCandidates = validCandidates.Distinct().ToList();
        invalidCandidates = invalidCandidates.Distinct().ToList();
        if (
            validCandidates.Count > 1
            || invalidCandidates.Count > 0
        )
        {
            return new SkillResolution(
                null,
                validCandidates,
                invalidCandidates,
                true
            );
        }

        if (validCandidates.Count == 1)
        {
            return new SkillResolution(
                validCandidates[0],
                [],
                [],
                false
            );
        }

        return selectedSkillId is null
            ? new SkillResolution(null, [], invalidCandidates, false)
            : new SkillResolution(
                selectedSkillId,
                [],
                invalidCandidates,
                false
            );
    }

    private static IEnumerable<int> ToCandidates(
        IEnumerable<Match> matches
    )
    {
        foreach (Match match in matches)
        {
            string value = match.Groups["id"].Value;
            if (int.TryParse(value, out int candidate))
            {
                yield return candidate;
            }
        }
    }

    private static bool IsInsideSquareBrackets(string text, int index)
    {
        int open = text.LastIndexOf('[', index);
        int close = text.LastIndexOf(']', index);
        return open > close;
    }

    private async Task AppendCandidateSummariesAsync(
        StringBuilder builder,
        IReadOnlyList<int> candidates,
        CancellationToken cancellationToken
    )
    {
        builder.AppendLine("候选技能摘要:");
        foreach (int candidate in candidates)
        {
            SkillChainSnapshot chain = await workspace.GetSkillChainAsync(
                candidate,
                depth: 1,
                cancellationToken
            );
            SkillChainNode? focus = chain.Nodes.FirstOrDefault(
                node => node.IsFocus
            );
            builder.AppendLine($"- TbSkill:{candidate}");
            if (focus is not null)
            {
                AppendFields(builder, focus.Fields, "    ");
            }
        }
    }

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

    [GeneratedRegex(
        @"(?i)(?:TbSkill\s*[:：]\s*|技能(?:ID|id|编号)?\s*[:：]?\s*)\[?\s*(?<id>\d{1,8})\s*\]?"
    )]
    private static partial Regex ExplicitSkillIdRegex();

    [GeneratedRegex(@"(?<!\d)(?<id>\d{5,8})(?!\d)")]
    private static partial Regex BareSkillIdRegex();

    [GeneratedRegex(@"(?i)(?:action_param|param|参数|参数槽)")]
    private static partial Regex ParameterContextRegex();
}

public sealed record AgentWorkspaceContext(
    string Text,
    int? SkillId,
    IReadOnlyList<int> ValidCandidates,
    IReadOnlyList<int> InvalidCandidates,
    bool RequiresClarification
);

internal sealed record SkillResolution(
    int? SkillId,
    IReadOnlyList<int> ValidCandidates,
    IReadOnlyList<int> InvalidCandidates,
    bool RequiresClarification
);
