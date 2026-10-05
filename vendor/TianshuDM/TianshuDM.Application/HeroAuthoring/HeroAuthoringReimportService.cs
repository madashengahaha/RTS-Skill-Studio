using TianshuDM.Application.GameData;
using TianshuDM.Application.Workspaces;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.HeroAuthoring;

public sealed record HeroAuthoringReimportResult(string Status, int TableCount);

public sealed class HeroAuthoringReimportService(
    IWorkspaceStore workspaceStore,
    IGameDataDraftStore gameDataStore,
    IWorkspaceImporter importer)
{
    public HeroAuthoringReimportResult Execute()
    {
        WorkspaceImportResult workspace = workspaceStore.ReadWorkspace()
            ?? throw new InvalidOperationException("当前没有已打开的游戏工程。");
        WorkspaceImportResult imported = importer.Import(workspace.UnityProjectRoot);
        gameDataStore.ReplaceGameDataImport(imported.GameData);
        return new HeroAuthoringReimportResult("completed", imported.GameData.Tables.Count);
    }
}
