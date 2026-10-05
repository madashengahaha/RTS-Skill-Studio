namespace TianshuDM.Contract.Workspaces;

public sealed record WorkspaceStateResponse(
    string UnityProjectRoot,
    string SourceHash,
    string DraftStatus,
    int DailyQuestCount,
    int WeeklyQuestCount,
    int QuestChestRewardCount,
    int StoryQuestCount,
    int HeroDataCount = 0,
    int UnitDataCount = 0);
