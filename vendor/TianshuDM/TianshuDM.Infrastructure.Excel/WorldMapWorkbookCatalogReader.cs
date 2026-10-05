using TianshuDM.Application.GameData;
using TianshuDM.Application.Quests;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.Quests;

namespace TianshuDM.Infrastructure.Excel;

public sealed class WorldMapWorkbookCatalogReader(IGameDataWorkbookReader workbookReader)
    : IWorldMapCatalogReader
{
    public IReadOnlyList<WorldMapReference> Read(string unityProjectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unityProjectRoot);
        string path = Path.Combine(
            Path.GetFullPath(unityProjectRoot),
            "Unity",
            "Assets",
            "Config",
            "Excel",
            "Datas",
            "WorldMap",
            "WorldMapNode.xlsx");
        if (!File.Exists(path))
        {
            return [];
        }

        GameDataTable table = workbookReader.Read(
            new GameDataTableSource(
                "world-map-node",
                "世界地图节点",
                "任务引用数据",
                "WorldMap/WorldMapNode.xlsx",
                path));
        return table.Records
            .OrderBy(record => record.Id)
            .Select(
                record => new WorldMapReference(
                    record.Id,
                    record.Fields.TryGetValue("Name", out IReadOnlyList<string>? names)
                        && names.Count > 0
                        && !string.IsNullOrWhiteSpace(names[0])
                            ? names[0]
                            : $"地图 {record.Id}"))
            .ToArray();
    }
}
