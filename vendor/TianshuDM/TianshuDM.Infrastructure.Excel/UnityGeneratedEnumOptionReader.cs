using System.Globalization;
using System.Text.RegularExpressions;
using TianshuDM.Domain.GameData;

namespace TianshuDM.Infrastructure.Excel;

public static partial class UnityGeneratedEnumOptionReader
{
    public static IReadOnlyList<GameDataOption>? Read(string dataRoot, string enumName)
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || string.IsNullOrWhiteSpace(enumName))
        {
            return null;
        }

        string? generatedRoot = GeneratedConfigRoot(dataRoot);
        if (generatedRoot is null) return null;
        string path = Path.Combine(generatedRoot, $"{enumName}.cs");
        if (!File.Exists(path)) return null;

        string source = File.ReadAllText(path);
        if (!Regex.IsMatch(source, $@"\benum\s+{Regex.Escape(enumName)}\b", RegexOptions.CultureInvariant))
        {
            return null;
        }

        return EnumMemberRegex()
            .Matches(source)
            .Select(match => new GameDataOption(
                Summary(match.Groups["docs"].Value, match.Groups["name"].Value),
                Summary(match.Groups["docs"].Value, match.Groups["name"].Value),
                match.Groups["name"].Value,
                int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture)))
            .ToArray();
    }

    public static IReadOnlyList<GameDataOption> Merge(
        IReadOnlyList<GameDataOption> excelOptions,
        IReadOnlyList<GameDataOption> generatedOptions)
    {
        return generatedOptions
            .Select(generated =>
            {
                GameDataOption? excel = excelOptions.FirstOrDefault(option =>
                    option.LegacyValue == generated.LegacyValue
                    || string.Equals(option.Code, generated.Code, StringComparison.Ordinal));
                return new GameDataOption(
                    excel?.Value ?? generated.Value,
                    excel?.Label ?? generated.Label,
                    generated.Code,
                    generated.LegacyValue);
            })
            .ToArray();
    }

    private static string? GeneratedConfigRoot(string dataRoot)
    {
        DirectoryInfo? excelRoot = Directory.GetParent(Path.GetFullPath(dataRoot));
        DirectoryInfo? configRoot = excelRoot?.Parent;
        DirectoryInfo? assetsRoot = configRoot?.Parent;
        if (assetsRoot is null) return null;
        return Path.Combine(assetsRoot.FullName, "Scripts", "Model", "Generate", "Client", "Config");
    }

    private static string Summary(string documentation, string fallback)
    {
        string text = string.Join(
            ' ',
            documentation.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().TrimStart('/').Trim())
                .Where(line => line.Length > 0
                               && !line.StartsWith("<summary>", StringComparison.Ordinal)
                               && !line.StartsWith("</summary>", StringComparison.Ordinal)));
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    [GeneratedRegex(
        @"(?<docs>(?:\s*///[^\r\n]*(?:\r?\n|$))*)\s*(?<name>\w+)\s*=\s*(?<value>-?\d+)\s*,",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnumMemberRegex();
}
