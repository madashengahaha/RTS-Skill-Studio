namespace TianshuDM.Contract.Quests;

public sealed record UpdateQuestChestRewardRequest(
    int RequiredCount,
    IReadOnlyList<QuestRewardRequest>? Rewards,
    int ChestLevel);
