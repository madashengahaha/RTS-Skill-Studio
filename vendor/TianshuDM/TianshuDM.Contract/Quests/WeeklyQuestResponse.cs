namespace TianshuDM.Contract.Quests;

public sealed record WeeklyQuestResponse(
    int Id,
    QuestTypeResponse Type,
    int TargetValue,
    IReadOnlyList<QuestRewardResponse> Rewards,
    int SortOrder,
    string Description);
