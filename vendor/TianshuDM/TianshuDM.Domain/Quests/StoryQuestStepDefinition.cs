namespace TianshuDM.Domain.Quests;

public sealed record StoryQuestStepDefinition(
    int Id,
    int QuestId,
    int StepOrder,
    int NpcId,
    int AcceptDialogueId,
    int SubmitDialogueId,
    int ProcessingDialogueId,
    StoryQuestConditionType ConditionType,
    int ConditionValue,
    IReadOnlyList<QuestReward> Rewards,
    string Description = "",
    IReadOnlyList<int>? ConditionParams = null);
