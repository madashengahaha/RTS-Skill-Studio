namespace TianshuDM.Domain.Quests;

public sealed record LobbyCharacterReference(
    int Id,
    string Name,
    int ModelId,
    IReadOnlyList<string> ScenePaths);
