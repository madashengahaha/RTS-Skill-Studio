namespace TianshuDM.Domain.Quests;

public sealed record StoryQuestReferenceOption(
    int Id,
    string Name,
    string TableKey,
    string Category);

public sealed record StoryQuestReferenceTable(
    string TableKey,
    string DisplayName,
    IReadOnlyList<StoryQuestReferenceOption> Options);

public sealed record StoryQuestEditorMetadata(
    IReadOnlyList<LobbyCharacterReference> Characters,
    IReadOnlyList<WorldMapReference> Maps,
    IReadOnlyList<StoryQuestReferenceOption> CombatUnits,
    IReadOnlyList<StoryQuestReferenceTable> ReferenceTables);
