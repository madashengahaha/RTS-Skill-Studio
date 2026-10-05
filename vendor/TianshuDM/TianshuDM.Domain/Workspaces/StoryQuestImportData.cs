using TianshuDM.Domain.Quests;

namespace TianshuDM.Domain.Workspaces;

public sealed record StoryQuestImportData(
    string QuestWorkbookPath,
    string StepWorkbookPath,
    string TurnWorkbookPath,
    string QuestSourceHash,
    string StepSourceHash,
    string TurnSourceHash,
    StoryQuestDataset Dataset);
