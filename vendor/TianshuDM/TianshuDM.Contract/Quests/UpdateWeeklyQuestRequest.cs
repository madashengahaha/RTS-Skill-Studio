namespace TianshuDM.Contract.Quests;

public sealed record UpdateWeeklyQuestRequest(
    QuestTypeRequest? Type,
    int TargetValue,
    IReadOnlyList<QuestRewardRequest>? Rewards,
    int SortOrder,
    string? Description);
