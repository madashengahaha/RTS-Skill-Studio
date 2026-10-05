using TianshuDM.Domain.Quests;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Quests;

public interface IStoryQuestReleasePipeline
{
    StoryQuestReleaseResult Execute(
        WorkspaceImportResult workspace,
        StoryQuestDataset dataset,
        bool publish,
        bool confirmLubanFailure = false);
}

public sealed record StoryQuestReleaseResult(
    string Status,
    string BackupPath,
    string JournalPath,
    string ValidationOutput)
{
    public bool ValidationOverridden { get; init; }

    public string ValidationStatus { get; init; } = "passed";
}
