namespace TianshuDM.Contract.Quests;

public sealed record QuestChestRewardResponse(
    int Id,
    int RequiredCount,
    IReadOnlyList<QuestRewardResponse> Rewards,
    int ChestLevel);
