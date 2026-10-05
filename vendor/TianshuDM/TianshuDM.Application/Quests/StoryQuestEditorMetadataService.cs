using TianshuDM.Application.GameData;
using TianshuDM.Application.Workspaces;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.Quests;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Quests;

public sealed class StoryQuestEditorMetadataService(
    IWorkspaceStore workspaceStore,
    IGameDataDraftStore gameDataStore,
    ILobbyCharacterCatalogReader characterReader,
    IWorldMapCatalogReader mapReader)
{
    private static readonly HashSet<string> CombatTableKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "hero",
            "soldier",
            "building",
            "block",
            "trap",
        };

    public StoryQuestEditorMetadata GetMetadata()
    {
        WorkspaceImportResult workspace = workspaceStore.ReadWorkspace()
            ?? throw new InvalidOperationException("No workspace is open.");
        GameDataCatalog catalog = gameDataStore.ReadGameDataCatalog()
            ?? GameDataCatalog.Empty;
        StoryQuestReferenceTable[] tables = catalog.Tables
            .Select(
                table => new StoryQuestReferenceTable(
                    table.Key,
                    table.DisplayName,
                    table.Records
                        .OrderBy(record => record.Id)
                        .Select(record => ToOption(table, record))
                        .ToArray()))
            .ToArray();

        return new StoryQuestEditorMetadata(
            characterReader.Read(workspace.UnityProjectRoot),
            mapReader.Read(workspace.UnityProjectRoot),
            tables.Where(table => CombatTableKeys.Contains(table.TableKey))
                .SelectMany(table => table.Options)
                .OrderBy(option => option.Id)
                .ThenBy(option => option.TableKey, StringComparer.Ordinal)
                .ToArray(),
            tables);
    }

    private static StoryQuestReferenceOption ToOption(
        GameDataTable table,
        GameDataRecord record)
    {
        string name = PreferredName(record) ?? $"{table.DisplayName} {record.Id}";
        return new StoryQuestReferenceOption(
            record.Id,
            name,
            table.Key,
            table.DisplayName);
    }

    private static string? PreferredName(GameDataRecord record)
    {
        foreach (string key in new[] { "Name", "name", "DisplayName", "display_name" })
        {
            if (record.Fields.TryGetValue(key, out IReadOnlyList<string>? values)
                && values.Count > 0
                && !string.IsNullOrWhiteSpace(values[0]))
            {
                return values[0];
            }
        }

        return record.Fields
            .Where(pair => pair.Key.StartsWith("__remark", StringComparison.Ordinal))
            .SelectMany(pair => pair.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
