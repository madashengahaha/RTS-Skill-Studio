using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public interface ILobbyCharacterCatalogReader
{
    IReadOnlyList<LobbyCharacterReference> Read(string unityProjectRoot);
}
