using TianshuDM.Application.Workspaces;
using TianshuDM.Application.HeroAuthoring;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.GameData;

public sealed class GameDataReleaseService(
    IWorkspaceStore workspaceStore,
    IGameDataDraftStore draftStore,
    IWorkspaceImporter importer,
    IGameDataReleasePipeline pipeline,
    HeroAuthoringDraftValidator? heroAuthoringValidator = null,
    IGameDataSourceIntegrityValidator? sourceIntegrityValidator = null)
{
    public GameDataReleaseResult Execute(
        bool publish,
        bool confirmLubanFailure = false)
    {
        WorkspaceImportResult workspace = workspaceStore.ReadWorkspace()
            ?? throw new InvalidOperationException("尚未打开项目工作区。");
        GameDataCatalog catalog = draftStore.ReadGameDataCatalog()
            ?? throw new InvalidOperationException("尚未加载英雄和单位数据。");
        IReadOnlyList<string> dirtyTableKeys = draftStore.ReadDirtyGameDataTableKeys();
        sourceIntegrityValidator?.Verify(catalog);
        if (heroAuthoringValidator is not null)
        {
            WorkspaceImportResult baseline = importer.Import(workspace.UnityProjectRoot);
            heroAuthoringValidator.EnsureNoNewIssues(baseline.GameData, catalog, dirtyTableKeys);
        }
        GameDataReleaseResult result = pipeline.Execute(
            workspace,
            catalog,
            dirtyTableKeys,
            publish,
            confirmLubanFailure);
        WorkspaceImportResult refreshed = importer.Import(workspace.UnityProjectRoot);
        draftStore.ReplaceGameDataImport(refreshed.GameData);
        return result;
    }
}
