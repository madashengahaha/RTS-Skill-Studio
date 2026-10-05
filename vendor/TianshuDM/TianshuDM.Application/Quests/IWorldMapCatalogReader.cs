using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public interface IWorldMapCatalogReader
{
    IReadOnlyList<WorldMapReference> Read(string unityProjectRoot);
}
