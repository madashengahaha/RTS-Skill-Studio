using TianshuDM.Domain.GameData;

namespace TianshuDM.Application.GameData;

public interface IGameDataWorkbookReader
{
    GameDataTable Read(GameDataTableSource source);
}

public interface IGameDataCatalogReader
{
    GameDataCatalog Read(string excelDataRoot);
}
