namespace TianshuDM.Domain.Quests;

public enum StoryQuestPrerequisiteType
{
    CompleteQuest = 1,
    TransmigratorLevel = 2,
    UnlockMap = 3,
}

public sealed record StoryQuestPrerequisite(
    StoryQuestPrerequisiteType Type,
    int Value);
