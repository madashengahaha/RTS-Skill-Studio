using TianshuDM.Application.Workspaces;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Quests;

public sealed class RecurringQuestReleaseService(
    IWorkspaceStore workspaceStore,
    IRecurringQuestReleaseStore releaseStore,
    IWorkspaceImporter importer,
    IRecurringQuestReleasePipeline pipeline)
{
    public RecurringQuestReleaseResult Execute(
        bool publish,
        bool confirmLubanFailure = false)
    {
        WorkspaceImportResult workspace = workspaceStore.ReadWorkspace()
            ?? throw new InvalidOperationException("尚未打开项目工作区。");
        RecurringQuestReleaseResult result = pipeline.Execute(
            workspace,
            publish,
            confirmLubanFailure);
        WorkspaceImportResult refreshed = importer.Import(workspace.UnityProjectRoot);
        releaseStore.ReplaceRecurringQuestRelease(refreshed);
        return result;
    }
}
