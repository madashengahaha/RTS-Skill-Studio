namespace TianshuDM.Domain.Quests;

public sealed record StoryQuestDefinition(
    int Id,
    string Name,
    string Description,
    bool IsRepeatable,
    bool StepAutoAccept,
    IReadOnlyList<StoryQuestPrerequisite> Prerequisites);
