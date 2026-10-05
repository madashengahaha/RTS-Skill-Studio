namespace TianshuDM.Domain.Quests;

public sealed record QuestChestRewardDefinition(
    int Id,
    int RequiredCount,
    IReadOnlyList<QuestReward> Rewards,
    int ChestLevel);
