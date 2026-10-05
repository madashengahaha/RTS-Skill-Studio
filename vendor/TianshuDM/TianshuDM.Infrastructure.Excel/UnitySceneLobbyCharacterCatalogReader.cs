using System.Globalization;
using System.Text.RegularExpressions;
using TianshuDM.Application.Quests;
using TianshuDM.Domain.Quests;

namespace TianshuDM.Infrastructure.Excel;

public sealed partial class UnitySceneLobbyCharacterCatalogReader : ILobbyCharacterCatalogReader
{
    private const string LobbyNpcScriptGuid = "2111c24c8861f8048a6e94863d75ddc3";

    public IReadOnlyList<LobbyCharacterReference> Read(string unityProjectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unityProjectRoot);
        string root = Path.GetFullPath(unityProjectRoot);
        string assetsRoot = Path.Combine(root, "Unity", "Assets");
        if (!Directory.Exists(assetsRoot))
        {
            throw new DirectoryNotFoundException($"找不到 Unity 资源目录：{assetsRoot}");
        }

        var placements = new List<CharacterPlacement>();
        foreach (string scenePath in Directory.EnumerateFiles(
                     assetsRoot,
                     "*.unity",
                     SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(root, scenePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            string content = File.ReadAllText(scenePath);
            foreach (Match block in MonoBehaviourBlockRegex().Matches(content))
            {
                string value = block.Value;
                if (!value.Contains($"guid: {LobbyNpcScriptGuid}", StringComparison.Ordinal))
                {
                    continue;
                }

                Match id = NpcIdRegex().Match(value);
                Match name = NpcNameRegex().Match(value);
                Match modelId = ModelIdRegex().Match(value);
                if (!id.Success || !name.Success || !modelId.Success)
                {
                    continue;
                }

                placements.Add(
                    new CharacterPlacement(
                        int.Parse(id.Groups[1].Value, CultureInfo.InvariantCulture),
                        DecodeYamlString(name.Groups[1].Value),
                        int.Parse(modelId.Groups[1].Value, CultureInfo.InvariantCulture),
                        relativePath));
            }
        }

        return placements
            .GroupBy(placement => placement.Id)
            .OrderBy(group => group.Key)
            .Select(
                group =>
                {
                    CharacterPlacement first = group
                        .OrderBy(placement => placement.ScenePath, StringComparer.Ordinal)
                        .First();
                    return new LobbyCharacterReference(
                        group.Key,
                        first.Name,
                        first.ModelId,
                        group.Select(placement => placement.ScenePath)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(path => path, StringComparer.Ordinal)
                            .ToArray());
                })
            .ToArray();
    }

    private static string DecodeYamlString(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        try
        {
            return Regex.Unescape(trimmed);
        }
        catch (ArgumentException)
        {
            return trimmed;
        }
    }

    [GeneratedRegex("(?ms)^--- !u!114 .*?(?=^--- !u!|\\z)")]
    private static partial Regex MonoBehaviourBlockRegex();

    [GeneratedRegex("(?m)^\\s*npcId:\\s*(-?\\d+)\\s*$")]
    private static partial Regex NpcIdRegex();

    [GeneratedRegex("(?m)^\\s*npcName:\\s*(.*?)\\s*$")]
    private static partial Regex NpcNameRegex();

    [GeneratedRegex("(?m)^\\s*modelId:\\s*(-?\\d+)\\s*$")]
    private static partial Regex ModelIdRegex();

    private sealed record CharacterPlacement(int Id, string Name, int ModelId, string ScenePath);
}
