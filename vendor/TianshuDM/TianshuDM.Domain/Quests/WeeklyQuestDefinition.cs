namespace TianshuDM.Domain.Quests;

public sealed record WeeklyQuestDefinition(
    int Id,
    QuestTypeValue Type,
    int TargetValue,
    IReadOnlyList<QuestReward> Rewards,
    int SortOrder,
    string Description);
