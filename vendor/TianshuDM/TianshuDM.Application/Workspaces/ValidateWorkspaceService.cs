using TianshuDM.Domain.Workspaces;
using TianshuDM.Application.GameData;

namespace TianshuDM.Application.Workspaces;

public sealed class ValidateWorkspaceService(
    IWorkspaceStore store,
    IWorkspaceSourceValidator sourceValidator,
    IWorkspaceImporter importer,
    IRecurringQuestImportStore recurringQuestImportStore,
    IGameDataDraftStore? gameDataDraftStore = null)
{
    public WorkspaceImportResult? Validate(string unityProjectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unityProjectRoot);

        WorkspaceImportResult? workspace = store.ReadWorkspace();
        if (workspace is null || !PathsEqual(workspace.UnityProjectRoot, unityProjectRoot))
        {
            return null;
        }

        sourceValidator.Validate(workspace);

        bool needsRecurringQuestImport = NeedsRecurringQuestImport(workspace);
        bool needsGameDataImport = workspace.GameData.Tables.Count == 0 && gameDataDraftStore is not null;
        if (needsRecurringQuestImport || needsGameDataImport)
        {
            WorkspaceImportResult imported = importer.Import(unityProjectRoot);
            if (needsRecurringQuestImport)
            {
                recurringQuestImportStore.ReplaceRecurringQuestImport(imported);
            }

            if (needsGameDataImport)
            {
                gameDataDraftStore!.ReplaceGameDataImport(imported.GameData);
            }

            workspace = store.ReadWorkspace()
                ?? throw new InvalidOperationException(
                    "补充导入英雄、单位或周期任务后，当前工作副本丢失。");
        }

        return workspace;
    }

    private static bool NeedsRecurringQuestImport(WorkspaceImportResult workspace)
    {
        return string.IsNullOrWhiteSpace(workspace.WeeklyQuestWorkbookPath)
               || string.IsNullOrWhiteSpace(workspace.WeeklyQuestSourceHash)
               || string.IsNullOrWhiteSpace(workspace.QuestChestRewardWorkbookPath)
               || string.IsNullOrWhiteSpace(workspace.QuestChestRewardSourceHash);
    }

    private static bool PathsEqual(string left, string right)
    {
        string normalizedLeft = NormalizePath(left);
        string normalizedRight = NormalizePath(right);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalizedLeft, normalizedRight, comparison);
    }

    private static string NormalizePath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
