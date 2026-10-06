using System.Text.RegularExpressions;

namespace RtsSkillStudio.Agent.Llm;

public static class AgentIntentDetector
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

    public static bool IsConfigurationRequest(string message)
    {
        return Classify(message) == AgentIntentKind.Configuration;
    }

    public static AgentIntentKind Classify(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return AgentIntentKind.Query;
        }

        string normalized = message.Trim();
        if (AssignmentRegex().IsMatch(normalized))
        {
            return AgentIntentKind.Configuration;
        }

        if (
            StrongMutationPhrases.Any(
                phrase => normalized.Contains(phrase, StringComparison.Ordinal)
            )
        )
        {
            return AgentIntentKind.Configuration;
        }

        bool looksLikeQuestion = QuestionMarkers.Any(
            marker => normalized.Contains(marker, StringComparison.Ordinal)
        );
        if (looksLikeQuestion)
        {
            return AgentIntentKind.Query;
        }

        if (
            WeakMutationVerbs.Any(
                verb => normalized.Contains(verb, StringComparison.Ordinal)
            )
        )
        {
            return AgentIntentKind.Configuration;
        }

        return IncompleteMutationRegex().IsMatch(normalized)
            ? AgentIntentKind.Ambiguous
            : AgentIntentKind.Query;
    }

    private static Regex AssignmentRegex()
    {
        return AssignmentRegexHolder.Value;
    }

    private static readonly Lazy<Regex> AssignmentRegexHolder = new(
        () =>
            new Regex(
                @"(?i)(?:set\s+(?:the\s+)?(?:cd_time|cooldown|duration|damage)\s+to\s+\d+(?:\.\d+)?|(?:cd_time|cooldown|duration|damage|冷却|持续时间|伤害)\s*(?:=|:|为|改成|改为|设为|调到)?\s*\d+(?:\.\d+)?\s*(?:毫秒|秒|%|点)?)",
                RegexOptions.Compiled
            )
    );

    private static Regex IncompleteMutationRegex()
    {
        return IncompleteMutationRegexHolder.Value;
    }

    private static readonly Lazy<Regex> IncompleteMutationRegexHolder = new(
        () =>
            new Regex(
                @"(?i)(?:^|[\s,，。])(?:改|修改|调整|变更|设置)(?:[\s,，。]|$)",
                RegexOptions.Compiled
            )
    );
}

public enum AgentIntentKind
{
    Query,
    Configuration,
    Ambiguous
}
