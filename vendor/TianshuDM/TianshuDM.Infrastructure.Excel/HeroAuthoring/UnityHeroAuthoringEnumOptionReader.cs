using System.Globalization;
using System.Xml.Linq;
using TianshuDM.Application.HeroAuthoring;
using TianshuDM.Domain.HeroAuthoring;

namespace TianshuDM.Infrastructure.Excel.HeroAuthoring;

public sealed class UnityHeroAuthoringEnumOptionReader : IHeroAuthoringEnumOptionSource
{
    private static readonly IReadOnlyDictionary<string, HeroAuthoringEnumDefinition> RuntimeDefinitions =
        new Dictionary<string, HeroAuthoringEnumDefinition>(StringComparer.Ordinal)
        {
            ["ECompareType"] = Definition("ECompareType", false,
                ("Equal", "等于", 0), ("Greater", "大于", 1), ("Less", "小于", 2),
                ("GreaterOrEqual", "大于等于", 3), ("LessOrEqual", "小于等于", 4)),
            ["EGroupCompletedType"] = Definition("EGroupCompletedType", false,
                ("None", "无", 0), ("All", "满足所有条件", 1), ("Any", "满足任一条件", 2)),
            ["EKnockbackDirection"] = Definition("EKnockbackDirection", false,
                ("AwayFromCaster", "远离施法者", 0), ("TowardCaster", "朝向施法者", 1),
                ("CasterForward", "施法者前方", 2), ("FixedWorldDirection", "固定世界方向", 3)),
            ["EffectKillTarget"] = Definition("EffectKillTarget", false,
                ("Owner", "施法者", 1), ("Target", "目标", 2)),
            ["EffectSourceUnit"] = Definition("EffectSourceUnit", false,
                ("Owner", "施法者", 0), ("Target", "目标", 1), ("Carrier", "载体", 2)),
            ["EBuffApplyTarget"] = Definition("EBuffApplyTarget", false,
                ("Target", "当前效果目标", 0), ("Caster", "施法者", 1)),
            ["ShareHostilityMode"] = Definition("ShareHostilityMode", false,
                ("Target", "仅当前目标", 0), ("TargetAndAlliesInRadius", "目标及范围内同阵营", 1)),
            ["TeamRelation"] = Definition("TeamRelation", false,
                ("Self", "自己", 0), ("Ally", "友方", 1), ("Enemy", "敌方", 2), ("Neutral", "中立", 3)),
        };

    public IReadOnlyDictionary<string, HeroAuthoringEnumDefinition> Read(
        string unityProjectRoot,
        IReadOnlyCollection<string> enumNames)
    {
        HashSet<string> requested = enumNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        var definitions = RuntimeDefinitions
            .Where(pair => requested.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        foreach (string path in CandidateDefinitionFiles(unityProjectRoot))
        {
            if (!File.Exists(path)) continue;
            XDocument document = XDocument.Load(path, LoadOptions.None);
            foreach (XElement enumElement in document.Descendants("enum"))
            {
                string name = enumElement.Attribute("name")?.Value ?? string.Empty;
                if (!requested.Contains(name)) continue;
                bool flags = string.Equals(enumElement.Attribute("flags")?.Value, "TRUE", StringComparison.OrdinalIgnoreCase);
                HeroAuthoringEnumOptionDefinition[] options = enumElement.Elements("var")
                    .Select(ToOption)
                    .Where(option => option is not null)
                    .Cast<HeroAuthoringEnumOptionDefinition>()
                    .OrderBy(option => option.Value)
                    .ToArray();
                if (options.Length > 0) definitions[name] = new HeroAuthoringEnumDefinition(name, flags, options);
            }
        }
        return definitions;
    }

    private static IEnumerable<string> CandidateDefinitionFiles(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) yield break;
        yield return Path.Combine(root, "Unity", "Assets", "Config", "Excel", "Defines", "builtin.xml");
        yield return Path.Combine(root, "Assets", "Config", "Excel", "Defines", "builtin.xml");
    }

    private static HeroAuthoringEnumOptionDefinition? ToOption(XElement element)
    {
        string code = element.Attribute("name")?.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code)
            || !int.TryParse(element.Attribute("value")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return null;
        }
        string alias = element.Attribute("alias")?.Value ?? string.Empty;
        return new HeroAuthoringEnumOptionDefinition(code, string.IsNullOrWhiteSpace(alias) ? code : alias, value);
    }

    private static HeroAuthoringEnumDefinition Definition(
        string name,
        bool flags,
        params (string Code, string Label, int Value)[] options) =>
        new(name, flags, options.Select(option =>
            new HeroAuthoringEnumOptionDefinition(option.Code, option.Label, option.Value)).ToArray());
}
