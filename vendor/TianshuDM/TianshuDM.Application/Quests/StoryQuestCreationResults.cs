using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public sealed record StoryQuestCreationResult(
    StoryQuestDefinition Quest,
    StoryQuestStepDefinition Step,
    IReadOnlyList<StoryConversationTurnDefinition> Turns);

public sealed record StoryQuestStepCreationResult(
    StoryQuestStepDefinition Step,
    IReadOnlyList<StoryConversationTurnDefinition> Turns);
