using TianshuDM.Domain.GameData;

namespace TianshuDM.Application.GameData;

public interface IGameDataDraftStore
{
    GameDataCatalog? ReadGameDataCatalog();

    void ReplaceGameDataImport(GameDataCatalog catalog);

    void ReplaceGameDataTable(
        string tableKey,
        IReadOnlyList<GameDataRecord> records,
        string dirtyScope);

    IReadOnlyList<string> ReadDirtyGameDataTableKeys();
}

public interface IGameDataAtomicDraftStore : IGameDataDraftStore
{
    void ReplaceGameDataTables(
        IReadOnlyDictionary<string, IReadOnlyList<GameDataRecord>> replacements);
}
