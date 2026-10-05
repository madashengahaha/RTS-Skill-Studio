namespace TianshuDM.Domain.Quests;

public sealed record DailyQuestDefinition(
    int Id,
    QuestTypeValue Type,
    int TargetValue,
    IReadOnlyList<QuestReward> Rewards,
    int SortOrder,
    string Description);
