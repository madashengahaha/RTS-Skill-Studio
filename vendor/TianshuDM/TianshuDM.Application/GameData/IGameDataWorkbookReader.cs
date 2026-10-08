using TianshuDM.Domain.GameData;

namespace TianshuDM.Application.GameData;

public interface IGameDataWorkbookReader
{
    GameDataTable Read(GameDataTableSource source);
}

public interface IGameDataCatalogReader
{
    GameDataCatalog Read(string excelDataRoot);

    GameDataCatalog Read(
        string excelDataRoot,
        IReadOnlyCollection<string> tableKeys
    )
    {
        HashSet<string> selected = tableKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase
        );
        return new GameDataCatalog(
            Read(excelDataRoot)
                .Tables.Where(table => selected.Contains(table.Key))
                .ToArray()
        );
    }
}
