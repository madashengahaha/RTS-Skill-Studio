namespace TianshuDM.Contract.Quests;

public sealed record UpdateDailyQuestRequest(
    QuestTypeRequest? Type,
    int TargetValue,
    IReadOnlyList<QuestRewardRequest>? Rewards,
    int SortOrder,
    string? Description);
