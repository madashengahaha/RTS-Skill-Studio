using TianshuDM.Domain.GameData;
using TianshuDM.Domain.Quests;

namespace TianshuDM.Domain.Workspaces;

public sealed record WorkspaceImportResult(
    string UnityProjectRoot,
    string DailyQuestWorkbookPath,
    string SourceHash,
    IReadOnlyList<DailyQuestDefinition> DailyQuests)
{
    public string WeeklyQuestWorkbookPath { get; init; } = string.Empty;

    public string WeeklyQuestSourceHash { get; init; } = string.Empty;

    public IReadOnlyList<WeeklyQuestDefinition> WeeklyQuests { get; init; } = [];

    public string QuestChestRewardWorkbookPath { get; init; } = string.Empty;

    public string QuestChestRewardSourceHash { get; init; } = string.Empty;

    public IReadOnlyList<QuestChestRewardDefinition> QuestChestRewards { get; init; } = [];

    public StoryQuestImportData? StoryQuest { get; init; }

    public GameDataCatalog GameData { get; init; } = GameDataCatalog.Empty;
}
