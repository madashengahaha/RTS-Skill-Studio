using System.Globalization;
using System.Text.RegularExpressions;
using TianshuDM.Application.HeroAuthoring;
using TianshuDM.Domain.HeroAuthoring;

namespace TianshuDM.Infrastructure.Excel.HeroAuthoring;

public sealed partial class UnityCodeBindingScanner : IHeroAuthoringCodeBindingSource
{
    public HeroAuthoringCodeBindings Scan(string unityProjectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unityProjectRoot);
        string scriptsRoot = Path.Combine(unityProjectRoot, "Unity", "Assets", "Scripts");
        string generatedRoot = Path.Combine(scriptsRoot, "Model", "Generate", "Client", "Config");
        if (!Directory.Exists(scriptsRoot) || !Directory.Exists(generatedRoot))
        {
            throw new DirectoryNotFoundException($"找不到 Unity 脚本目录：{scriptsRoot}");
        }

        string[] sourceFiles = Directory.GetFiles(scriptsRoot, "*.cs", SearchOption.AllDirectories);
        return new HeroAuthoringCodeBindings(
            ReadEnum(Path.Combine(generatedRoot, "EffectActionType.cs")),
            ReadBindings(sourceFiles, "EffectExecutor", "EffectActionType", unityProjectRoot),
            ReadEnum(Path.Combine(generatedRoot, "EConditionType.cs")),
            ReadBindings(sourceFiles, "ConditionHandler", "EConditionType", unityProjectRoot),
            ReadEnum(Path.Combine(generatedRoot, "DamageStageType.cs")),
            ReadBindings(sourceFiles, "DamageStage", "DamageStageType", unityProjectRoot));
    }

    private static HeroAuthoringEnumValue[] ReadEnum(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到 Unity 生成枚举。", path);
        }

        return EnumEntryRegex().Matches(File.ReadAllText(path))
            .Select(match => new HeroAuthoringEnumValue(
                match.Groups[1].Value,
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)))
            .Where(value => value.Key != "None")
            .ToArray();
    }

    private static HeroAuthoringCodeBinding[] ReadBindings(
        IEnumerable<string> files,
        string attributeName,
        string enumName,
        string root)
    {
        var result = new List<HeroAuthoringCodeBinding>();
        var regex = new Regex(
            $@"\[{Regex.Escape(attributeName)}\s*\(\s*{Regex.Escape(enumName)}\.(\w+)\s*\)\s*\]",
            RegexOptions.CultureInvariant);
        foreach (string file in files)
        {
            string content = File.ReadAllText(file);
            foreach (Match match in regex.Matches(content))
            {
                result.Add(
                    new HeroAuthoringCodeBinding(
                        match.Groups[1].Value,
                        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')));
            }
        }

        return result
            .GroupBy(binding => binding.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(binding => binding.Key, StringComparer.Ordinal)
            .ToArray();
    }

    [GeneratedRegex(@"\b(\w+)\s*=\s*(-?\d+)\s*,", RegexOptions.CultureInvariant)]
    private static partial Regex EnumEntryRegex();
}
