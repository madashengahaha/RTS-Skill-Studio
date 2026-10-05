using TianshuDM.Application.Workspaces;
using TianshuDM.Domain.Quests;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Quests;

public sealed class StoryQuestReleaseService(
    IWorkspaceStore workspaceStore,
    IStoryQuestDraftStore draftStore,
    IWorkspaceImporter importer,
    IStoryQuestReleasePipeline pipeline)
{
    public StoryQuestReleaseResult Execute(
        bool publish,
        bool confirmLubanFailure = false)
    {
        WorkspaceImportResult workspace = workspaceStore.ReadWorkspace()
            ?? throw new InvalidOperationException("No workspace is open.");
        StoryQuestDataset dataset = draftStore.ReadStoryQuestDataset()
            ?? throw new InvalidOperationException("No StoryQuest data is loaded.");
        StoryQuestReleaseResult result = pipeline.Execute(
            workspace,
            dataset,
            publish,
            confirmLubanFailure);
        WorkspaceImportResult refreshed = importer.Import(workspace.UnityProjectRoot);
        StoryQuestImportData refreshedStory = refreshed.StoryQuest
            ?? throw new InvalidOperationException("StoryQuest data disappeared after release.");
        draftStore.ReplaceStoryQuestImport(refreshedStory);
        return result;
    }
}
