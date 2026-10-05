namespace TianshuDM.Domain.Quests;

public sealed record StoryConversationTurnDefinition(
    int Id,
    int DialogueId,
    int OrderIndex,
    int SpeakerType,
    string SpeakerName,
    string SpeakerImage,
    string Text);
