namespace TianshuDM.Domain.Quests;

public sealed record StoryQuestDataset(
    IReadOnlyList<StoryQuestDefinition> Quests,
    IReadOnlyList<StoryQuestStepDefinition> Steps,
    IReadOnlyList<StoryConversationTurnDefinition> Turns);
