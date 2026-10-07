using System.Text.RegularExpressions;

namespace RtsSkillStudio.Agent.Llm;

public enum AgentIntentKind
{
    Query,
    Configuration,
    Create,
    Ambiguous,
    Unsupported
}

public sealed record AgentRoute(
    AgentIntentKind Kind,
    bool ExpectsPlan,
    IReadOnlyList<string> SuggestedTools,
    string Reason
);

public static partial class AgentIntentRouter
{
    private static readonly string[] StrongMutationPhrases =
    [
        "改成",
        "改为",
        "修改为",
        "调整为",
        "调到",
        "降至",
        "降低到",
        "降到",
        "提升到",
        "提高到",
        "增加到",
        "设置成",
        "设置为",
        "设为",
        "替换为"
    ];

    private static readonly string[] WeakMutationVerbs =
    [
        "修改",
        "调整",
        "设置",
        "增加",
        "新增",
        "添加",
        "加一个",
        "创建",
        "新建",
        "删除",
        "移除",
        "去掉",
        "替换",
        "提升",
        "降低"
    ];

    private static readonly string[] CreatePhrases =
    [
        "创建技能",
        "新建技能",
        "从零创建",
        "从零做",
        "做一个技能",
        "做一个新技能"
    ];

    private static readonly string[] QuestionMarkers =
    [
        "多少",
        "是多少",
        "是什么",
        "怎么",
        "如何",
        "为什么",
        "哪些",
        "哪个",
        "有没有",
        "是否",
        "会有什么影响",
        "有什么影响",
        "看下",
        "查看",
        "查询",
        "介绍",
        "吗",
        "么"
    ];

    public static AgentRoute Route(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new AgentRoute(
                AgentIntentKind.Ambiguous,
                false,
                [],
                "请求为空。"
            );
        }

        string normalized = message.Trim();
        string unsupportedProbe = NegatedUnsupportedRegex().Replace(
            normalized,
            " "
        );
        if (UnsupportedRegex().IsMatch(unsupportedProbe))
        {
            return new AgentRoute(
                AgentIntentKind.Unsupported,
                false,
                [],
                "请求绕过受控配置流程或超出当前能力边界。"
            );
        }

        if (ParameterQuestionRegex().IsMatch(normalized))
        {
            return new AgentRoute(
                AgentIntentKind.Query,
                false,
                [
                    "resolve_asset",
                    "get_graph",
                    "get_capability_context",
                    "explain_execution_chain"
                ],
                "请求解释参数、字段或机制语义，必须依据能力契约回答。"
            );
        }

        if (CreatePhrases.Any(
                phrase => normalized.Contains(phrase, StringComparison.Ordinal)
            ))
        {
            return new AgentRoute(
                AgentIntentKind.Create,
                true,
                ["get_capability_context", "search_similar_skills", "resolve_asset", "get_graph"],
                "请求从零创建技能。"
            );
        }

        if (
            AssignmentRegex().IsMatch(normalized)
            || StrongMutationPhrases.Any(
                phrase => normalized.Contains(phrase, StringComparison.Ordinal)
            )
        )
        {
            return new AgentRoute(
                AgentIntentKind.Configuration,
                true,
                ["resolve_asset", "get_graph", "get_capability_context"],
                "请求修改或配置现有技能资产。"
            );
        }

        if (IncompleteMutationRegex().IsMatch(normalized))
        {
            return new AgentRoute(
                AgentIntentKind.Ambiguous,
                false,
                ["resolve_asset", "get_graph"],
                "请求缺少明确目标、字段或值。"
            );
        }

        if (
            !QuestionMarkers.Any(
                marker => normalized.Contains(marker, StringComparison.Ordinal)
            )
            && WeakMutationVerbs.Any(
                verb => normalized.Contains(verb, StringComparison.Ordinal)
            )
        )
        {
            return new AgentRoute(
                AgentIntentKind.Configuration,
                true,
                ["resolve_asset", "get_graph", "get_capability_context"],
                "请求修改或配置现有技能资产。"
            );
        }

        return new AgentRoute(
            AgentIntentKind.Query,
            false,
            [
                "resolve_asset",
                "get_graph",
                "get_capability_context",
                "explain_execution_chain"
            ],
            "请求查询、解释或审计现有技能资产。"
        );
    }

    public static bool IsConfigurationRequest(string message)
    {
        return Route(message).ExpectsPlan;
    }

    private static Regex AssignmentRegex() => AssignmentRegexHolder.Value;

    private static readonly Lazy<Regex> AssignmentRegexHolder = new(
        () => new Regex(
            @"(?i)(?:set\s+(?:the\s+)?(?:cd_time|cooldown|duration|damage)\s+to\s+\d+(?:\.\d+)?|(?:cd_time|cooldown|duration|damage|冷却|持续时间|伤害)\s*(?:=|:|为|改成|改为|设为|调到)?\s*\d+(?:\.\d+)?\s*(?:毫秒|秒|%|点)?)",
            RegexOptions.Compiled
        )
    );

    private static Regex IncompleteMutationRegex() =>
        IncompleteMutationRegexHolder.Value;

    private static readonly Lazy<Regex> IncompleteMutationRegexHolder = new(
        () => new Regex(
            @"(?i)(?:^|[\s,，。])(?:改|修改|调整|变更|设置)(?:[\s,，。]|$)|(?:改|修改|调整|变更|设置)(?:一下|下|点)(?:[\s,，。]|$)",
            RegexOptions.Compiled
        )
    );

    private static Regex UnsupportedRegex() => UnsupportedRegexHolder.Value;

    private static Regex ParameterQuestionRegex() =>
        ParameterQuestionRegexHolder.Value;

    private static Regex NegatedUnsupportedRegex() =>
        NegatedUnsupportedRegexHolder.Value;

    private static readonly Lazy<Regex> NegatedUnsupportedRegexHolder = new(
        () => new Regex(
            @"(?i)(?:不要|不得|不能|禁止|别)\s*(?:绕过校验|直接发布|直接写|写入|操作\s*sql|写\s*excel|修改\s*excel)",
            RegexOptions.Compiled
        )
    );

    private static readonly Lazy<Regex> UnsupportedRegexHolder = new(
        () => new Regex(
            @"(?i)(?:excel\s*r?\d+\s*c?\d+|单元格|行列坐标|sql|绕过校验|直接写|直接发布|批量\s*\d*|588\s*个)",
            RegexOptions.Compiled
        )
    );

    private static readonly Lazy<Regex> ParameterQuestionRegexHolder = new(
        () => new Regex(
            @"(?i)(?:action_param|参数|param).{0,40}(?:代表什么|什么意思|什么含义|含义是什么|定义是什么|清楚吗|是什么意思)|(?:代表什么|什么意思|什么含义|你清楚吗|清楚吗|难道不是)",
            RegexOptions.Compiled
        )
    );
}
